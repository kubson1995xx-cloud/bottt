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
  const stage=element('p','',details);stage.id='okx-enrollment-stage';stage.setAttribute('role','status');
  const status=element('pre','Ładowanie…',details);status.id='okx-status';
  const nav=element('nav');nav.setAttribute('aria-label','OKX / Naval / panel');
  if(naval){const back=element('button','Wróć do widoku Naval',nav);back.type='button';back.addEventListener('click',()=>{const main=document.getElementById('main');main?.scrollIntoView({block:'start'});main?.focus({preventScroll:true});});}
  const link=element('a',naval?'Oryginalny panel po polsku':'Pięć widoków Naval / opcjonalne 3D',nav);link.href=naval?'/':'/naval/';
  function mount(){const slot=document.getElementById('okx-operator-slot');if(slot){slot.append(box);return true;}if(!naval){document.querySelector('.app .main')?.prepend(box);if(!box.isConnected)document.body.prepend(box);return true;}return false;}
  if(!mount()){const observer=new MutationObserver(()=>{if(mount())observer.disconnect();});observer.observe(document.body,{childList:true,subtree:true});}
  let state=null,healthy=false,polling=false;const pending=new Set();
  const names=[['configure','Połącz konto OKX'],['reconnect','Połącz ponownie — READ-ONLY'],['cancel','Anuluj łączenie / STOP'],['diagnose','Sprawdź zapis klucza lokalnie (bez sieci)'],['test-demo','Test połączenia DEMO / odczytaj saldo — tylko odczyt'],['connect','Starszy zakres — chronione Observe'],['disconnect','Rozłącz / zmień konto'],['baseline','Zatwierdź rzeczywistą bazę'],['arm','Uzbrój Execute (nie START)'],['start','START — osobne polecenie'],['stop','STOP / cofnij zgody'],['emergency','Emergency Stop — sprawdź zobowiązania'],['recover','Redukuj wybrany własny stan — bez START / bez ponowienia Unknown'],['refresh','Odśwież status']];
  function enrollmentHelp(code){
    if(!code||code==='NOT_STARTED')return 'Połącz konto OKX: jeden lokalny formularz Windows, DEMO domyślne, LIVE zablokowane, trzy maskowane pola. Połącz wykonuje wyłącznie podpisany odczyt; UID, salda i rynki są wykrywane automatycznie. Chroniona zgoda Windows jest wymagana przed zapisem DPAPI. Bez Arm i START.';
    if(code.includes('APPROVAL_TIMEOUT')||code.includes('APPROVAL_NATIVE_PENDING'))return code+' — zamknij poprzednie okno chronionej zgody Windows; klucza nie zapisano. Uruchom w zwykłej interaktywnej sesji Windows i ponów świadomie.';
    if(code.includes('APPROVAL_WINDOWS'))return code+' — oczekiwane okno „APEX — chroniona zgoda OKX” na chronionym pulpicie Windows. Użyj hasła tego samego użytkownika Windows, NIE PIN ani hasła OKX. Bez tej zgody klucz nie zostanie zapisany.';
    if(code.includes('UNPACK_UNSUPPORTED'))return code+' — metoda Windows Hello/karty nie dała obsługiwanych danych; wybierz hasło tego samego użytkownika Windows. Bez omijania chronionej zgody.';
    if(code.includes('AUTHENTICATION_REFUSED')||code.includes('SAME_USER')||code.includes('SESSION_REQUIRED'))return code+' — Windows odmówił weryfikacji tego samego użytkownika/sesji. Sprawdź lokalną metodę logowania; klucza nie zapisano.';
    if(code.includes('OS_ERROR')||code.includes('NATIVE_EXCEPTION'))return code+' — błąd natywnego okna, nie anulowanie. Zachowaj tylko ten kod; nie przesyłaj haseł ani plików prywatnych.';
    if(code.includes('SECRET_WINDOW'))return code+' — oczekiwane „APEX — lokalny klucz OKX”: API Key, Secret Key i Passphrase. Wyłącznie natywne pola, nie ta przeglądarka.';
    if(code.includes('PRESERVED'))return code+' — stan zachowany, możliwy częściowy zapis. Nie usuwaj katalogu, nie nadpisuj i nie powtarzaj w ciemno; użyj diagnozy zapisu.';
    if(code.includes('CANCELLED')||code.includes('EXPIRED'))return code+' — przerwano ten etap; brak potwierdzonego zapisu. Sprawdź lokalny status przed ponowieniem.';
    return code+' — wyłącznie READ-ONLY / STOP / nieuzbrojony; po restarcie wymagane jawne ponowne połączenie. Brak ownera, Arm i START.';
  }
  function options(target,items,empty){const previous=target.value;target.replaceChildren();for(const [value,text] of items){const option=element('option',text,target);option.value=value;}if(!items.length){const option=element('option',empty,target);option.value='';}if(items.some(([value])=>value===previous))target.value=previous;}
  function render(){
    const accounts=state?.accounts||[];
    const current=accounts.find(a=>a.scopeId===select.value),connected=accounts.some(a=>a.connected),busy=pending.size>0;
    options(recovery,(current?.inventory||[]).map(i=>[i.reservationId,`${i.instrument} | ${i.quantity} | ${i.state} | ${i.reservationId}`]),'Brak własnego stanu do redukcji');
    select.disabled=!healthy||busy||connected||!accounts.length;
    recovery.disabled=!healthy||busy||!recovery.value;
    reason.textContent=!healthy?'Status niepotwierdzony. Polecenia zwiększające ryzyko są niedostępne; odśwież status. STOP / Emergency / rozłączenie pozostają dostępne dla ostatniego znanego zakresu.':!accounts.length?'Brak konta OKX. Użyj „Połącz konto OKX” — trzy pola w lokalnym oknie Windows, DEMO domyślne.':current?.readOnly?'READ-ONLY / STOP / nieuzbrojony. Salda według walut, bez przeliczeń i bez finansowego ownera. Po restarcie lub wygaśnięciu odczytu użyj Połącz ponownie.':connected?'Połączony rachunek: przed zmianą zakresu lub konfiguracji użyj „Rozłącz”.':'Starszy zakres zachowany; konfiguracja nie jest dowodem połączenia ani gotowości START.';
    for(const b of actions.querySelectorAll('button')){
      const action=b.dataset.action,safety=['stop','emergency','disconnect'].includes(action);
      if(action==='cancel'){b.disabled=false;continue;}
      if(current?.readOnly&&['connect','baseline','arm','start','recover'].includes(action)){b.disabled=true;b.title='Konto tylko do odczytu — brak uprawnień aktywacji';continue;}
      if(action==='reconnect'&&!current?.readOnly){b.disabled=true;b.title='Starszy zakres: użyj istniejącego testu odczytu DEMO';continue;}
      const why=action==='refresh'?(polling?'Pobieranie statusu':''):action==='configure'?(busy?'Trwa lokalne polecenie':connected?'Najpierw rozłącz rachunek':state?.nativeAvailable===false?'Wymagany interaktywny Windows':''):!current?'Brak konta OKX':safety?'':!healthy?'Brak potwierdzonego statusu':busy?'Trwa lokalne polecenie':action==='test-demo'&&(current.environment!=='OkxDemo'||connected)?'Tylko DEMO, przed testem rozłącz aktywną sesję':action==='recover'&&!recovery.value?'Brak własnego stanu do redukcji':'';
      b.disabled=!!why;b.title=why;b.setAttribute('aria-describedby','okx-reason');
    }
    box.dataset.ready=current?.ready?'true':'false';box.dataset.profile=String(state?.productionProfileApproved===true);box.setAttribute('aria-busy',String(busy));
  }
  async function refresh(){
    if(polling)return;polling=true;render();const abort=new AbortController(),timer=setTimeout(()=>abort.abort(),5000);
    try{
      const r=await fetch('/api/naval/okx/status',{cache:'no-store',signal:abort.signal});if(!r.ok)throw new Error('HTTP '+r.status);
      const next=await r.json();if(!next||!Array.isArray(next.accounts))throw new Error('Niepoprawna odpowiedź statusu');
      state=next;healthy=true;options(select,state.accounts.map(a=>[a.scopeId,`${a.environment==='Live'?'LIVE':'DEMO'} | UID ${a.uid} | ${a.quote||'waluty osobno / READ-ONLY'} | ${a.scopeId}`]),'Brak konta OKX');
      const current=state.accounts.find(a=>a.scopeId===select.value);
      stage.textContent=enrollmentHelp(state.enrollmentStage);
      status.textContent=(current?JSON.stringify(current,null,2):'Brak konta. Połącz je w lokalnym formularzu Windows. Żadne prywatne połączenie nie jest uruchomione.')+'\nMagazyn starszych zakresów: '+(state.unboundStoreState||'status nieznany')+'\nAkceptowane read_only albo read_only,trade, wyłącznie odczyt. To nie P&L, baza kapitału ani zgoda na handel. Salda walut są oddzielne; brak salda oznacza brak obserwacji, nie wymyślone zero. Brak automatycznego rynku i USD/USDT. UI nie oferuje rotacji ani nadpisywania istniejących danych.';
    }catch(e){healthy=false;status.textContent='Status niedostępny — '+(e.name==='AbortError'?'przekroczono 5 s':e.message)+'. Nie jest to potwierdzenie stanu rachunku.';}
    finally{clearTimeout(timer);polling=false;render();}
  }
  async function command(action){
    if(action==='refresh'){await refresh();return;}
    const scope=select.value,reservationId=recovery.value;if(!['configure','cancel'].includes(action)&&!scope){result.textContent='Brak konta OKX — polecenie nie zostało wysłane.';return;}
    pending.add(action);render();result.textContent='Wysłano '+action+'. Oczekiwanie na lokalną interakcję / wynik. Formularz i zgody są w osobnym oknie Windows „APEX”; sprawdź pasek zadań / Alt+Tab. Nie ponawiaj polecenia.';
    const abort=new AbortController(),timer=setTimeout(()=>abort.abort(),390000);
    try{
      const url=['configure','cancel'].includes(action)?'/api/naval/okx/'+action:`/api/naval/okx/${encodeURIComponent(scope)}/${action}`;
      const payload={commandId:crypto.randomUUID()};if(action==='recover')payload.reservationId=reservationId;
      const response=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json','X-Apex-Local':'1'},body:JSON.stringify(payload),signal:abort.signal});
      let body;try{body=await response.json();}catch{body=null;}
      const code=body?.code||body?.reasonCode||'HTTP '+response.status;
      result.textContent=enrollmentHelp(code)+(code.includes('UnsafeAccountConfig')?' Test wymaga bezpiecznego cash SPOT/IP bez pożyczek oraz dokładnie read_only albo read_only,trade. Withdraw i inne uprawnienia są odrzucane. Trade samo w sobie nie daje zgody na handel.':'')+' — brak automatycznego START ani ponownej wysyłki.';
    }catch(e){result.textContent='Niepewny wynik polecenia '+action+'. Sprawdź stan; nie ponawiaj w ciemno. '+(e.name==='AbortError'?'Upłynął czas oczekiwania.':e.message);}
    finally{clearTimeout(timer);pending.delete(action);render();void refresh();}
  }
  for(const [action,text] of names){const b=element('button',text,actions);b.type='button';b.dataset.action=action;b.addEventListener('click',()=>command(action));}
  select.addEventListener('change',()=>{result.textContent='Wybrano inny zakres. Wybór nie uruchamia połączenia ani handlu.';render();});
  recovery.addEventListener('change',render);
  render();void refresh();setInterval(refresh,2000);
})();
