'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {parseHTML}=require('linkedom');
const file=path.resolve(__dirname,'../../src/Apex.Desktop/Okx/remote-controller.js');
const tick=()=>new Promise(setImmediate);
function setup(status){
 const {document,window}=parseHTML('<html><head></head><body><div id="okx-operator-slot"></div></body></html>');const calls=[];let release;
 vm.runInNewContext(fs.readFileSync(file,'utf8'),{document,location:{pathname:'/naval/'},MutationObserver:window.MutationObserver,crypto:{randomUUID:()=> 'explicit-connect-id'},AbortController,setTimeout:()=>1,clearTimeout(){},setInterval(){},fetch:async(u,o)=>{calls.push([u,o]);if(o?.method==='POST')return await new Promise(r=>release=()=>r({ok:true,json:async()=>({outcome:'pending-or-unknown',code:'DROPPED'})}));return{ok:true,json:async()=>status};}});
 return{document,calls,click:()=>document.querySelector('[data-controller-action=connect-account]').dispatchEvent(new window.Event('click')),release:()=>release()};
}
test('broker connection is a separate deliberate closed action, with pending duplicate protection and no auto CONNECT',async()=>{
 const p=setup({availability:'CONNECTED',configured:true,channelConnected:true,stale:false,canConnectAccount:true,canPrepare:false,snapshot:{connected:false},mode:'STOP'});await tick();
 const button=p.document.querySelector('[data-controller-action=connect-account]');assert.equal(button.textContent,'Połącz OKX DEMO na VPS');assert.equal(button.disabled,false);
 assert.match(p.document.querySelector('#remote-state').textContent,/OKX DEMO: niepołączony/);assert.equal(p.document.querySelector('[data-controller-action=prepare]').disabled,true);assert.equal(p.calls.filter(c=>c[1]?.method==='POST').length,0);
 p.click();p.click();assert.equal(button.disabled,true);assert.equal(p.calls.filter(c=>c[1]?.method==='POST').length,1);const [url,options]=p.calls.find(c=>c[1]?.method==='POST');assert.equal(url,'/api/naval/controller/connect-account');assert.equal(options.headers['X-Apex-Local'],'1');assert.deepEqual(JSON.parse(options.body),{commandId:'explicit-connect-id'});
 p.release();await tick();assert.match(p.document.querySelector('#remote-result').textContent,/pending-or-unknown/);assert.doesNotMatch(p.document.querySelector('#remote-result').textContent,/Potwierdzono/);
});
test('unpaired, stale, or already-connected observations cannot enable the explicit broker button',async()=>{
 for(const status of [{availability:'NOT_CONFIGURED',stale:true},{channelConnected:true,stale:true,canConnectAccount:true},{channelConnected:true,stale:false,canConnectAccount:false,snapshot:{connected:true}}]){
  const p=setup(status);await tick();assert.equal(p.document.querySelector('[data-controller-action=connect-account]').disabled,true);p.click();await tick();assert.equal(p.calls.filter(c=>c[1]?.method==='POST').length,0);
 }
});
