import {useState} from 'react';
import {selectFleets} from '../read/selectors.ts';import type {Fleet} from '../read/contracts.ts';
import {ReservationPage} from '../ui/ReservationPage.tsx';import {Unknown,matches} from '../ui/Truth.tsx';import type {ViewProps} from '../ui/Truth.tsx';
export function FleetOperations({state,account,visual}:ViewProps){
 const v=selectFleets(state),[fleet,setFleet]=useState<Fleet>('Alpha');
 const f=v.fleets.find(x=>x.fleet===fleet),reservations=f?.reservations.filter(r=>matches(r.reservation.account,account))??[],fills=f?.fills.filter(r=>matches(r.fill.account,account))??[];
 const key=JSON.stringify([state.fence.generation,state.snapshot?.hostEpoch,state.snapshot?.epoch,state.snapshot?.scopeId,state.snapshot?.checkpoint?.configDigest,fleet,account]);
 return <><p className="eyebrow">03 / FLEET RECORDS</p><h1>Fleet Operations</h1><div className="tabs">{(['Alpha','Bravo'] as const).map(s=><button key={s} aria-pressed={fleet===s} onClick={()=>setFleet(s)}>{s==='Alpha'?'ALFA':'BRAVO'}</button>)}</div><div className="mission-grid"><section className="dominant">{v.checkpoint?<ReservationPage key={key} kind="fleet" state={state} visual={visual} accountKey={account} fleet={fleet} asset="*" onSelectAsset={()=>{}} reservations={reservations} fills={fills}/>:<Unknown label="Rezerwacje / fille checkpointu" why="Brak zaakceptowanego checkpointu; liczby zapisów niedostępne."/>}</section><aside className="evidence"><Unknown label="Budżet / settings / contribution" why="Configuration not published. Brak osobnego salda floty; cash portfela nie jest dublowany."/><Unknown label="Broker orders / protection" why="Requested SL/TP to zamiar Offline, nie zweryfikowana ochrona."/></aside></div></>;
}
