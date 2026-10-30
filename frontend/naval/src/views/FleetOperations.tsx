import {useState} from 'react';
import {selectFleets} from '../read/selectors.ts';
import type {ReadReservation,ReadFill} from '../read/contracts.ts';
import {FleetGlyph} from '../ui/FleetGlyph.tsx';
import {DiagramBoundary,ReservationRows,FillRows,Unknown,matches} from '../ui/Truth.tsx';
import type {ViewProps} from '../ui/Truth.tsx';

// One page and one reservation ID drive both representations. Filter identity
// remounts this owner; an unavailable checkpoint removes it, not a zero baseline.
function FleetPage({fleet,reservations,fills}:{fleet:string;reservations:readonly ReadReservation[];fills:readonly ReadFill[]}){
 const [page,setPage]=useState(0),[selected,setSelected]=useState<string|null>(null);
 const pages=Math.max(1,Math.ceil(reservations.length/8)),p=Math.min(page,pages-1);
 const visible=reservations.slice(p*8,p*8+8);
 const selectedId=visible.some(r=>r.reservation.id===selected)?selected:null;
 const select=(id:string)=>setSelected(selectedId===id?null:id);
 const changePage=(value:number)=>{setPage(value);setSelected(null);};
 return <><DiagramBoundary><FleetGlyph fleet={fleet} reservations={visible} totalCount={reservations.length} selectedId={selectedId} onSelect={select}/></DiagramBoundary><p>{reservations.length} rezerwacji / {fills.length} filli w checkpointcie i filtrze.</p><ReservationRows items={visible} pagination={{page:p,pages,setPage:changePage,total:reservations.length}} selection={{id:selectedId,onSelect:select}}/><FillRows items={fills}/></>;
}
export function FleetOperations({state,account}:ViewProps){
 const v=selectFleets(state),[fleet,setFleet]=useState('Alpha');
 const f=v.fleets.find(x=>x.fleet===fleet),reservations=f?.reservations.filter(r=>matches(r.reservation.account,account))??[],fills=f?.fills.filter(r=>matches(r.fill.account,account))??[];
 return <><p className="eyebrow">03 / FLEET RECORDS</p><h1>Fleet Operations</h1><div className="tabs">{['Alpha','Bravo'].map(s=><button key={s} aria-pressed={fleet===s} onClick={()=>setFleet(s)}>{s==='Alpha'?'ALFA':'BRAVO'}</button>)}</div><div className="mission-grid"><section className="dominant">{v.checkpoint?<FleetPage key={fleet+':'+account} fleet={fleet} reservations={reservations} fills={fills}/>:<Unknown label="Rezerwacje / fille checkpointu" why="Brak zaakceptowanego checkpointu; liczby zapisów niedostępne."/>}</section><aside className="evidence"><Unknown label="Budżet / settings / contribution" why="Configuration not published. Brak osobnego salda floty; cash portfela nie jest dublowany."/><Unknown label="Broker orders / protection" why="Requested SL/TP to zamiar Offline, nie zweryfikowana ochrona."/></aside></div></>;
}
