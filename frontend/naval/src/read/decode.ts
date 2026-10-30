import type { Snapshot, Events, Cursor, Checkpoint, Fact, ReadReservation, ReadFill } from './contracts.ts';
import { parseCounter, parseDecimal } from './decimal.ts';
export class ContractError extends Error { readonly code: string; constructor(code: string) { super(code);this.code=code; } }
export function requireContract(ok: unknown,code='CONTRACT_INVALID'): asserts ok { if(!ok)throw new ContractError(code); }
export function canonical(value: unknown): string {
 if(Array.isArray(value))return '['+value.map(canonical).join(',')+']';
 if(value!==null&&typeof value==='object')return '{'+Object.keys(value).sort().map(k=>JSON.stringify(k)+':'+canonical((value as Record<string,unknown>)[k])).join(',')+'}';
 return JSON.stringify(value);
}
type V=(v:unknown)=>void;
const text:V=v=>requireContract(typeof v==='string'&&v.length>0&&v.length<=4096&&!/[\u0000-\u001f]/.test(v));
const code:V=v=>requireContract(typeof v==='string'&&/^[A-Z0-9_]{1,128}$/.test(v));
const bool:V=v=>requireContract(typeof v==='boolean');
const literal=(...allowed:unknown[]):V=>v=>requireContract(allowed.includes(v));
const nullable=(f:V):V=>v=>{if(v!==null)f(v);};
const list=(f:V):V=>v=>{requireContract(Array.isArray(v));v.forEach(f);};
const object=(fields:Record<string,V>):V=>v=>{requireContract(v!==null&&typeof v==='object'&&!Array.isArray(v));const r=v as Record<string,unknown>;requireContract(Object.keys(r).length===Object.keys(fields).length);for(const [k,f]of Object.entries(fields)){requireContract(Object.hasOwn(r,k));f(r[k]);}};
const counter:V=v=>{try{parseCounter(v);}catch{throw new ContractError('COUNTER_INVALID');}};
const decimal:V=v=>{try{parseDecimal(v);}catch{throw new ContractError('DECIMAL_INVALID');}};
const guid:V=v=>requireContract(typeof v==='string'&&/^[0-9a-fA-F]{32}$/.test(v));
const digest:V=v=>requireContract(typeof v==='string'&&/^[0-9a-fA-F]{64}$/.test(v));
const utc:V=v=>{requireContract(typeof v==='string'&&/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|\+00:00)$/.test(v));const d=new Date(v);requireContract(Number.isFinite(d.getTime())&&d.toISOString().slice(0,19)===v.slice(0,19));};
const account=object({venue:text,environment:literal('Offline'),accountId:text,quoteCurrency:text});
const portfolio=object({portfolioId:text,reportingCurrency:text});
const money=object({amount:decimal,currency:text});const fleet=literal('Alpha','Bravo'),mode=literal('STOP','RUNNING');
const intent=object({commandId:text,signalId:text,instrument:text,quantity:decimal,entryPriceCeiling:decimal,stopPrice:decimal,takeProfitPrice:decimal});
const reservation=object({id:text,account,fleet,intent,quantity:decimal,pending:decimal,filled:decimal,inventory:decimal,allocatedCost:decimal,entryHold:decimal,closeFeeHold:decimal,riskHold:decimal,terminalCoverage:bool,reductionUnsettled:bool,stage:literal('Reserved','Confirmed','Uncertain','Rejected','Terminal')});
const readReservationShape=object({reservation,baseCurrency:text,quoteCurrency:text});
const readReservation:V=v=>{readReservationShape(v);const r=v as ReadReservation;requireContract(r.quoteCurrency===r.reservation.account.quoteCurrency);};
const fill=object({account,instrument:text,tradeId:text,reservationId:text,reduction:bool,quantity:decimal,price:decimal,fee:decimal,feeCurrency:text,tradeUtc:utc});
const readFillShape=object({key:text,fill,baseCurrency:text,quoteCurrency:text});
const readFill:V=v=>{readFillShape(v);const r=v as ReadFill;requireContract(r.quoteCurrency===r.fill.account.quoteCurrency&&[r.baseCurrency,r.quoteCurrency].includes(r.fill.feeCurrency));};
const operation=object({resultCode:code,durableMode:mode,dailyLatch:bool,drawdownLatch:bool,emergencyLatch:bool});
const coverage=object({complete:bool,final:bool,terminalCoverage:bool,reductionUnsettled:bool,offlineLifecycleFinal:bool});
const nil=literal(null),unknown=literal('UNKNOWN'),unconfigured=literal('NOT_CONFIGURED');
const checkpointShape=object({financialRevision:counter,portfolioId:portfolio,configDigest:digest,sourceGeneration:counter,sourceSequence:counter,reconciledUtc:nullable(utc),durableMode:mode,reasonCode:code,dailyLatch:bool,drawdownLatch:bool,emergencyLatch:bool,historicalLiquidationEquity:nullable(money),currentQuality:unknown,runtimeReadiness:unknown,brokerState:unconfigured,coverage:unknown,brokerAccountId:nil,brokerEquity:nil,realizedPnl:nil,unrealizedPnl:nil,accounts:list(object({account,seed:money,cash:money})),fleets:list(object({fleet,reservations:list(readReservation),fills:list(readFill)}))});
function unique(values:readonly string[]){requireContract(new Set(values).size===values.length);}
const checkpoint:V=v=>{checkpointShape(v);const c=v as Checkpoint;requireContract(c.accounts.length>0&&c.fleets.length===2);unique(c.accounts.map(x=>canonical(x.account)));unique(c.fleets.map(x=>x.fleet));const members=new Set(c.accounts.map(x=>canonical(x.account)));const reservations=new Set<string>(),fills=new Set<string>();
 for(const a of c.accounts)requireContract(a.cash.currency===a.account.quoteCurrency&&a.seed.currency===a.account.quoteCurrency);
 for(const f of c.fleets){for(const r of f.reservations){requireContract(r.reservation.fleet===f.fleet&&members.has(canonical(r.reservation.account))&&!reservations.has(r.reservation.id));reservations.add(r.reservation.id);}for(const r of f.fills){requireContract(members.has(canonical(r.fill.account))&&!fills.has(r.key));fills.add(r.key);}}
 for(const f of c.fleets)for(const r of f.fills)requireContract(f.reservations.some(x=>x.reservation.id===r.fill.reservationId&&canonical(x.reservation.account)===canonical(r.fill.account)&&x.reservation.intent.instrument===r.fill.instrument));
 if(c.historicalLiquidationEquity)requireContract(c.historicalLiquidationEquity.currency===c.portfolioId.reportingCurrency);
};
const factShape=object({kind:literal('OfflineReservationAccepted','OfflineReservationRefused','OfflineFillAccepted','OfflineEntryCoverageAccepted','OfflineReductionCoverageAccepted','OfflineReconciliationAccepted','OfflineSafetyChanged','OfflineModeChanged'),factId:text,account:nullable(account),fleet:nullable(fleet),reservationId:nullable(text),fillKey:nullable(text),reservation:nullable(readReservation),fill:nullable(readFill),coverage:nullable(coverage),operation:nullable(operation),intent:nullable(intent)});
const fact:V=v=>{factShape(v);const f=v as Fact;
 const expected=f.kind==='OfflineReservationAccepted'?['reservation','operation','intent']:f.kind==='OfflineReservationRefused'?['operation','intent']:f.kind==='OfflineFillAccepted'?['fill']:f.kind==='OfflineEntryCoverageAccepted'||f.kind==='OfflineReductionCoverageAccepted'?['reservation','coverage']:['operation'];
 for(const k of ['reservation','operation','intent','fill','coverage'] as const)requireContract((f[k]!==null)===expected.includes(k));
 if(f.kind==='OfflineReconciliationAccepted'||f.kind==='OfflineSafetyChanged'||f.kind==='OfflineModeChanged')requireContract(f.account===null&&f.fleet===null&&f.reservationId===null&&f.fillKey===null);
 else requireContract(f.account!==null&&f.fleet!==null);
 if(f.reservation)requireContract(f.reservationId===f.reservation.reservation.id&&canonical(f.account)===canonical(f.reservation.reservation.account)&&f.fleet===f.reservation.reservation.fleet&&f.fillKey===null);
 if(f.fill)requireContract(f.fillKey===f.fill.key&&f.reservationId===f.fill.fill.reservationId&&canonical(f.account)===canonical(f.fill.fill.account));
 if(f.kind==='OfflineReservationAccepted')requireContract(canonical(f.intent)===canonical(f.reservation.reservation.intent));
 if(f.kind==='OfflineReservationRefused')requireContract(f.reservationId===null&&f.fillKey===null);
};
const batch=object({sequence:counter,eventId:text,financialRevision:counter,captureUtc:nullable(utc),receiveUtc:nullable(utc),committedUtc:utc,sourceGeneration:counter,sourceSequence:counter,facts:list(fact)});
const cursor=object({epoch:guid,scopeId:digest,sequence:counter});
const page=object({epoch:guid,scopeId:digest,oldestAvailableSequence:counter,asOfSequence:counter,nextCursor:cursor,hasMore:bool,gap:bool,resetRequired:bool,batches:list(batch)});
const source=object({kind:literal('OFFLINE_PUBLICATION'),sourceClockUtc:nullable(utc),captureUtc:nullable(utc),receiveUtc:nullable(utc),reconciledUtc:nullable(utc),committedUtc:nullable(utc),ageSeconds:nullable(decimal),quality:unknown,coverage:unknown,freshness:literal('HISTORICAL','UNKNOWN'),stale:literal(true),reasonCode:code});
const broker=object({state:unconfigured,accountId:nil,balance:nil,equity:nil,realizedPnl:nil,unrealizedPnl:nil,statistics:nil,quality:unknown,coverage:unknown,runtimeReadiness:unknown});
const envelope={schemaVersion:literal('naval-host-read-v1'),hostEpoch:guid,scope:(v:unknown)=>requireContract(typeof v==='string'&&/^[a-zA-Z0-9_-]{1,64}$/.test(v)),requestGeneration:nullable(text),servedUtc:utc,observedUtc:nullable(utc),availability:literal('AVAILABLE','NOT_CONFIGURED','UNKNOWN'),reasonCode:code,temporalClassification:literal('HISTORICAL_OFFLINE_CHECKPOINT'),classification:literal('OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY'),epoch:nullable(guid),scopeId:nullable(digest),sourceGeneration:nullable(counter),sourceSequence:nullable(counter),snapshotVersion:nullable(counter),asOfSequence:nullable(counter),financialRevision:nullable(counter),sources:list(source),broker};
function freeze<T>(v:T):T {if(v!==null&&typeof v==='object'){Object.values(v).forEach(freeze);Object.freeze(v);}return v;}
function admitted<T>(v:unknown,shape:V,refine:(v:T)=>void):T {try{const detached=structuredClone(v);shape(detached);refine(detached as T);return freeze(detached as T);}catch(e){if(e instanceof ContractError)throw e;throw new ContractError('CONTRACT_INVALID');}}
function envelopeIdentity(s:Snapshot|Events){for(const k of ['epoch','scopeId','sourceGeneration','sourceSequence','snapshotVersion','asOfSequence','financialRevision'] as const)requireContract((s[k]!==null)===(s.availability==='AVAILABLE'));if(s.availability==='AVAILABLE')requireContract(s.snapshotVersion===s.financialRevision);}
export function decodeSnapshot(value:unknown):Snapshot {return admitted<Snapshot>(value,object({...envelope,checkpoint:nullable(checkpoint),oldestAvailableSequence:nullable(counter),cursor:nullable(text)}),s=>{
 envelopeIdentity(s);if(s.availability!=='AVAILABLE'){requireContract(s.checkpoint===null&&s.cursor===null&&s.oldestAvailableSequence===null);return;}
 requireContract(s.checkpoint&&s.cursor&&s.oldestAvailableSequence);const c=decodeCursor(s.cursor);requireContract(c.epoch===s.epoch&&c.scopeId===s.scopeId&&c.sequence===s.asOfSequence&&s.financialRevision===s.checkpoint.financialRevision&&s.sourceGeneration===s.checkpoint.sourceGeneration&&s.sourceSequence===s.checkpoint.sourceSequence&&BigInt(s.oldestAvailableSequence)<=BigInt(c.sequence)+1n);
 });}
