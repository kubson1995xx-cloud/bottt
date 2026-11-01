/* Local operator controls only. Credentials and activation never enter this document. */
'use strict';
(() => {
  const box=document.createElement('section'); box.id='okx-operator'; box.className='card';
  const title=document.createElement('h2');title.textContent='OKX — lokalny operator (kandydat inżynierski)';box.append(title);
  const notice=document.createElement('p');notice.textContent='Cash SPOT bez pożyczek, feeType=1: opłaty tylko w quote. TP/SL jest warunkowe po fill i może zawieść. Po anulowaniu i nieudanej/Unknown sprzedaży pozostaje niechroniony stan bez gwarantowanego czasu odzyskania. STOP nie oznacza sprzedaży. LIVE domyślnie OFF; osobne uzbrojenie i START. Prywatne DEMO wymaga Twojej lokalnej weryfikacji.';box.append(notice);
  const label=document.createElement('label');label.textContent='Rachunek / środowisko: ';const select=document.createElement('select');select.id='okx-scope';label.append(select);box.append(label);
  const actions=document.createElement('div');box.append(actions);
  const recoveryLabel=document.createElement('label');recoveryLabel.textContent='Dokładny własny stan do redukcji: ';const recovery=document.createElement('select');recovery.id='okx-reservation';recoveryLabel.append(recovery);box.append(recoveryLabel);
  const result=document.createElement('p');result.id='okx-result';result.setAttribute('role','status');box.append(result);
  const status=document.createElement('pre');status.id='okx-status';status.setAttribute('aria-live','polite');box.append(status);
  const link=document.createElement('a');link.href=location.pathname.startsWith('/naval')?'/':'/naval/';link.textContent=location.pathname.startsWith('/naval')?'Oryginalny panel po polsku':'Pięć widoków Naval / opcjonalne 3D';box.append(link);
  document.body.prepend(box);
  let busy=false;
  async function refresh(){
    try{
      const r=await fetch('/api/naval/okx/status',{cache:'no-store'});if(!r.ok)throw new Error('HTTP '+r.status);
      const state=await r.json();const previous=select.value;select.replaceChildren();
      for(const a of state.accounts){const option=document.createElement('option');option.value=a.scopeId;option.textContent=`${a.environment==='Live'?'LIVE':'DEMO'} | UID ${a.uid} | ${a.quote} | ${a.scopeId}`;select.append(option);}
      if([...select.options].some(o=>o.value===previous))select.value=previous;
      const current=state.accounts.find(a=>a.scopeId===select.value);
      const owned=recovery.value;recovery.replaceChildren();for(const item of current?.inventory||[]){const option=document.createElement('option');option.value=item.reservationId;option.textContent=`${item.instrument} | ${item.quantity} | ${item.state} | ${item.reservationId}`;recovery.append(option);}if([...recovery.options].some(o=>o.value===owned))recovery.value=owned;
      select.disabled=busy||state.accounts.some(a=>a.connected); // Disconnect before changing the active account/environment.
      status.textContent=current?JSON.stringify(current,null,2):'Brak konta. Dodaj je w lokalnym formularzu Windows. Żadne prywatne połączenie nie jest uruchomione.';
      for(const b of actions.querySelectorAll('button'))b.disabled=(busy&&!['stop','emergency'].includes(b.dataset.action))||(b.dataset.action!=='configure'&&!current)||(b.dataset.action==='recover'&&!recovery.value);
      box.dataset.ready=current?.ready?'true':'false';box.dataset.profile=String(state.productionProfileApproved);
    }catch(e){status.textContent='Status niedostępny. STOP i Emergency Stop pozostają oddzielnymi poleceniami. '+e.message;}
  }
  async function command(action){
    const scope=select.value;const reservationId=recovery.value;if(action!=='configure'&&!scope)return;
    busy=true;result.textContent='Oczekiwanie na lokalną interakcję / wynik. Zgody udzielaj tylko w natywnym oknie Windows.';await refresh();
    try{
      const url=action==='configure'?'/api/naval/okx/configure':`/api/naval/okx/${encodeURIComponent(scope)}/${action}`;
      const payload={commandId:crypto.randomUUID()};if(action==='recover')payload.reservationId=reservationId;
      const response=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json','X-Apex-Local':'1'},body:JSON.stringify(payload)});
      const body=await response.json();result.textContent=(body.code||body.reasonCode||'HTTP '+response.status)+' — brak automatycznego START ani ponownej wysyłki.';
    }catch(e){result.textContent='Niepewny wynik polecenia. Sprawdź stan; nie ponawiaj w ciemno. '+e.message;}
    finally{busy=false;await refresh();}
  }
  for(const [action,text] of [['configure','Dodaj konto — natywny formularz'],['connect','Połącz — chronione Observe'],['disconnect','Rozłącz / zmień konto'],['baseline','Zatwierdź rzeczywistą bazę'],['arm','Uzbrój Execute (nie START)'],['start','START — osobne polecenie'],['stop','STOP / cofnij zgody'],['emergency','Emergency Stop — sprawdź zobowiązania'],['recover','Redukuj wybrany własny stan — bez START / bez ponowienia Unknown']]){
    const button=document.createElement('button');button.type='button';button.dataset.action=action;button.textContent=text;button.addEventListener('click',()=>command(action));actions.append(button);
  }
  select.addEventListener('change',()=>{result.textContent='Wybrano inny zakres. Najpierw rozłącz poprzedni rachunek; wybór nie uruchamia połączenia ani handlu.';refresh();});
  refresh();setInterval(refresh,2000);
})();
