import {useEffect,useState} from 'react';
import type {NavalState} from '../read/store.ts';import type {ReadReservation,ReadFill,Fleet} from '../read/contracts.ts';
import {SceneViewport} from '../scene/SceneViewport.tsx';import type {SceneInput,VisualPrefs} from '../scene/model.ts';
import {FleetGlyph} from './FleetGlyph.tsx';import {Radar2D} from './Radar2D.tsx';import {DiagramBoundary,ReservationRows,FillRows} from './Truth.tsx';
export function ReservationPage({kind,state,visual,accountKey,fleet,asset,onSelectAsset,reservations,fills}:{kind:'fleet'|'radar';state:NavalState;visual:VisualPrefs;accountKey:string;fleet:Fleet|null;asset:string;onSelectAsset:(asset:string)=>void;reservations:readonly ReadReservation[];fills:readonly ReadFill[]}){
 const [page,setPage]=useState(0),[selected,setSelected]=useState<string|null>(null);
 const pages=Math.max(1,Math.ceil(reservations.length/8)),p=Math.min(page,pages-1),visible=reservations.slice(p*8,p*8+8);
 const selectedId=page===p&&visible.some(r=>r.reservation.id===selected)?selected:null;
 useEffect(()=>{if(page!==p){setPage(p);setSelected(null);}},[page,p]);
 const select=(id:string)=>{if(visible.some(r=>r.reservation.id===id))setSelected(selectedId===id?null:id);};
 const changePage=(value:number)=>{setPage(Math.max(0,Math.min(pages-1,value)));setSelected(null);};
 const input:SceneInput={kind,state,accountKey,fleet,asset,page:p,visible,totalCount:reservations.length,selectedId};
 const fallback=<><DiagramBoundary>{kind==='radar'&&<Radar2D selected={asset} onSelect={onSelectAsset}/>}</DiagramBoundary><DiagramBoundary><FleetGlyph fleet={fleet??'PLANNED ROSTER / ACTUAL RECORD LANE'} reservations={visible} totalCount={reservations.length} selectedId={selectedId} onSelect={select}/></DiagramBoundary></>;
 return <><SceneViewport input={input} visual={visual} fallback={fallback} onSelect={select}/><p>{reservations.length} rezerwacji / {fills.length} filli w checkpointcie i filtrze.</p><ReservationRows items={visible} pagination={{page:p,pages,setPage:changePage,total:reservations.length}} selection={{id:selectedId,onSelect:select}}/><FillRows items={fills}/></>;
}