export function decodeEvents(value:unknown):Events {return admitted<Events>(value,object({...envelope,page:nullable(page),nextCursorEncoded:nullable(text),gap:bool,resetRequired:bool,batches:list(batch)}),e=>{
 envelopeIdentity(e);if(!e.page){requireContract(e.batches.length===0&&e.nextCursorEncoded===null&&e.resetRequired);return;}
 const p=e.page;requireContract(e.availability==='AVAILABLE'&&p.epoch===e.epoch&&p.scopeId===e.scopeId&&p.asOfSequence===e.asOfSequence&&canonical(p.batches)===canonical(e.batches)&&p.gap===e.gap&&p.resetRequired===e.resetRequired&&e.nextCursorEncoded);
 requireContract(canonical(decodeCursor(e.nextCursorEncoded))===canonical(p.nextCursor)&&p.nextCursor.epoch===e.epoch&&p.nextCursor.scopeId===e.scopeId);
 if(p.gap||p.resetRequired)requireContract(e.batches.length===0);
 let prev:bigint|null=null,rev:bigint|null=null;for(const b of e.batches){requireContract(b.eventId===e.epoch+':'+b.sequence&&(prev===null||BigInt(b.sequence)===prev+1n)&&BigInt(b.sequence)<=BigInt(e.asOfSequence!)&&(rev===null||BigInt(b.financialRevision)>rev)&&BigInt(b.financialRevision)<=BigInt(e.financialRevision!));b.facts.forEach((f,i)=>requireContract(f.factId===b.eventId+':'+i));prev=BigInt(b.sequence);rev=BigInt(b.financialRevision);}
 if(e.batches.length)requireContract(p.nextCursor.sequence===e.batches.at(-1)!.sequence);
 if(!p.gap&&!p.resetRequired)requireContract(p.hasMore===(BigInt(p.nextCursor.sequence)<BigInt(p.asOfSequence)));
 });}
export function decodeCursor(value:string):Cursor {
 try{requireContract(value.length>0&&value.length<=2048&&/^[A-Za-z0-9_-]+$/.test(value));const raw=atob(value.replace(/-/g,'+').replace(/_/g,'/'));requireContract(btoa(raw).replace(/=/g,'').replace(/\+/g,'-').replace(/\//g,'_')===value);const tuple:unknown=JSON.parse(raw);requireContract(Array.isArray(tuple)&&tuple.length===4&&tuple[0]==='naval-cursor-v1'&&JSON.stringify(tuple)===raw);const c={epoch:tuple[1],scopeId:tuple[2],sequence:tuple[3]};cursor(c);return freeze(c as Cursor);}catch{throw new ContractError('CURSOR_INVALID');}
}
