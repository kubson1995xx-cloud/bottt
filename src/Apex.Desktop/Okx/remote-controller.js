/* Remote VPS controller; historical Naval views and Windows read-only enrollment are independent. */
'use strict';
(()=>{
 if(document.getElementById('remote-controller'))return;
 const css=document.createElement('link');css.rel='stylesheet';css.href='/remote-controller.css';document.head.append(css);
 const box=document.createElement('section');box.id='remote-controller';box.setAttribute('aria-label','Sterownik silnika VPS');
 function el(tag,text,parent=box){const e=document.createElement(tag);if(text!=null)e.textContent=text;parent.append(e);return e;}
 el('h2','Silnik VPS — sterowanie i rzeczywiste obserwacje');
 el('p','Jeden silnik na VPS. Ten panel nie wykonuje zleceń lokalnie. Po potwierdzonym START zamknięcie EXE lub rozłączenie kanału NIE zatrzymuje silnika VPS. STOP wymaga potwierdzenia serwera; nie oznacza sprzedaży pozycji.');
 el('p','Widoki Naval poniżej pozostają historyczną / syntetyczną prezentacją, nie bieżącym rachunkiem. Ten oddzielny panel pokazuje wyłącznie obserwacje klienta sterownika.');
 const state=el('p','Stan nieznany —'),result=el('p','Brak wysłanego polecenia.'),last=el('p','Ostatnie potwierdzone sterowanie: —');state.id='remote-state';state.setAttribute('role','status');result.id='remote-result';result.setAttribute('aria-live','polite');
 const actions=el('div',null);actions.className='remote-actions';const buttons={};let status=null,reading=false;const pending=new Set();
 const names=[['connect','Połącz kanał VPS'],['disconnect','Rozłącz tylko kanał — NIE STOP'],['connect-account','Połącz OKX DEMO na VPS'],['prepare','PREPARE — sprawdź gotowość'],['arm','ARM — uzbrój, bez START'],['start','START silnika VPS'],['stop','STOP — żądaj potwierdzenia'],['emergency','Emergency STOP'],['refresh','Odśwież obserwacje VPS']];
 const warning=el('p','SOL: niechroniony stan wymaga uwagi; brak obserwacji nie dowodzi ochrony.');warning.id='remote-sol-warning';
 const data=el('div',null);data.id='remote-data';
 const value=x=>x==null?'—':String(x);
 function render(){
  const stale=!status||status.stale!==false;state.dataset.tone=stale?'stale':status.mode==='RUNNING'?'running':'stopped';
  state.textContent=`${status?.availability||'NIEZNANY'} | ${stale?'NIEAKTUALNE / NIEPOŁĄCZONE — ostatnie dane, nie bieżące RUNNING':'Potwierdzona obserwacja'} | OKX DEMO: ${stale?'stan nieaktualny':status?.snapshot?.connected===true?'połączony':status?.snapshot?.connected===false?'niepołączony':'—'} | ostatni tryb: ${value(status?.mode)} | ${value(status?.outcome)} / ${value(status?.code)}`;
  last.textContent='Ostatnie potwierdzone sterowanie: '+value(status?.lastConfirmedControl)+'; oczekujące: '+value(status?.pending);
  for(const [a,b] of Object.entries(buttons)){
   b.disabled=['stop','emergency','disconnect'].includes(a)?pending.has(a):a==='connect'?status?.configured!==true||pending.has(a):pending.has(a)||(a==='connect-account'?(!status?.canConnectAccount||stale||pending.size>0):a==='start'?(!status?.canStart||stale||pending.size>0):a==='prepare'?(!status?.canPrepare||stale):a==='arm'?(!status?.canArm||stale):!status?.channelConnected);
  }
  data.replaceChildren();const s=status?.snapshot;
  if(!s||s.connected===false){el('p','Broker / kapitał / pozycje / historia: — (brak obserwacji, nie zero). P&L niedostępny.',data);return;}
  const o=s.observation;el('p',`Źródło: ${value(o?.origin)} | publikacja: ${value(s.generatedUtc)} | capture: ${value(o?.captureUtc)} | request: ${value(o?.requestUtc)} | receive: ${value(o?.receiveUtc)} | czas ekonomiczny: ${value(o?.economicUtc)}`,data);
  const c=s.capital;el('h3','Kapitał bota — oddzielny od całego salda brokera',data);el('p',`Waluta ${value(c?.quote)} | Cash ${value(c?.cash)} | Free ${value(c?.free)} | Held ${value(c?.held)} | koszt inventory ${value(c?.allocatedCost)} | equity likwidacyjne ${value(c?.liquidationEquity)} | P&L niedostępny — DTO nie zawiera wyniku.`,data);
  function table(title,window,headers,rows,available=true){
   el('h3',title,data);if(!available){el('p','— Brak pokrycia obserwacji / historii.',data);return;}
   if(!window){el('p','—',data);return;}
   el('p',`Pokazano ${window.items.length} / łącznie ${window.total}${window.truncated?' — UCIĘTE (nie pełna historia)':''}`,data);
   const t=el('table',null,data),head=el('tr',null,el('thead',null,t));for(const h of headers)el('th',h,head);
   const body=el('tbody',null,t);for(const item of window.items){const tr=el('tr',null,body);for(const x of rows(item))el('td',value(x),tr);}
  }
  table('Broker — salda osobno według walut',s.balances,['Waluta','Razem','Dostępne','Zablokowane','Czas źródłowy','Wyłączone'],b=>[b.currency,b.total,b.available,b.frozen,b.sourceUtc,b.excluded],o?.evidenceAvailable);
  table('Własne pozycje i ochrona',s.inventory,['Instrument','Ilość','Pending','Koszt','Risk hold','Ochrona'],i=>[i.instrument,i.quantity,i.pending,i.allocatedCost,i.riskHold,i.protection]);
  table('Własne zlecenia',s.orders,['Instrument','Stan','Ilość','Fill','Opłata signed','Waluta'],i=>[i.instrument,i.state,i.quantity,i.filled,i.signedFee,i.feeCurrency],o?.historyAvailable);
  table('SL / TP — obserwowane zabezpieczenia',s.protection,['Instrument','Stan','Ilość','SL','TP'],i=>[i.instrument,i.state,i.quantity,i.stop,i.takeProfit],o?.historyAvailable);
  table('Historia własnych fill / opłaty',s.history,['Instrument','Redukcja','Ilość','Cena','Opłata','Waluta','Czas transakcji'],i=>[i.instrument,i.reduction,i.quantity,i.price,i.fee,i.feeCurrency,i.tradeUtc],o?.historyAvailable);
  el('p',`Strategia: ${value(s.evaluation?.code)} | zakończone ${value(s.evaluation?.completed)} | wait ${value(s.evaluation?.wait)} | entry ${value(s.evaluation?.entry)} | exit ${value(s.evaluation?.exit)}. Ocena nie oznacza fill.`,data);
  const sol=s.inventory?.items?.find(i=>i.instrument==='SOL-EUR'&&i.protection!=='OBSERVED_PROTECTION'&&i.quantity>0);
  warning.textContent=sol?'UWAGA SOL — niechroniony własny stan: '+sol.protection+'. Risk hold nie jest zleceniem SL.':'SOL — brak wykazanego niechronionego stanu w tej obserwacji; nie jest to gwarancja ochrony.';
 }
 async function read(){if(reading)return;reading=true;const abort=new AbortController(),timer=setTimeout(()=>abort.abort(),5000);try{const r=await fetch('/api/naval/controller/status',{cache:'no-store',signal:abort.signal});if(!r.ok)throw new Error();status=await r.json();}catch{status={...status,stale:true,availability:'STATUS_NIEDOSTĘPNY',canStart:false};}finally{reading=false;clearTimeout(timer);render();}}
 async function command(a){if(pending.has(a)||buttons[a].disabled)return;pending.add(a);result.textContent='Wysłano '+a+' — wynik oczekujący / nieznany. Bez automatycznego ponowienia.';render();try{const r=await fetch('/api/naval/controller/'+a,{method:'POST',headers:{'Content-Type':'application/json','X-Apex-Local':'1'},body:JSON.stringify({commandId:crypto.randomUUID()})});const b=await r.json();result.textContent=b.outcome==='confirmed'?`Potwierdzono odpowiedź serwera: ${a} / ${value(b.confirmedMode)} / ${value(b.code)}`:`${value(b.outcome)}: ${value(b.code)} — nie potwierdzono wykonania. Nie ponawiaj w ciemno.`;}catch{result.textContent='pending-or-unknown — brak potwierdzenia; nie ponawiaj mutacji.';}finally{pending.delete(a);render();void read();}}
 for(const [a,name] of names){const b=el('button',name,actions);b.type='button';b.dataset.controllerAction=a;buttons[a]=b;b.addEventListener('click',()=>command(a));}
 function mount(){const slot=document.getElementById('okx-operator-slot');if(slot){slot.prepend(box);return true;}if(!location.pathname.startsWith('/naval')){(document.querySelector('.app .main')||document.body).prepend(box);return true;}return false;}
 if(!mount()){const observer=new MutationObserver(()=>{if(mount())observer.disconnect();});observer.observe(document.body,{childList:true,subtree:true});}
 render();void read();setInterval(read,2000);
})();
