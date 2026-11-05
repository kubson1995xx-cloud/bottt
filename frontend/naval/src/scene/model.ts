import {canonical} from '../read/decode.ts';
import {plannedRoster} from '../read/selectors.ts';
import type {NavalState} from '../read/store.ts';
import type {ReadReservation,Fleet,Counter} from '../read/contracts.ts';
export type VisualPrefs=Readonly<{reduced:boolean;minimal:boolean;mute:boolean}>;
export type SceneInput=Readonly<{kind:'fleet'|'radar';state:NavalState;accountKey:string;fleet:Fleet|null;asset:string;page:number;visible:readonly ReadReservation[];totalCount:number;selectedId:string|null}>;
export type SceneUnit=Readonly<{id:string;accountKey:string;fleet:Fleet;instrument:string;baseCurrency:string;stage:ReadReservation['reservation']['stage'];uncertain:boolean;terminalCoverage:boolean;reductionUnsettled:boolean}>;
export type SceneFrame=Readonly<{sessionKey:string|null;pageKey:string;kind:'fleet'|'radar';revision:Counter|null;auditThrough:Counter|null;units:readonly SceneUnit[];totalCount:number|null;selectedId:string|null;plannedAssets:readonly string[];classification:string;available:boolean;stale:true;broker:'NOT_CONFIGURED'}>;
export type SceneCue=Readonly<{sessionKey:string;pageKey:string;eventId:string;kind:'historical-publication';unitIds:readonly string[];startedAtMs:number;durationMs:600}>;
export type CueState=Readonly<{sessionKey:string|null;pageKey:string;through:Counter|null;active:SceneCue|null}>;
export const sceneBudget=Object.freeze({units:8,stations:10,fps:30,publicationMs:600,selectionMs:180,geometries:24,materials:12,textures:0,calls:64,triangles:20000});
export function projectScene(input:SceneInput):SceneFrame{
 const {state}=input,s=state.snapshot,c=s?.checkpoint;
 const sessionKey=c&&s?JSON.stringify([state.fence.generation,s.hostEpoch,s.epoch,s.scopeId,s.scope,c.configDigest]):null;
 const pageKey=JSON.stringify([sessionKey,input.kind,input.accountKey,input.fleet,input.asset,input.page]);
 const all=c?.fleets.flatMap(f=>f.reservations).filter(r=>(!input.fleet||r.reservation.fleet===input.fleet)&&(!input.accountKey||canonical(r.reservation.account)===input.accountKey)&&(input.asset==='*'||r.baseCurrency===input.asset))??[];
 const expected=all.slice(input.page*8,input.page*8+8);
 const available=!!c&&s?.availability==='AVAILABLE'&&Number.isSafeInteger(input.page)&&input.page>=0&&input.page<Math.max(1,Math.ceil(all.length/8))&&input.totalCount===all.length&&input.visible.length<=8&&new Set(input.visible.map(r=>r.reservation.id)).size===input.visible.length&&canonical(input.visible)===canonical(expected);
 const units:readonly SceneUnit[]=available?input.visible.map(r=>({id:r.reservation.id,accountKey:canonical(r.reservation.account),fleet:r.reservation.fleet,instrument:r.reservation.intent.instrument,baseCurrency:r.baseCurrency,stage:r.reservation.stage,uncertain:r.reservation.stage==='Uncertain'||r.reservation.reductionUnsettled,terminalCoverage:r.reservation.terminalCoverage,reductionUnsettled:r.reservation.reductionUnsettled})):[];
 return {sessionKey,pageKey,kind:input.kind,revision:s?.financialRevision??null,auditThrough:state.eventSequence,units,totalCount:available?all.length:null,selectedId:units.some(u=>u.id===input.selectedId)?input.selectedId:null,plannedAssets:input.kind==='radar'?plannedRoster:[],classification:s?.classification??'UNKNOWN',available,stale:true,broker:'NOT_CONFIGURED'};
}
export function stepSceneCue(previous:CueState|null,input:SceneInput,enabled:boolean,nowMs:number):CueState{
 const f=projectScene(input),s=input.state,through=s.eventSequence;
 const baseline:CueState={sessionKey:f.sessionKey,pageKey:f.pageKey,through,active:null};
 if(!enabled||!f.available||!f.sessionKey||s.transport!=='connected'||!previous||previous.sessionKey!==f.sessionKey||previous.pageKey!==f.pageKey||!through||!previous.through||BigInt(through)<BigInt(previous.through))return baseline;
 let active=previous.active&&nowMs>=previous.active.startedAtMs&&nowMs-previous.active.startedAtMs<600?previous.active:null;
 for(const effect of s.effects){
  const b=s.audit.find(b=>b.eventId===effect.id);
  if(!b||BigInt(b.sequence)<=BigInt(previous.through)||BigInt(b.sequence)>BigInt(through))continue;
  // Only the admitted effect + retained real facts may start neutral historical motion.
  if(!b.facts.length)continue;
  const unitIds=f.units.filter(u=>b.facts.some(fact=>fact.reservationId===u.id&&fact.account!==null&&canonical(fact.account)===u.accountKey&&fact.fleet===u.fleet)).map(u=>u.id);
  active={sessionKey:f.sessionKey,pageKey:f.pageKey,eventId:b.eventId,kind:'historical-publication',unitIds,startedAtMs:nowMs,durationMs:600};
 }
 return {...baseline,active};
}
