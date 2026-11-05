import type { Snapshot, Events, Counter, Batch, Utc } from './contracts.ts';
export type Transport='idle'|'loading'|'connected'|'catching-up'|'resyncing'|'offline'|'unauthorized'|'forbidden'|'not-configured'|'unavailable'|'conflict';
export type Fence=Readonly<{generation:number;requestGeneration:string;scopeSelector:string|null;hostEpoch:string|null;epoch:string|null;scopeId:string|null}>;
export type NavalState=Readonly<{fence:Fence;transport:Transport;reasonCode:string|null;snapshot:Snapshot|null;eventCursor:string|null;eventSequence:Counter|null;eventRevision:Counter|null;audit:readonly Batch[];historyTruncated:boolean;effects:readonly Readonly<{id:string;kind:'publication-updated'}>[];lastResponseUtc:Utc|null}>;
export type NavalAction=Readonly<{type:'begin';fence:Fence}>|Readonly<{type:'hydrate';fence:Fence;snapshot:Snapshot}>|Readonly<{type:'page';fence:Fence;events:Events;checkpointRefresh:Snapshot|null;allowHighlight:boolean}>|Readonly<{type:'invalidate';fence:Fence;transport:Transport;reasonCode:string}>|Readonly<{type:'consume-effects';ids:readonly string[]}>;
export interface NavalStore {getSnapshot():NavalState;subscribe(listener:()=>void):()=>void;dispatch(action:NavalAction):void;}
export function initialState():NavalState{return {fence:{generation:0,requestGeneration:'',scopeSelector:null,hostEpoch:null,epoch:null,scopeId:null},transport:'idle',reasonCode:null,snapshot:null,eventCursor:null,eventSequence:null,eventRevision:null,audit:[],historyTruncated:false,effects:[],lastResponseUtc:null};}
function cleared(state:NavalState,transport:Transport,reasonCode:string):NavalState{return Object.freeze({...initialState(),fence:state.fence,transport,reasonCode,lastResponseUtc:state.lastResponseUtc});}
function sameSession(state:NavalState,f:Fence){return f.generation===state.fence.generation&&f.scopeSelector===state.fence.scopeSelector;}
function ensure(ok:unknown):asserts ok{if(!ok)throw Error('CLIENT_CONFLICT');}
function stable(v:unknown):string{if(Array.isArray(v))return '['+v.map(stable).join(',')+']';if(v&&typeof v==='object')return '{'+Object.keys(v).sort().map(k=>JSON.stringify(k)+':'+stable((v as Record<string,unknown>)[k])).join(',')+'}';return JSON.stringify(v);}
export function sameLineage(a:Snapshot,b:Snapshot|Events):boolean {if(a.hostEpoch!==b.hostEpoch||a.epoch!==b.epoch||a.scopeId!==b.scopeId||a.scope!==b.scope)return false;if('checkpoint' in b&&a.checkpoint&&b.checkpoint)return a.checkpoint.configDigest===b.checkpoint.configDigest&&stable(a.checkpoint.portfolioId)===stable(b.checkpoint.portfolioId)&&stable(a.checkpoint.accounts.map(x=>x.account))===stable(b.checkpoint.accounts.map(x=>x.account));return true;}
function forward(ag:string,as:string,bg:string,bs:string){return BigInt(bg)>BigInt(ag)||(bg===ag&&BigInt(bs)>=BigInt(as));}
export function reduceNaval(state:NavalState,action:NavalAction):NavalState{
 if(action.type==='begin'){if(!Number.isSafeInteger(action.fence.generation)||action.fence.generation<=state.fence.generation)return state;return Object.freeze({...initialState(),fence:Object.freeze({...action.fence}),transport:'loading',reasonCode:state.reasonCode==='HISTORY_DISCONTINUITY'?'HISTORY_DISCONTINUITY':null,lastResponseUtc:state.lastResponseUtc});}
 if(action.type==='consume-effects'){const effects=state.effects.filter(x=>!action.ids.includes(x.id));return effects.length===state.effects.length?state:Object.freeze({...state,effects:Object.freeze(effects)});}
 if(!sameSession(state,action.fence))return state;
 if(action.type==='invalidate')return cleared(state,action.transport,action.reasonCode);
 if(action.type==='hydrate'){
  const s=action.snapshot;
  if(s.availability!=='AVAILABLE')return {...cleared(state,s.availability==='NOT_CONFIGURED'?'not-configured':'unavailable',s.reasonCode),lastResponseUtc:s.servedUtc};
  if(state.snapshot||!s.checkpoint||!s.cursor||action.fence.scopeSelector!==null&&s.scope!==action.fence.scopeSelector)return cleared(state,'conflict','CLIENT_IDENTITY_CONFLICT');
  return Object.freeze({...state,fence:Object.freeze({...action.fence,hostEpoch:s.hostEpoch,epoch:s.epoch,scopeId:s.scopeId}),transport:'connected',snapshot:s,eventCursor:s.cursor,eventSequence:s.asOfSequence,eventRevision:s.financialRevision,audit:Object.freeze([]),effects:Object.freeze([]),lastResponseUtc:s.servedUtc});
 }
 const e=action.events;if(!state.snapshot||!state.eventSequence||!state.eventRevision)return state;
 if(e.gap||e.resetRequired||e.hostEpoch!==state.snapshot.hostEpoch||e.epoch!==state.snapshot.epoch)return cleared(state,'resyncing','HISTORY_DISCONTINUITY');
 try{
  ensure(e.page&&e.nextCursorEncoded&&sameLineage(state.snapshot,e)&&e.availability==='AVAILABLE');
  const retained=new Map(state.audit.map(b=>[b.eventId,b]));const fills=new Map<string,string>();const facts=new Map<string,string>();
  for(const b of state.audit)for(const f of b.facts){facts.set(f.factId,stable(f));if(f.fillKey)fills.set(f.fillKey,f.factId);}
  const added:Batch[]=[];let sequence=BigInt(state.eventSequence),revision=BigInt(state.eventRevision);let source=state.audit.at(-1)??state.snapshot;
  ensure(BigInt(e.financialRevision!)>=revision&&BigInt(e.asOfSequence!)>=sequence&&forward(source.sourceGeneration!,source.sourceSequence!,e.sourceGeneration!,e.sourceSequence!));
  const members=new Set(state.snapshot.checkpoint!.accounts.map(a=>stable(a.account)));
  for(const b of e.batches){const old=retained.get(b.eventId);if(old){ensure(stable(old)===stable(b));continue;}
   // A response older than retained history cannot reintroduce an evicted fact.
   if(BigInt(b.sequence)<=sequence)return state;
   ensure(BigInt(b.sequence)===sequence+1n&&BigInt(b.financialRevision)>revision&&forward(source.sourceGeneration!,source.sourceSequence!,b.sourceGeneration,b.sourceSequence));
   for(const f of b.facts){ensure(!f.account||members.has(stable(f.account)));const oldFact=facts.get(f.factId);ensure(!oldFact||oldFact===stable(f));if(f.fillKey){ensure(!fills.has(f.fillKey)||fills.get(f.fillKey)===f.factId);fills.set(f.fillKey,f.factId);}facts.set(f.factId,stable(f));}
   added.push(b);sequence=BigInt(b.sequence);revision=BigInt(b.financialRevision);source=b;
  }
  if(e.batches.length&&!added.length)return state;
  ensure(e.page.nextCursor.sequence===sequence.toString());
  let snapshot=state.snapshot;const fresh=action.checkpointRefresh;
  if(fresh){ensure(fresh.availability==='AVAILABLE'&&fresh.checkpoint&&sameLineage(snapshot,fresh)&&BigInt(fresh.financialRevision!)>=BigInt(snapshot.financialRevision!)&&BigInt(fresh.asOfSequence!)>=BigInt(snapshot.asOfSequence!)&&forward(snapshot.sourceGeneration!,snapshot.sourceSequence!,fresh.sourceGeneration!,fresh.sourceSequence!));if(fresh.financialRevision===snapshot.financialRevision)ensure(stable(fresh.checkpoint)===stable(snapshot.checkpoint));snapshot=fresh;}
  ensure(BigInt(snapshot.financialRevision!)>=BigInt(e.financialRevision!));
  const all=[...state.audit,...added],audit=Object.freeze(all.slice(-512));const last=added.at(-1);
  return Object.freeze({...state,fence:Object.freeze({...state.fence,requestGeneration:action.fence.requestGeneration}),transport:e.page.hasMore?'catching-up':'connected',snapshot,eventCursor:e.nextCursorEncoded,eventSequence:sequence.toString() as Counter,eventRevision:revision.toString() as Counter,audit,historyTruncated:state.historyTruncated||all.length>512,effects:Object.freeze(last&&action.allowHighlight&&!e.page.hasMore?[{id:last.eventId,kind:'publication-updated' as const}]:[]),lastResponseUtc:e.servedUtc});
 }catch{return cleared(state,'conflict','CLIENT_PUBLICATION_CONFLICT');}
}
export function createNavalStore():NavalStore{let state=initialState();const listeners=new Set<()=>void>();return {getSnapshot:()=>state,subscribe(fn){listeners.add(fn);return ()=>{listeners.delete(fn);};},dispatch(a){const next=reduceNaval(state,a);if(next!==state){state=next;listeners.forEach(fn=>fn());}}};}
