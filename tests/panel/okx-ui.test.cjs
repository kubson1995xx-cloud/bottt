'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {parseHTML}=require('linkedom');
const source=fs.readFileSync(path.resolve(__dirname,'../../src/Apex.Desktop/Okx/okx-controls.js'),'utf8');
function setup(handler,naval=true){
 const {document,window}=parseHTML(naval?'<html><head></head><body><div class="workspace"><div id="okx-operator-slot"></div></div></body></html>':'<html><head></head><body><div class="app"><aside class="sidebar"></aside><main class="main"></main></div></body></html>');
 const calls=[],timers=[];let interval;
 const context=vm.createContext({document,window,location:{pathname:naval?'/naval/':'/'},crypto:{randomUUID:()=> 'test-command'},AbortController,MutationObserver:window.MutationObserver,
 fetch:async(url,options)=>{calls.push({url,options});return handler(url,options);},setTimeout:(fn,ms)=>{timers.push({fn,ms});return timers.length;},clearTimeout:()=>{},setInterval:fn=>{interval=fn;},console});
 vm.runInContext(source,context);
 return {document,calls,timers,poll:()=>interval?.(),click:action=>document.querySelector('[data-action='+action+']').dispatchEvent(new window.Event('click'))};
}
const tick=()=>new Promise(setImmediate);
test('explicit read-only DEMO test and saved-state diagnosis are separate from connect and START',async()=>{const p=setup(()=>ok());await tick();assert.ok(p.document.querySelector('[data-action=test-demo]'));assert.ok(p.document.querySelector('[data-action=diagnose]'));assert.ok(p.document.querySelector('#okx-enrollment-stage'));});
const ok=(accounts=[])=>({ok:true,status:200,json:async()=>({accounts,productionProfileApproved:false,nativeAvailable:true})});
test('legacy grid/sidebar remains intact; controls mount inside original main',async()=>{const p=setup(()=>ok(),false);await tick();assert.equal(p.document.querySelector('#okx-operator').parentElement.className,'main');assert.equal(p.document.querySelector('.app').firstElementChild.className,'sidebar');});
test('operator mounts only into deliberate Naval slot, not over body/rail',async()=>{const p=setup(()=>ok());await tick();assert.equal(p.document.querySelector('#okx-operator').parentElement.id,'okx-operator-slot');assert.notEqual(p.document.querySelector('#okx-operator').className,'card');});
test('failed initial status cannot leave scope-dependent commands enabled',async()=>{const p=setup(()=>({ok:false,status:503}));await tick();for(const b of p.document.querySelectorAll('button[data-action]'))if(!['configure','refresh'].includes(b.dataset.action))assert.equal(b.disabled,true,b.dataset.action);assert.match(p.document.querySelector('#okx-status').textContent,/503/);assert.ok(p.document.querySelector('[data-action=refresh]'));});
test('command POST is not blocked behind a hung status poll',async()=>{let fail=false;const p=setup((u,o)=>o?.method==='POST'?{ok:true,status:200,json:async()=>({code:'NATIVE_CANCELLED'})}:fail?new Promise(()=>{}):ok());await tick();fail=true;p.poll();p.click('configure');await tick();assert.equal(p.calls.filter(c=>c.options?.method==='POST').length,1);assert.match(p.document.querySelector('#okx-result').textContent,/NATIVE_CANCELLED/);});
test('missing scope and recovery explain disabled controls; polling preserves scope choice',async()=>{const p=setup(()=>ok());await tick();assert.equal(p.document.querySelector('#okx-scope').disabled,true);assert.match(p.document.querySelector('#okx-scope').textContent,/Brak/);assert.equal(p.document.querySelector('#okx-reservation').disabled,true);assert.match(p.document.querySelector('#okx-reason').textContent,/Brak konta/);});
