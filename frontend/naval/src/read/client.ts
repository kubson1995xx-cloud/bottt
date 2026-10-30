import type { NavalStore, Fence, Transport } from './store.ts';
import type { Snapshot, Events } from './contracts.ts';
import { decodeSnapshot, decodeEvents, ContractError, requireContract } from './decode.ts';
export interface ClientDeps {fetch:typeof fetch;now:()=>number;setTimer:(fn:()=>void,milliseconds:number)=>unknown;clearTimer:(handle:unknown)=>void;}
export interface NavalClient {start(scopeSelector:string|null):void;resync():void;setVisible(visible:boolean):void;setEffectsEnabled(enabled:boolean):void;dispose():void;}
class ReadFailure extends Error { readonly transport:Transport;readonly code:string;readonly retry:boolean;constructor(transport:Transport,code:string,retry=true){super(code);this.transport=transport;this.code=code;this.retry=retry;} }
export function createNavalClient(store:NavalStore,deps:ClientDeps):NavalClient{
 let generation=store.getSnapshot().fence.generation,request=0,selector:string|null=null,visible=true,active=false,effects=false,backoff=0,pages=0,continuous=false;
 let controller:AbortController|null=null;let timer:unknown=null;const deadlines=new Set<unknown>();
 const live=(g:number)=>active&&visible&&generation===g;
 const fence=(token=''):Fence=>({...store.getSnapshot().fence,generation,scopeSelector:selector,requestGeneration:token});
 function cancel(){controller?.abort();controller=null;if(timer!==null)deps.clearTimer(timer);timer=null;for(const t of deadlines)deps.clearTimer(t);deadlines.clear();}
 function advance(){if(!Number.isSafeInteger(generation+1))throw new ContractError('CLIENT_GENERATION_EXHAUSTED');generation++;cancel();}
 function schedule(fn:()=>void,ms:number){if(!active||!visible)return;const g=generation;if(timer!==null)deps.clearTimer(timer);timer=deps.setTimer(()=>{timer=null;if(live(g))fn();},ms);}
 async function get<T extends Snapshot|Events>(kind:'snapshot'|'events',g:number,decode:(v:unknown)=>T,position?:string):Promise<{body:T;token:string}>{
  if(!live(g))throw new ContractError('STALE_RESPONSE');if(!Number.isSafeInteger(request+1))throw new ContractError('CLIENT_REQUEST_EXHAUSTED');const token=`g${g}r${++request}`;requireContract(token.length<=64);
  const q=new URLSearchParams({requestGeneration:token});const pinned=selector??store.getSnapshot().snapshot?.scope;if(pinned!==undefined&&pinned!==null)q.set('scope',pinned);
  if(kind==='events'){const s=store.getSnapshot();requireContract(s.snapshot&&position);q.set('hostEpoch',s.snapshot.hostEpoch);q.set('cursor',position);q.set('limit','100');}
  const ac=new AbortController();controller=ac;let deadline:unknown;
  const timeout=new Promise<never>((_,reject)=>{deadline=deps.setTimer(()=>{ac.abort();reject(new ReadFailure('offline','READ_TIMEOUT'));},10000);deadlines.add(deadline);});
  try{
   const operation=(async()=>{const r=await deps.fetch(`/api/naval/${kind}?${q}`,{method:'GET',credentials:'same-origin',cache:'no-store',signal:ac.signal});
    if(!live(g))throw new ContractError('STALE_RESPONSE');
    if(r.status===401)throw new ReadFailure('unauthorized','OPERATOR_AUTHENTICATION_REQUIRED',false);
    if(r.status===403)throw new ReadFailure('forbidden','ACCESS_DENIED',false);
    if(r.status===400)throw new ReadFailure('unavailable','CLIENT_CONTRACT_ERROR',false);
    if(r.status===409)throw new ReadFailure('resyncing','HISTORY_DISCONTINUITY');
    if(!r.ok){let reason='READ_UNAVAILABLE';try{const v=await r.json();if(['PUBLICATION_CONFLICT','PUBLICATION_BACKWARD','PUBLICATION_LINEAGE_CHANGED','PUBLICATION_INVALID','PUBLICATION_MISSING','PUBLICATION_TOO_LARGE'].includes(v?.reasonCode))reason=v.reasonCode;}catch{}throw new ReadFailure(reason==='PUBLICATION_CONFLICT'||reason==='PUBLICATION_BACKWARD'||reason==='PUBLICATION_LINEAGE_CHANGED'?'conflict':'unavailable',reason);}
    const length=r.headers.get('content-length');if(length&&Number(length)>32*1024*1024)throw new ContractError('RESPONSE_TOO_LARGE');
    // Bound bytes before JSON.parse, including unknown/chunked content lengths.
    const reader=r.body?.getReader();requireContract(reader);const chunks:Uint8Array[]=[];let size=0;
    while(true){const {done,value}=await reader.read();if(!live(g)){await reader.cancel();throw new ContractError('STALE_RESPONSE');}if(done)break;size+=value.byteLength;if(size>32*1024*1024){await reader.cancel();throw new ContractError('RESPONSE_TOO_LARGE');}chunks.push(value);}
    const bytes=new Uint8Array(size);let at=0;for(const c of chunks){bytes.set(c,at);at+=c.length;}
    let raw:unknown;try{raw=JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes));}catch{throw new ContractError('JSON_INVALID');}
    const body=decode(raw);requireContract(body.requestGeneration===token&&(pinned==null||body.scope===pinned),'REQUEST_IDENTITY_MISMATCH');if(!live(g))throw new ContractError('STALE_RESPONSE');
    if(kind==='events')requireContract(store.getSnapshot().eventCursor===position,'STALE_CONTINUATION');return {body,token};})();
   return await Promise.race([operation,timeout]);
  }finally{deps.clearTimer(deadline);deadlines.delete(deadline);if(controller===ac)controller=null;}
 }
 function fail(error:unknown,g:number){if(!live(g))return;const failure=error instanceof ReadFailure?error:error instanceof ContractError?new ReadFailure('unavailable',error.code):new ReadFailure('offline','NETWORK_UNAVAILABLE');
  advance();continuous=false;store.dispatch({type:'begin',fence:fence()});store.dispatch({type:'invalidate',fence:fence(),transport:failure.transport,reasonCode:failure.code});
  if(failure.retry){const ms=failure.transport==='resyncing'?0:failure.transport==='conflict'?30000:[2000,4000,8000,16000,30000][Math.min(backoff++,4)];schedule(()=>begin(selector),ms);}
 }
 async function snapshot(g:number){try{const {body,token}=await get('snapshot',g,decodeSnapshot);if(!live(g))return;store.dispatch({type:'hydrate',fence:fence(token),snapshot:body});backoff=0;pages=0;
  if(body.availability==='AVAILABLE'){schedule(()=>poll(g),0);}else schedule(()=>begin(selector),30000);
 }catch(e){fail(e,g);}}
 async function poll(g:number){const position=store.getSnapshot().eventCursor;if(!position)return;try{
  const {body,token}=await get('events',g,decodeEvents,position);if(!live(g))return;
  if(body.gap||body.resetRequired){throw new ReadFailure('resyncing','HISTORY_DISCONTINUITY');}
  const current=store.getSnapshot().snapshot;requireContract(current&&body.financialRevision);
  let fresh:Snapshot|null=null;if(BigInt(body.financialRevision)>BigInt(current.financialRevision!)){fresh=(await get('snapshot',g,decodeSnapshot)).body;if(!live(g))return;}
  store.dispatch({type:'page',fence:fence(token),events:body,checkpointRefresh:fresh,allowHighlight:effects&&continuous&&!body.page?.hasMore});
  const state=store.getSnapshot();if(state.transport==='conflict')throw new ReadFailure('conflict',state.reasonCode??'CLIENT_PUBLICATION_CONFLICT');if(state.transport==='resyncing')throw new ReadFailure('resyncing','HISTORY_DISCONTINUITY');
  backoff=0;if(body.page?.hasMore){continuous=false;pages++;schedule(()=>poll(g),pages%10===0?100:0);}else{continuous=true;pages=0;schedule(()=>poll(g),2000);}
 }catch(e){fail(e,g);}}
 function begin(next:string|null){selector=next;active=true;advance();continuous=false;store.dispatch({type:'begin',fence:fence()});if(visible)void snapshot(generation);}
 return {start:begin,resync:()=>begin(selector),setVisible(v){if(visible===v)return;visible=v;if(v&&active)begin(selector);else{advance();continuous=false;store.dispatch({type:'begin',fence:fence()});store.dispatch({type:'invalidate',fence:fence(),transport:'idle',reasonCode:'DOCUMENT_HIDDEN'});}},setEffectsEnabled(v){effects=v;if(!v)store.dispatch({type:'consume-effects',ids:store.getSnapshot().effects.map(e=>e.id)});},dispose(){active=false;advance();continuous=false;store.dispatch({type:'begin',fence:fence()});store.dispatch({type:'invalidate',fence:fence(),transport:'idle',reasonCode:'CLIENT_DISPOSED'});}};
}
