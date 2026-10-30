// The only runtime GPU imports. No Canvas wrapper, remote assets, or economics.
import {Component} from 'react';import type {ReactNode} from 'react';
import {createRoot,extend,events,unmountComponentAtNode,_roots} from '@react-three/fiber';import type {RootStore,ThreeEvent} from '@react-three/fiber';
import {WebGLRenderer,Group,Mesh,BoxGeometry,ConeGeometry,RingGeometry,MeshBasicMaterial,DoubleSide,OrthographicCamera} from 'three';
import type {SceneFrame,SceneCue,VisualPrefs} from './model.ts';import {sceneBudget} from './model.ts';import {createFrameDriver} from './SceneViewport.tsx';import type {SceneHandle} from './SceneViewport.tsx';
export type {SceneHandle} from './SceneViewport.tsx';
extend({Group,Mesh});
// Three installs setSize on each instance; a prototype override is shadowed.
class CspRenderer extends WebGLRenderer{constructor(parameters:ConstructorParameters<typeof WebGLRenderer>[0]){super(parameters);const resize=this.setSize.bind(this);this.setSize=(width:number,height:number,_updateStyle?:boolean)=>resize(width,height,false);}}
class SceneBoundary extends Component<{children:ReactNode;fail:()=>void},{failed:boolean}>{state={failed:false};static getDerivedStateFromError(){return {failed:true};}override componentDidCatch(){this.props.fail();}override render(){return this.state.failed?null:this.props.children;}}
export async function mountScene(canvas:HTMLCanvasElement,initial:SceneFrame,initialVisual:VisualPrefs,onSelect:(id:string)=>void,onFailure:(code:string)=>void):Promise<SceneHandle>{
 let disposed=false,deliberate=false,failed=false,disposePromise:Promise<void>|null=null;
 let latest=initial,visual=initialVisual,cue:SceneCue|null=null,selectionStart=-Infinity,selected=initial.selectedId;
 let store:RootStore|null=null,root:ReturnType<typeof createRoot>|null=null,context:WebGL2RenderingContext|null=null;let gl:CspRenderer|null=null;
 const group=new Group(),ships:Group[]=[],rings:Mesh[]=[],hulls:Mesh[]=[];
 const geometries=[new BoxGeometry(1,.18,.42),new ConeGeometry(.23,.42,3),new RingGeometry(.47,.51,24),new RingGeometry(.15,.18,16),new RingGeometry(1.7,1.72,48),new RingGeometry(1.2,1.22,48)];
 const materials=['#398b9b','#ffc278','#516675','#85e2ed','#173443','#547d8c','#439eae'].map(color=>new MeshBasicMaterial({color,side:DoubleSide}));
 const arc=new Mesh(new RingGeometry(1.75,1.79,24,1,0,Math.PI/3),materials[6]);geometries.push(arc.geometry);arc.rotation.x=-Math.PI/2;arc.position.y=.015;arc.visible=false;group.add(arc);
 for(let n=0;n<8;n++){const ship=new Group();const hull=new Mesh(geometries[0],materials[0]);const bow=new Mesh(geometries[1],materials[0]);bow.rotation.x=Math.PI/2;bow.position.z=-.33;const deck=new Mesh(geometries[0],materials[5]);deck.scale.set(.45,.9,.45);deck.position.y=.16;const ring=new Mesh(geometries[2],materials[3]);ring.rotation.x=-Math.PI/2;ring.position.y=-.13;ship.add(hull,bow,deck,ring);group.add(ship);ships.push(ship);rings.push(ring);hulls.push(hull);}
 const stations:Mesh[]=[];for(let n=0;n<10;n++){const m=new Mesh(geometries[3],materials[5]),a=n*Math.PI/5;m.rotation.x=-Math.PI/2;m.position.set(Math.cos(a)*1.45,-.2,Math.sin(a)*1.45-1);stations.push(m);group.add(m);}
 const radarRings=[geometries[4],geometries[5]].map(g=>{const m=new Mesh(g,materials[4]);m.rotation.x=-Math.PI/2;m.position.set(0,-.2,-1);group.add(m);return m;});
 const fail=(code:string)=>{if(disposed||deliberate||failed)return;failed=true;driver.stop();if(store)store.getState().events.enabled=false;onFailure(code);};
 const clock={now:()=>performance.now(),request:(fn:(now:number)=>void)=>requestAnimationFrame(fn),cancel:(id:number)=>cancelAnimationFrame(id)};
 let lastVisualSeconds=0,lastNow:number|null=null;
 const driver=createFrameDriver(clock,now=>{
  if(disposed||failed||!store||!gl)return;
  const active=!visual.reduced&&cue&&now-cue.startedAtMs<600?cue:null;
  // A RAF timestamp can precede a cue admitted later in the same frame.
  const phase=active?Math.max(0,(now-active.startedAtMs)/600):0;const selection=!visual.reduced&&now-selectionStart<180?Math.max(0,(now-selectionStart)/180):1;
  latest.units.forEach((u,i)=>{ships[i].position.y=active?.unitIds.includes(u.id)?Math.sin(phase*Math.PI)*.08:0;rings[i].visible=u.id===selected||!!active?.unitIds.includes(u.id);rings[i].scale.setScalar(u.id===selected?1+.12*Math.sin(selection*Math.PI):1);});
  arc.visible=!!active;arc.position.z=latest.kind==='radar'?-1:0;arc.rotation.z=phase*Math.PI*2;
  if(!active)cue=null;
  // Fiber's never-loop consumes seconds. Clamp visual clock delta after suspension.
  lastVisualSeconds+=lastNow===null?0:Math.min(.05,Math.max(0,(now-lastNow)/1000));lastNow=now;
  store.getState().advance(lastVisualSeconds,false);
  if(gl.info.render.calls>sceneBudget.calls||gl.info.render.triangles>sceneBudget.triangles||gl.info.memory.geometries>sceneBudget.geometries||gl.info.memory.textures>0)fail('RESOURCE_CAP_FAILED');
 },()=>fail('RENDER_FAILED'));
 const lost=(e:Event)=>{e.preventDefault();fail('CONTEXT_LOST');};
 const restored=()=>{/* A lost root is latched; restore never resumes or replays. */};
 canvas.addEventListener('webglcontextlost',lost);canvas.addEventListener('webglcontextrestored',restored);
 let observer:ResizeObserver|null=null;
 const size=()=>{const r=canvas.getBoundingClientRect();if(r.width<=0||r.height<=0){fail('ZERO_SIZE_FAILED');return null;}const dpr=Math.min(devicePixelRatio||1,r.width<=720?1:1.5,1600/r.width,900/r.height);return {size:{width:r.width,height:r.height,top:r.top,left:r.left},dpr};};
 const resize=()=>{if(disposed||failed||!store||!gl)return;try{const box=canvas.getBoundingClientRect();if(box.width<=0||box.height<=0){driver.stop();store.getState().events.enabled=false;return;}store.getState().events.enabled=true;const s=size();if(!s)return;store.getState().setDpr(s.dpr);store.getState().setSize(s.size.width,s.size.height,s.size.top,s.size.left);const camera=store.getState().camera;if(camera instanceof OrthographicCamera){camera.zoom=Math.min(s.size.width/9.5,s.size.height/6.5);camera.updateProjectionMatrix();}driver.wake();}catch{fail('RESIZE_FAILED');}};
 const update=(frame:SceneFrame,nextCue:SceneCue|null,prefs:VisualPrefs)=>{if(disposed||failed)return;if(frame.pageKey!==initial.pageKey||!frame.available){fail('IDENTITY_FAILED');return;}const now=performance.now();const changedSelection=frame.selectedId!==selected;latest=frame;visual=prefs;selected=frame.selectedId;if(prefs.reduced){cue=null;selectionStart=-Infinity;driver.stop();}else{if(changedSelection)selectionStart=now;cue=nextCue&&nextCue.pageKey===frame.pageKey&&nextCue.sessionKey===frame.sessionKey?nextCue:null;}
  frame.units.forEach((u,i)=>{const ship=ships[i];ship.visible=true;ship.position.set((i%4-1.5)*1.9,0,Math.floor(i/4)*1.7+(frame.kind==='radar'?1.8:-.7));ship.userData.reservationId=u.id;ship.children.forEach(m=>{m.userData.reservationId=u.id;});const mat=u.uncertain?materials[1]:u.stage==='Rejected'||u.stage==='Terminal'?materials[2]:materials[0];hulls[i].material=mat;(ship.children[1] as Mesh).material=mat;rings[i].material=u.uncertain?materials[1]:materials[3];rings[i].visible=u.id===selected||!!cue?.unitIds.includes(u.id);});ships.forEach((s,i)=>{if(i>=frame.units.length)s.visible=false;});stations.forEach(s=>s.visible=frame.kind==='radar');radarRings.forEach(s=>s.visible=frame.kind==='radar');
  driver.wake(prefs.reduced?0:Math.max(cue?cue.startedAtMs+600:0,changedSelection?selectionStart+180:0));
 };
 const localResources=()=>{group.clear();geometries.forEach(g=>g.dispose());materials.forEach(m=>m.dispose());gl?.dispose();store=null;gl=null;};
 const dispose=()=>{if(disposePromise)return disposePromise;disposed=true;deliberate=true;cue=null;driver.dispose();observer?.disconnect();observer=null;canvas.removeEventListener('webglcontextlost',lost);canvas.removeEventListener('webglcontextrestored',restored);if(store){store.getState().events.enabled=false;store.getState().events.disconnect?.();}
  const registration=root?_roots.get(canvas):null;
  // Fiber 9.6.1 cannot complete unmount without a scene. Before scene setup
  // configure has installed no events/XR and render has never been called:
  // only our raw context, primitives and empty root registration are owned.
  if(!root||(!store&&registration&&!registration.store.getState().scene)){
   disposePromise=Promise.resolve().then(()=>{try{localResources();}finally{if(context&&!context.isContextLost()){context.getExtension('WEBGL_lose_context')?.loseContext();if(!context.isContextLost())throw Error('CLEANUP_FAILED');}context=null;}if(registration)_roots.delete(canvas);}).catch(()=>{throw Error('CLEANUP_FAILED');});return disposePromise;
  }
  disposePromise=new Promise<void>((resolve,reject)=>{const deadline=setTimeout(()=>reject(Error('CLEANUP_FAILED')),1000);unmountComponentAtNode(canvas,()=>{clearTimeout(deadline);try{localResources();context=null;resolve();}catch{reject(Error('CLEANUP_FAILED'));}});});return disposePromise;
 };
 try{const s=size();if(!s)throw Error('ZERO_SIZE_FAILED');context=canvas.getContext('webgl2',{antialias:false,alpha:false,powerPreference:'low-power'});if(!context){fail('NO_WEBGL2');throw Error('NO_WEBGL2');}gl=new CspRenderer({canvas,context,antialias:false,alpha:false,powerPreference:'low-power'});gl.setClearColor('#071722');root=createRoot(canvas);
  await root.configure({gl,frameloop:'never',dpr:s.dpr,size:s.size,orthographic:true,camera:{position:[0,7,10],zoom:Math.min(s.size.width/9.5,s.size.height/6.5),near:.1,far:50},events,shadows:false,onCreated:()=>driver.wake()});
  store=root.render(<SceneBoundary fail={()=>fail('RENDER_FAILED')}><primitive object={group} dispose={null} onClick={(e:ThreeEvent<MouseEvent>)=>{if(disposed||failed)return;const id:unknown=e.object.userData.reservationId;if(typeof id==='string'&&latest.units.some(u=>u.id===id)){e.stopPropagation();onSelect(id);}}}/></SceneBoundary>);
  observer=new ResizeObserver(resize);observer.observe(canvas);update(initial,null,initialVisual);
  return {update,dispose};
 }catch(e){await dispose();throw e;}
}
