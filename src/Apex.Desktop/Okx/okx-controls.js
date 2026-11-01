/* Local operator only. No credentials, permission grants or trading state in this document. */
'use strict';
(() => {
  if(document.getElementById('okx-operator'))return;
  const naval=location.pathname.startsWith('/naval');
  const css=document.createElement('link');css.rel='stylesheet';css.href='/okx-controls.css';document.head.append(css);
  const box=document.createElement('section');box.id='okx-operator';box.className='okx-operator-panel';box.tabIndex=-1;box.setAttribute('aria-label','OKX — lokalny operator');
  function element(tag,text,parent=box){const e=document.createElement(tag);if(text)e.textContent=text;parent.append(e);return e;}
  element('h2','OKX — lokalny operator');
  element('p','Rzeczywista konfiguracja i stan hosta OKX — niezależne od historycznej, syntetycznej wizualizacji Naval. Samo otwarcie panelu nie łączy rachunku ani nie uruchamia handlu.');
  const details=element('details');details.open=true;element('summary','Konfiguracja i polecenia OKX',details);
  element('p','Cash SPOT ≤1x, bez pożyczek, feeType=1: opłaty tylko w quote. TP/SL jest warunkowe po fill i może zawieść. Po anulowaniu i nieudanej/Unknown sprzedaży pozostaje niechroniony stan bez gwarantowanego czasu odzyskania. STOP nie oznacza sprzedaży. LIVE domyślnie OFF; osobne uzbrojenie i START. Prywatne DEMO wymaga Twojej lokalnej weryfikacji.',details);
  const label=element('label','Rachunek / środowisko OKX: ',details),select=element('select',null,label);select.id='okx-scope';
  const actions=element('div',null,details);actions.className='okx-actions';
  const recoveryLabel=element('label','Dokładny własny stan do redukcji: ',details),recovery=element('select',null,recoveryLabel);recovery.id='okx-reservation';
  const reason=element('p','Pobieranie stanu hosta. Polecenia zakresu czekają na poprawny odczyt.',details);reason.id='okx-reason';
  const result=element('p','',details);result.id='okx-result';result.setAttribute('role','status');result.setAttribute('aria-live','polite');
  const status=element('pre','Ładowanie…',details);status.id='okx-status';
  const nav=element('nav');nav.setAttribute('aria-label','OKX / Naval / panel');
  if(naval){const back=element('button','Wróć do widoku Naval',nav);back.type='button';back.addEventListener('click',()=>{const main=document.getElementById('main');main?.scrollIntoView({block:'start'});main?.focus({preventScroll:true});});}
  const link=element('a',naval?'Oryginalny panel po polsku':'Pięć widoków Naval / opcjonalne 3D',nav);link.href=naval?'/':'/naval/';
  function mount(){const slot=document.getElementById('okx-operator-slot');if(slot){slot.append(box);return true;}if(!naval){document.querySelector('.app .main')?.prepend(box);if(!box.isConnected)document.body.prepend(box);return true;}return false;}
  if(!mount()){const observer=new MutationObserver(()=>{if(mount())observer.disconnect();});observer.observe(document.body,{childList:true,subtree:true});}
  let state=null,healthy=false,polling=false;const pending=new Set();
  const names=[['configure','Dodaj konto — natywny formularz'],['connect','Połącz — chronione Observe'],['disconnect','Rozłącz / zmień konto'],['baseline','Zatwierdź rzeczywistą bazę'],['arm','Uzbrój Execute (nie START)'],['start','START — osobne polecenie'],['stop','STOP / cofnij zgody'],['emergency','Emergency Stop — sprawdź zobowiązania'],['recover','Redukuj wybrany własny stan — bez START / bez ponowienia Unknown'],['refresh','Odśwież status']];
  function options(target,items,empty){const previous=target.value;target.replaceChildren();for(const [value,text] of items){const option=element('option',text,target);option.value=value;}if(!items.length){const option=element('option',empty,target);option.value='';}if(items.some(([value])=>value===previous))target.value=previous;}
  function render(){
    const accounts=state?.accounts||[];
    const current=accounts.find(a=>a.scopeId===select.value),connected=accounts.some(a=>a.connected),busy=pending.size>0;
    options(recovery,(current?.inventory||[]).map(i=>[i.reservationId,`${i.instrument} | ${i.quantity} | ${i.state} | ${i.reservationId}`]),'Brak własnego stanu do redukcji');
    select.disabled=!healthy||busy||connected||!accounts.length;
    recovery.disabled=!healthy||busy||!recovery.value;
    reason.textContent=!healthy?'Status niepotwierdzony. Polecenia zwiększające ryzyko są niedostępne; odśwież status. STOP / Emergency / rozłączenie pozostają dostępne dla ostatniego znanego zakresu.':!accounts.length?'Brak konta OKX. Użyj „Dodaj konto” — otworzy się osobne natywne okno Windows.':connected?'Połączony rachunek: przed zmianą zakresu lub konfiguracji użyj „Rozłącz”.':'Zakres skonfigurowany, nie jest dowodem połączenia, uzbrojenia ani gotowości START. Odmowę backendu pokazujemy poniżej.';
    for(const b of actions.querySelectorAll('button')){
      const action=b.dataset.action,safety=['stop','emergency','disconnect'].includes(action);
      const why=action==='refresh'?(polling?'Pobieranie statusu':''):action==='configure'?(busy?'Trwa lokalne polecenie':connected?'Najpierw rozłącz rachunek':state?.nativeAvailable===false?'Wymagany interaktywny Windows':''):!current?'Brak konta OKX':safety?'':!healthy?'Brak potwierdzonego statusu':busy?'Trwa lokalne polecenie':action==='recover'&&!recovery.value?'Brak własnego stanu do redukcji':'';
      b.disabled=!!why;b.title=why;b.setAttribute('aria-describedby','okx-reason');
    }
    box.dataset.ready=current?.ready?'true':'false';box.dataset.profile=String(state?.productionProfileApproved===true);box.setAttribute('aria-busy',String(busy));
  }
  async function refresh(){
    if(polling)return;polling=true;render();const abort=new AbortController(),timer=setTimeout(()=>abort.abort(),5000);
    try{
      const r=await fetch('/api/naval/okx/status',{cache:'no-store',signal:abort.signal});if(!r.ok)throw new Error('HTTP '+r.status);
      const next=await r.json();if(!next||!Array.isArray(next.accounts))throw new Error('Niepoprawna odpowiedź statusu');
      state=next;healthy=true;options(select,state.accounts.map(a=>[a.scopeId,`${a.environment==='Live'?'LIVE':'DEMO'} | UID ${a.uid} | ${a.quote} | ${a.scopeId}`]),'Brak konta OKX');
      const current=state.accounts.find(a=>a.scopeId===select.value);
      status.textContent=current?JSON.stringify(current,null,2):'Brak konta. Dodaj je w lokalnym formularzu Windows. Żadne prywatne połączenie nie jest uruchomione.';
    }catch(e){healthy=false;status.textContent='Status niedostępny — '+(e.name==='AbortError'?'przekroczono 5 s':e.message)+'. Nie jest to potwierdzenie stanu rachunku.';}
    finally{clearTimeout(timer);polling=false;render();}
  }
  async function command(action){
    if(action==='refresh'){await refresh();return;}
    const scope=select.value,reservationId=recovery.value;if(action!=='configure'&&!scope){result.textContent='Brak konta OKX — polecenie nie zostało wysłane.';return;}
    pending.add(action);render();result.textContent='Wysłano '+action+'. Oczekiwanie na lokalną interakcję / wynik. Formularz i zgody są w osobnym oknie Windows „APEX”; sprawdź pasek zadań / Alt+Tab. Nie ponawiaj polecenia.';
    const abort=new AbortController(),timer=setTimeout(()=>abort.abort(),390000);
    try{
      const url=action==='configure'?'/api/naval/okx/configure':`/api/naval/okx/${encodeURIComponent(scope)}/${action}`;
      const payload={commandId:crypto.randomUUID()};if(action==='recover')payload.reservationId=reservationId;
      const response=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json','X-Apex-Local':'1'},body:JSON.stringify(payload),signal:abort.signal});
      let body;try{body=await response.json();}catch{body=null;}
      result.textContent=(body?.code||body?.reasonCode||'HTTP '+response.status)+' — brak automatycznego START ani ponownej wysyłki.';
    }catch(e){result.textContent='Niepewny wynik polecenia '+action+'. Sprawdź stan; nie ponawiaj w ciemno. '+(e.name==='AbortError'?'Upłynął czas oczekiwania.':e.message);}
    finally{clearTimeout(timer);pending.delete(action);render();void refresh();}
  }
  for(const [action,text] of names){const b=element('button',text,actions);b.type='button';b.dataset.action=action;b.addEventListener('click',()=>command(action));}
  select.addEventListener('change',()=>{result.textContent='Wybrano inny zakres. Wybór nie uruchamia połączenia ani handlu.';render();});
  recovery.addEventListener('change',render);
  render();void refresh();setInterval(refresh,2000);
})();
