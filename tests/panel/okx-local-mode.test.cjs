'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {parseHTML}=require('linkedom');
const source=fs.readFileSync(path.resolve(__dirname,'../../src/Apex.Desktop/Okx/okx-controls.js'),'utf8');
function setup(state){
 const {document,window}=parseHTML('<html><head></head><body><div class="workspace"><div id="okx-operator-slot"></div></div></body></html>');
 const calls=[];
 const context=vm.createContext({document,window,location:{pathname:'/naval/'},crypto:{randomUUID:()=>'test-command'},AbortController,MutationObserver:window.MutationObserver,
  fetch:async(url,options)=>{calls.push({url,options});return {ok:true,status:200,json:async()=>options?.method==='POST'?{code:'LOCAL_OWNER_CREATED_STOP_ATTACH_NEXT'}:state()};},setTimeout:()=>0,clearTimeout:()=>{},setInterval:()=>{},console});
 vm.runInContext(source,context);
 return {document,window,calls};
}
const tick=()=>new Promise(setImmediate);
const base=over=>Object.assign({accounts:[],nativeAvailable:true,enrollmentStage:'NOT_STARTED',mode:'STOP',keepAwake:false,executionMode:'NOT_SELECTED',localCreateAvailable:true},over);
function unload(window){const e=new window.Event('beforeunload',{cancelable:true});window.dispatchEvent(e);return e.defaultPrevented;}
test('mode label distinguishes local PC, VPS and first run in Polish',async()=>{
 for(const [mode,text] of [['LOCAL','Tryb: Lokalnie (ten PC)'],['VPS','Tryb: VPS'],['NOT_SELECTED','Tryb: nie wybrano']]){
  const p=setup(()=>base({executionMode:mode}));await tick();
  assert.ok(p.document.querySelector('#okx-execution-mode').textContent.startsWith(text),mode);
 }
});
test('RUNNING shows the Polish keep-PC-on warning and blocks accidental close; STOP clears it',async()=>{
 let s=base({executionMode:'LOCAL',mode:'RUNNING',keepAwake:true,localCreateAvailable:false});
 const p=setup(()=>s);await tick();
 const banner=p.document.querySelector('#okx-running-warning');
 assert.equal(banner.hidden,false);assert.match(banner.textContent,/Nie zamykaj okna APEX/);assert.match(banner.textContent,/najpierw STOP/);
 assert.match(p.document.querySelector('#okx-execution-mode').textContent,/uśpienie komputera zablokowane/);
 assert.equal(unload(p.window),true);
 s=base({executionMode:'LOCAL',mode:'STOP',localCreateAvailable:false});
 p.document.querySelector('[data-action=refresh]').dispatchEvent(new p.window.Event('click'));await tick();await tick();
 assert.equal(banner.hidden,true);assert.equal(unload(p.window),false);
 assert.doesNotMatch(p.document.querySelector('#okx-execution-mode').textContent,/uśpienie/);
});
test('first-run local creation posts only a command id to the closed route and is unavailable once configured',async()=>{
 const p=setup(()=>base());await tick();
 const b=p.document.querySelector('[data-action=create-local]');assert.equal(b.disabled,false);
 b.dispatchEvent(new p.window.Event('click'));await tick();await tick();
 const post=p.calls.find(c=>c.options?.method==='POST');assert.equal(post.url,'/api/naval/okx/create-local');assert.deepEqual(JSON.parse(post.options.body),{commandId:'test-command'});
 assert.match(p.document.querySelector('#okx-result').textContent,/LOCAL_OWNER_CREATED_STOP_ATTACH_NEXT/);
 const q=setup(()=>base({executionMode:'LOCAL',localCreateAvailable:false}));await tick();
 assert.equal(q.document.querySelector('[data-action=create-local]').disabled,true);
 const v=setup(()=>base({executionMode:'VPS',localCreateAvailable:false}));await tick();
 assert.equal(v.document.querySelector('[data-action=create-local]').disabled,true);
});
