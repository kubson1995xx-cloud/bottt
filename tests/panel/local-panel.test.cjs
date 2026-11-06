'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {parseHTML}=require('linkedom');
const dir=path.resolve(__dirname,'../../src/Apex.Desktop/Okx');
const html=fs.readFileSync(path.join(dir,'local-panel.html'),'utf8'),source=fs.readFileSync(path.join(dir,'local-panel.js'),'utf8');
const tick=async()=>{for(let i=0;i<4;i++)await new Promise(setImmediate);};
function setup(handler,{confirmResult=true}={}){
 const {document,window}=parseHTML(html);const calls=[],confirms=[];let poll;
 const context=vm.createContext({document,window,location:{hash:'',pathname:'/local/'},history:{replaceState(){}},crypto:{randomUUID:()=>'test-command'},AbortController,console,
  confirm:t=>{confirms.push(t);return confirmResult;},setTimeout:()=>0,clearTimeout:()=>{},setInterval:fn=>{poll=fn;},
  fetch:async(url,options)=>{calls.push({url,options});return handler(url,options);}});
 vm.runInContext(source,context);
 return {document,window,calls,confirms,poll:()=>poll?.(),q:s=>document.querySelector(s),text:id=>document.getElementById(id).textContent,
  click:sel=>document.querySelector(sel).dispatchEvent(new window.Event('click'))};
}
const ok=body=>({ok:true,status:200,json:async()=>body});
const acct=over=>Object.assign({scopeId:'local-demo',environment:'OkxDemo',uid:'1',quote:'EUR',connected:false,mode:'STOP',code:'NOT_CONNECTED',armed:false,ready:false,localOrdinary:false,instruments:null,keyState:'CIPHERTEXT_PRESENT_NOT_VERIFIED'},over);
const status=over=>Object.assign({mode:'STOP',lastResult:'NOT_CONNECTED',enrollmentStage:'NOT_STARTED',executionMode:'LOCAL',localCreateAvailable:false,keepAwake:false,nativeAvailable:true,accounts:[acct()]},over);
const strategy={name:'Naval D1',selectionFixed:true,sameForAllFleets:true,entryDays:40,exitDays:30,btcSmaDays:200,atrDays:14,stopAtr:2,takeProfitR:3,instruments:['BTC-EUR','ETH-EUR','SOL-EUR']};
const dashboard=over=>Object.assign({schema:'apex-local-dashboard-v1',generatedUtc:new Date().toISOString(),scopeId:'local-demo',connected:false,localOrdinary:false,snapshotState:'DISCONNECTED',snapshot:null,equity:null,limits:null,marks:null,strategy,evaluation:null,obligations:null,instruments:null,lastResult:'NOT_CONNECTED'},over);
const now=new Date().toISOString();
const snapshot=over=>Object.assign({schema:'apex-controller-snapshot-v1',generatedUtc:now,connected:true,mode:'RUNNING',armed:true,ready:true,emergency:false,revision:7,
 capital:{quote:'EUR',cash:987.5,held:0.1,free:987.4,allocatedCost:0.1,liquidationEquity:987.6},
 observation:{origin:'OPERATIONAL',captureUtc:now,requestUtc:now,receiveUtc:now,economicUtc:now,evidenceAvailable:true,historyAvailable:true},
 evaluation:{completed:3,wait:1,entry:1,exit:1,code:'ENTRY'},
 balances:{total:4,truncated:false,items:[{currency:'BTC',total:0.5009,available:0.5009,frozen:0,sourceUtc:now,excluded:true},{currency:'ETH',total:0,available:0,frozen:0,sourceUtc:now,excluded:true},{currency:'EUR',total:987.5,available:987.5,frozen:0,sourceUtc:now,excluded:false},{currency:'SOL',total:3.5,available:3.5,frozen:0,sourceUtc:now,excluded:true}]},
 catalog:{total:3,truncated:false,items:[]},
 inventory:{total:1,truncated:false,items:[{instrument:'BTC-EUR',quantity:0.0009,pending:0,allocatedCost:0.1,riskHold:0,reductionUnsettled:false,protection:'OBSERVED_PROTECTION'}]},
 orders:{total:0,truncated:false,items:[]},
 protection:{total:1,truncated:false,items:[{instrument:'BTC-EUR',state:'live',quantity:0.0009,stop:100.5,takeProfit:128.7}]},
 history:{total:3,truncated:false,items:[
  {instrument:'BTC-EUR',reduction:false,quantity:0.0009,price:110,fee:0.0000009,feeCurrency:'BTC',tradeUtc:'2026-11-06T10:05:00Z'},
  {instrument:'ETH-EUR',reduction:true,quantity:0.0899,price:112,fee:0.01,feeCurrency:'EUR',tradeUtc:'2026-11-06T10:01:00Z'},
  {instrument:'ETH-EUR',reduction:false,quantity:0.09,price:110,fee:0.00009,feeCurrency:'ETH',tradeUtc:'2026-11-06T10:00:00Z'}]}},over);
