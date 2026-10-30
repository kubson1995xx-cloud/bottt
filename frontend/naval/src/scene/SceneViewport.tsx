import {useEffect,useRef,useState} from 'react';import type {ReactNode} from 'react';
import {projectScene,stepSceneCue} from './model.ts';import type {SceneInput,SceneFrame,SceneCue,VisualPrefs,CueState} from './model.ts';
export interface SceneHandle{update(frame:SceneFrame,cue:SceneCue|null,visual:VisualPrefs):void;dispose():Promise<void>}
export type SceneModule={mountScene(canvas:HTMLCanvasElement,frame:SceneFrame,visual:VisualPrefs,onSelect:(id:string)=>void,onFailure:(code:string)=>void):Promise<SceneHandle>};
export type FrameClock={now:()=>number;request:(fn:(now:number)=>void)=>number;cancel:(id:number)=>void};
// One cancellable coalescing RAF. No polling, idle loop, or work queued behind a cue.
export function createFrameDriver(clock:FrameClock,render:(now:number)=>void,fail:()=>void){
 let request:number|null=null,until=0,dirty=false,dead=false,last=-Infinity;
 const stop=()=>{if(request!==null)clock.cancel(request);request=null;until=0;dirty=false;};
 const frame=(now:number)=>{request=null;if(dead)return;try{if(now-last>=1000/30){last=now;render(now);dirty=false;if(now>=until)until=0;}if(dirty||until>0)request=clock.request(frame);}catch{stop();fail();}};
 return {wake(end=0){if(dead)return;dirty=true;until=Math.max(until,end);if(request===null)request=clock.request(frame);},stop,dispose(){dead=true;stop();}};
}
// The lease is held through actual delayed R3F cleanup, including pending configure.
export function createSceneLane(){let tail=Promise.resolve(),blocked=false;return {async acquire(){const prior=tail;let done!:()=>void;tail=new Promise<void>(r=>done=r);await prior;if(blocked){done();throw Error('CLEANUP_FAILED');}let released=false;return (ok=true)=>{if(released)return;released=true;if(!ok)blocked=true;done();};}};}
const documentLane=createSceneLane();
export function createViewportController(deps:{load:()=>Promise<SceneModule>;lane:ReturnType<typeof createSceneLane>;now:()=>number;status:(code:string)=>void}){
 let generation=0,dead=false,latched=false,key:string|null=null,cue:CueState|null=null,lastRendered:string|null=null;
 let current:{handle:SceneHandle;release:(ok?:boolean)=>void}|null=null;
 let latest:{canvas:HTMLCanvasElement;input:SceneInput;visual:VisualPrefs;select:(id:string)=>void}|null=null;
 const pending=new Set<Promise<void>>();
 const track=(job:Promise<void>)=>{pending.add(job);void job.finally(()=>pending.delete(job));};
 const retire=(owned:{handle:SceneHandle;release:(ok?:boolean)=>void})=>{const job=owned.handle.dispose().then(()=>owned.release(),()=>{owned.release(false);latched=true;if(!dead)deps.status('CLEANUP_FAILED');});track(job);return job;};
 const stop=()=>{generation++;key=null;cue=null;lastRendered=null;const old=current;current=null;if(old)void retire(old);};
 const render=(handle:SceneHandle,frame:SceneFrame,active:SceneCue|null,visual:VisualPrefs)=>{const signature=JSON.stringify([frame,active,visual]);if(signature===lastRendered)return;handle.update(frame,active,visual);lastRendered=signature;};
 const failure=(code:string)=>{if(dead||latched)return;latched=true;stop();deps.status(code);};
 const update=(canvas:HTMLCanvasElement,input:SceneInput,visual:VisualPrefs,enabled:boolean,select:(id:string)=>void)=>{
  if(dead)return;latest={canvas,input,visual,select};const f=projectScene(input);
  if(!enabled||visual.minimal||!f.available){if(key!==null)stop();cue=stepSceneCue(null,input,false,deps.now());if(!latched)deps.status('2D');return;}
  if(latched)return;
  if(key===f.pageKey){if(current){cue=stepSceneCue(cue,input,!visual.reduced,deps.now());try{render(current.handle,f,cue.active,visual);}catch{failure('RENDER_FAILED');}}return;}
  stop();key=f.pageKey;const own=++generation;cue=stepSceneCue(null,input,false,deps.now());deps.status('loading');
  const job=(async()=>{let release:((ok?:boolean)=>void)|null=null;let attached=false;
   try{const mod=await deps.load();if(dead||own!==generation)return;release=await deps.lane.acquire();if(dead||own!==generation){release();return;}const l=latest!;const baseline=projectScene(l.input);const handle=await mod.mountScene(l.canvas,baseline,l.visual,id=>{const n=latest;if(!dead&&own===generation&&n&&projectScene(n.input).units.some(u=>u.id===id))n.select(id);},code=>{if(own===generation)failure(code);});
    if(dead||own!==generation){await retire({handle,release});return;}
    current={handle,release};attached=true;cue=stepSceneCue(null,latest!.input,false,deps.now());render(handle,projectScene(latest!.input),null,latest!.visual);deps.status('ready');
   }catch(e){if(release&&!attached)release(!(e instanceof Error&&e.message==='CLEANUP_FAILED'));if(own===generation)failure(e instanceof Error&&e.message==='CLEANUP_FAILED'?'CLEANUP_FAILED':release?'RENDER_FAILED':'IMPORT_FAILED');}
  })();track(job);
 };
 return {update,async dispose(){if(!dead){dead=true;stop();}while(pending.size)await Promise.all([...pending]);}};
}
export function SceneViewport({input,visual,fallback,onSelect}:{input:SceneInput;visual:VisualPrefs;fallback:ReactNode;onSelect:(id:string)=>void}){
 const host=useRef<HTMLDivElement>(null),canvas=useRef<HTMLCanvasElement>(null),lastCanvas=useRef<HTMLCanvasElement|null>(null),controller=useRef<ReturnType<typeof createViewportController>|null>(null);
 const [status,setStatus]=useState('2D'),[eligible,setEligible]=useState(false),[retry,setRetry]=useState(0);
 useEffect(()=>{let live=true;const sync=()=>{const r=host.current?.getBoundingClientRect();if(live)setEligible(!document.hidden&&!!r&&r.width>0&&r.height>0);};const ro=typeof ResizeObserver==='undefined'?null:new ResizeObserver(sync);if(host.current)ro?.observe(host.current);document.addEventListener('visibilitychange',sync);sync();return ()=>{live=false;ro?.disconnect();document.removeEventListener('visibilitychange',sync);};},[]);
 useEffect(()=>{let live=true;const c=createViewportController({load:()=>import('./scene.webgl.tsx'),lane:documentLane,now:()=>performance.now(),status:s=>{if(live)setStatus(s);}});controller.current=c;return ()=>{live=false;controller.current=null;void c.dispose();};},[retry]);
 useEffect(()=>{if(canvas.current)lastCanvas.current=canvas.current;const target=canvas.current??lastCanvas.current;if(target)controller.current?.update(target,input,visual,eligible,onSelect);},[input,visual,eligible,onSelect,retry]);
 const f=projectScene(input),ready=status==='ready'&&eligible&&!visual.minimal;
 return <section className="scene-viewport" aria-label="Historical reservation display"><div ref={host} className="naval-canvas-host">{!visual.minimal&&<canvas key={JSON.stringify([retry,f.pageKey,eligible])} ref={canvas} className={ready?'naval-canvas':'naval-canvas scene-pending'} aria-label="Historical records 3D; select exact records using the list below"/>}{!ready&&<div className="scene-fallback">{fallback}</div>}</div><p className="caption scene-status" role="status">{ready?'3D historical reservation records — categorical layout':`SVG / list fallback · ${status}`} · NO MARKET FEED · broker NOT_CONFIGURED · stale · brak audio</p><p className="caption">Historical publication updated — not a market scan. Local selection outline only; no trade command. {f.totalCount??'UNKNOWN'} records in filter.</p>{status.endsWith('_FAILED')||status==='CONTEXT_LOST'||status==='NO_WEBGL2'?<button onClick={()=>setRetry(n=>n+1)}>Retry 3D</button>:null}</section>;
}