const limits={quote:'EUR',tradePct:0.5,dailyPct:1.5,drawdownPct:5,maxLeverage:1,centralMax:3,alphaMax:2,bravoMax:2,ordinaryEntryCapQuote:10,entryFeeRate:0.001,exitFeeRate:0.001};
const equity={dayKey:'2026-11-06',dayStartEquity:987,liquidationEquity:987.6,dailyRemaining:14.8,drawdownRemaining:49,dailyLatch:false,drawdownLatch:false,reconciledUtc:now};
const marks=[{instrument:'BTC-EUR',bid:111,reconciledUtc:now},{instrument:'ETH-EUR',bid:null,reconciledUtc:now},{instrument:'SOL-EUR',bid:150.25,reconciledUtc:now}];
const serve=(s,d)=>url=>url==='/api/naval/okx/status'?ok(s):url==='/api/naval/okx/local-dashboard'?ok(d):ok({code:'POSTED'});
const metricIds=['capital-cash','account-eur','open-count','daily-limit'];

test('no status: every metric is "—", explicit unconfirmed notice, no sample data anywhere',async()=>{
 const p=setup(()=>({ok:false,status:503}));await tick();
 for(const id of metricIds)assert.equal(p.text(id),'—',id);
 assert.equal(p.q('#data-notice').hasAttribute('hidden'),false);assert.match(p.text('data-notice'),/Brak potwierdzonego statusu/);
 assert.equal(p.q('#daily-card').hasAttribute('hidden'),true);assert.equal(p.q('#chart-wrap').hasAttribute('hidden'),true);
 const all=p.document.documentElement.outerHTML;
 for(const banned of ['USDT','XRP','$','10 248','IndexDipBuy','Trend Following','Mean Reversion','Breakout','Prototyp','przykład','SYMULACJA'])assert.ok(!all.includes(banned),banned);
 for(const b of p.document.querySelectorAll('#stepper button'))assert.equal(b.disabled,true,b.dataset.action);
 assert.equal(p.q('#stop-button').disabled,false);
});
test('disconnected local owner: "—" values with explicit disconnected state and Polish mode label',async()=>{
 const p=setup(serve(status(),dashboard()));await tick();
 for(const id of metricIds)assert.equal(p.text(id),'—',id);
 assert.match(p.text('data-notice'),/nie jest połączony z OKX \(Observe\)/);assert.equal(p.text('mode-pill'),'Tryb: Lokalnie (ten PC)');
 assert.match(p.text('positions-dash'),/Brak połączenia/);assert.match(p.text('history-rows'),/Brak połączenia/);assert.match(p.text('risk-limits'),/odczyt po połączeniu Observe/);
 assert.match(p.text('markets'),/BTC \/ EUR—brak wyceny/);assert.ok(!/\d/.test(p.text('markets').replace(/BTC|ETH|SOL|EUR|\//g,'')),'no market numbers without data');
 assert.match(p.text('state-pill'),/STOP/);
});
test('stale, future or missing observation clocks show no financial values even when connected',async()=>{
 for(const at of [new Date(Date.now()-10*60000).toISOString(),new Date(Date.now()+60000).toISOString(),null,'invalid']){
  const p=setup(serve(status({accounts:[acct({connected:true,localOrdinary:true})]}),dashboard({connected:true,localOrdinary:true,snapshotState:'CURRENT',snapshot:snapshot({observation:{origin:'OPERATIONAL',captureUtc:at,requestUtc:at,receiveUtc:at,economicUtc:at,evidenceAvailable:true,historyAvailable:true}}),limits,equity,marks})));await tick();
  assert.match(p.text('data-notice'),/Dane nieaktualne/);
  for(const id of metricIds)assert.equal(p.text(id),'—',id);
  assert.equal(p.q('#chart-wrap').hasAttribute('hidden'),true);
  assert.equal(p.q('#daily-card').hasAttribute('hidden'),true);
  assert.ok(!p.text('history-rows').includes('110'));
  assert.ok(!p.text('markets').includes('111 EUR'));
 }
});
test('incomplete history cannot invent realized P&L from unmatched sales or unsupported fees',async()=>{
 for(const h of [
  {...snapshot().history,truncated:true},
  {total:1,truncated:false,items:[snapshot().history.items[1]]},
  {total:2,truncated:false,items:[{...snapshot().history.items[1],quantity:1},snapshot().history.items[2]]},
  {total:2,truncated:false,items:[{...snapshot().history.items[1],feeCurrency:'BTC'},snapshot().history.items[2]]}
 ]){
  const p=setup(serve(status(),dashboard({connected:true,snapshotState:'CURRENT',snapshot:snapshot({history:h}),limits,equity,marks})));await tick();
  assert.equal(p.q('#chart-wrap').hasAttribute('hidden'),true);
  assert.equal(p.text('chart-value'),'—');
 }
});
test('real snapshot renders capital, account balance, excluded holdings, positions, history, chart and marks',async()=>{
 const p=setup(serve(status({keepAwake:true,accounts:[acct({connected:true,localOrdinary:true,mode:'RUNNING',armed:true,instruments:['BTC-EUR','ETH-EUR','SOL-EUR']})]}),dashboard({connected:true,localOrdinary:true,snapshotState:'CURRENT',snapshot:snapshot(),limits,equity,marks})));await tick();
 assert.equal(p.text('capital-cash'),'987,50 EUR');assert.equal(p.text('account-eur'),'987,50 EUR');
 assert.match(p.text('account-excluded'),/BTC 0,5009 · SOL 3,5/);assert.ok(!p.text('account-excluded').includes('ETH'),'zero excluded rows are not listed');
 assert.equal(p.text('open-count'),'1');assert.equal(p.text('daily-limit'),'1,5%');assert.match(p.text('daily-remaining'),/14,80 EUR/);
 assert.equal(p.q('#daily-card').hasAttribute('hidden'),false);assert.equal(p.text('daily-pnl'),'+0,60 EUR');
 assert.match(p.text('positions-full'),/BTC-EUR/);assert.match(p.text('positions-full'),/Aktywna · SL 100,5 \/ TP 128,7/);
 assert.equal(p.document.querySelectorAll('#history-rows tr').length,3);assert.match(p.text('history-rows'),/SPRZEDAŻ/);assert.match(p.text('history-rows'),/KUPNO/);
 assert.equal(p.q('#chart-wrap').hasAttribute('hidden'),false);assert.match(p.text('chart-change'),/1 zamkn\. wyjść/);
 assert.match(p.text('markets'),/BTC \/ EUR111 EUR/);assert.match(p.text('markets'),/ETH \/ EUR—/);assert.ok(!p.text('markets').includes('USDT'));
 assert.match(p.text('state-pill'),/RUNNING · uśpienie zablokowane/);assert.equal(p.q('#running-notice').hasAttribute('hidden'),false);
 assert.match(p.text('strategy-params'),/40/);assert.match(p.text('strategy-name'),/40\/30\/200/);assert.match(p.text('risk-limits'),/0,5% kapitału/);
 assert.match(p.text('connection-info'),/zapisany \(Windows DPAPI\)/);
 const e=new p.window.Event('beforeunload',{cancelable:true});p.window.dispatchEvent(e);assert.equal(e.defaultPrevented,true);
});
test('stepper maps to the existing closed routes with only a command id; STOP warns it does not sell',async()=>{
 const posts=p=>p.calls.filter(c=>c.options?.method==='POST');
 const first=setup(serve(status({localCreateAvailable:true,executionMode:'NOT_SELECTED',accounts:[]}),dashboard({scopeId:null})));await tick();
 first.click('[data-action=create-local]');await tick();
 assert.equal(posts(first)[0].url,'/api/naval/okx/create-local');assert.deepEqual(JSON.parse(posts(first)[0].options.body),{commandId:'test-command'});assert.equal(posts(first)[0].options.headers['X-Apex-Local'],'1');
 first.click('#stop-button');await tick();assert.equal(posts(first)[1].url,'/api/naval/okx/cancel');assert.match(first.confirms[0],/NIE sprzedaje otwartych pozycji/);
 const stages=[[acct(),'attach-local'],[acct({localOrdinary:true}),'connect'],[acct({localOrdinary:true,connected:true}),'prepare'],[acct({localOrdinary:true,connected:true,instruments:['BTC-EUR','ETH-EUR','SOL-EUR']}),'arm'],[acct({localOrdinary:true,connected:true,armed:true,instruments:['BTC-EUR','ETH-EUR','SOL-EUR']}),'start']];
 for(const [a,action] of stages){
  const p=setup(serve(status({accounts:[a]}),dashboard()));await tick();
  const enabled=[...p.document.querySelectorAll('#stepper button')].filter(b=>!b.disabled).map(b=>b.dataset.action);assert.deepEqual(enabled,[action]);
  p.click('[data-action='+action+']');await tick();assert.equal(posts(p)[0].url,'/api/naval/okx/local-demo/'+action);assert.deepEqual(JSON.parse(posts(p)[0].options.body),{commandId:'test-command'});
 }
 const s=setup(serve(status({accounts:[acct({localOrdinary:true,connected:true,mode:'RUNNING',armed:true})]}),dashboard()));await tick();
 s.click('#stop-button');await tick();assert.equal(posts(s)[0].url,'/api/naval/okx/local-demo/stop');
 const declined=setup(serve(status(),dashboard()),{confirmResult:false});await tick();declined.click('#stop-button');await tick();assert.equal(posts(declined).length,0);
 const vps=setup(serve(status({executionMode:'VPS',accounts:[acct()]}),dashboard()));await tick();
 for(const b of vps.document.querySelectorAll('#stepper button'))assert.equal(b.disabled,true);assert.equal(vps.text('mode-pill'),'Tryb: VPS');
});
test('no Pause, manual close, strategy switch or editable risk controls exist',async()=>{
 const p=setup(serve(status({accounts:[acct({connected:true,localOrdinary:true,mode:'RUNNING',armed:true})]}),dashboard({connected:true,snapshotState:'CURRENT',snapshot:snapshot(),limits,equity,marks})));await tick();
 const buttons=[...p.document.querySelectorAll('button')].map(b=>b.textContent);
 for(const t of buttons)assert.ok(!/Pauza|Zamknij|Zapisz|Wybierz|Wznów/.test(t),t);
 const inputs=[...p.document.querySelectorAll('input,select,textarea')];assert.deepEqual(inputs.map(i=>i.id),['notify-toggle']);
 const actions=[...p.document.querySelectorAll('[data-action]')].map(b=>b.dataset.action).sort();assert.deepEqual(actions,['arm','attach-local','connect','create-local','prepare','start']);
 assert.ok(!html.includes('<script>')&&!/ style="/.test(html),'CSP: no inline script/style');
 assert.ok(!/https?:\/\//.test(html+fs.readFileSync(path.join(dir,'local-panel.css'),'utf8').replace(/http:\/\/www\.w3\.org\/2000\/svg/g,'')),'no external network resources');
});
