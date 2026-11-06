/* APEX local Windows edition panel. Real data only (status + read-only local dashboard);
   missing/stale data renders "—". Commands use the existing closed routes unchanged. */
'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const PAGES = [['dashboard','Pulpit','Najważniejsze informacje w jednym miejscu.'],['positions','Pozycje','Własne pozycje bota — tylko odczyt.'],['strategies','Strategie','Stała strategia bota — tylko odczyt.'],['history','Historia','Własne transakcje bota z opłatami.'],['settings','Ustawienia','Limity ryzyka i połączenie — tylko odczyt.']];
  const ICONS = {dashboard:'M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z',positions:'M3 3v18h18M7 16l4-4 3 3 6-8',strategies:'M12 3v3m0 12v3M3 12h3m12 0h3M12 7a5 5 0 1 0 0 10 5 5 0 1 0 0-10',history:'M12 3a9 9 0 1 0 0 18 9 9 0 1 0 0-18M12 7v5l3 2',settings:'M12 9a3 3 0 1 0 0 6 3 3 0 1 0 0-6M4 8l-2 4 2 4 3 .5 2 3.5h6l2-3.5 3-.5 2-4-2-4-3-.5L15 4H9L7 7.5z'};
  const STEPS = [
    ['create-local','Nowy lokalny bot','Tylko pierwsze uruchomienie: klucz DEMO, potwierdzenie, hasło Windows'],
    ['attach-local','Dołącz bota z tego PC','Hasło Windows (chroniony ekran)'],
    ['connect','Połącz z OKX (Observe)','Hasło Windows; odczyt konta'],
    ['prepare','Przygotuj BTC/ETH/SOL-EUR','Bez handlu'],
    ['arm','Uzbrój (Execute)','Hasło Windows; jeszcze bez START'],
    ['start','START','Bot zaczyna działać według strategii']];
  const STOP_CONFIRM = 'Zatrzymać bota (STOP)?\n\nSTOP cofa zgody i zatrzymuje nowe wejścia.\nSTOP NIE sprzedaje otwartych pozycji.';
  let page = 'dashboard', status = null, dash = null, healthy = false, polling = false, toastTimer = 0, vpsLoaded = false;
  const pending = new Set(), events = new Map();
  let seen = {lastResult: null, mode: null, completed: null, economic: null, obligations: ''};

  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
  const hide = (node, h) => node.toggleAttribute('hidden', !!h);
  const num = v => (typeof v === 'number' && isFinite(v)) ? v : null;
  function fmt(v, max = 8, min = 0) { v = num(v); return v == null ? '—' : v.toLocaleString('pl-PL', {minimumFractionDigits: min, maximumFractionDigits: max}); }
  const eur = v => num(v) == null ? '—' : fmt(v, 2, 2) + ' EUR';
  const signed = v => num(v) == null ? '—' : (v > 0 ? '+' : v < 0 ? '−' : '') + fmt(Math.abs(v), 2, 2) + ' EUR';
  const time = t => { const d = t ? new Date(t) : null; return d && !isNaN(d) ? d.toLocaleString('pl-PL', {day:'2-digit', month:'2-digit', year:'numeric', hour:'2-digit', minute:'2-digit', second:'2-digit'}) : '—'; };
  const clock = t => { const d = t ? new Date(t) : null; return d && !isNaN(d) ? d.toLocaleTimeString('pl-PL', {hour:'2-digit', minute:'2-digit', second:'2-digit'}) : '—'; };
  function setText(id, text, stale) { const n = $(id); n.textContent = text; n.classList.toggle('stale', !!stale); }

  function account() { return (status?.accounts || []).find(a => a.environment === 'OkxDemo' && !a.readOnly) || null; }
  function staleObservation() {
    const at = Date.parse(dash?.snapshot?.observation?.captureUtc || '');
    return !Number.isFinite(at) || Date.now() < at || Date.now() - at > 120000;
  }
  function current() { return healthy && dash?.snapshotState === 'CURRENT' && dash.snapshot?.connected === true && !staleObservation() ? dash.snapshot : null; }

  function icon(k) { const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('aria-hidden', 'true'); const p = document.createElementNS('http://www.w3.org/2000/svg', 'path'); p.setAttribute('d', ICONS[k]); s.append(p); return s; }
  function buildNav() {
    for (const id of ['desktop-nav', 'mobile-nav']) {
      const nav = $(id); nav.replaceChildren();
      for (const [key, label] of PAGES) { const b = el('button', 'nav-btn' + (key === page ? ' active' : '')); b.type = 'button'; b.dataset.page = key; b.append(icon(key), el('span', null, label)); b.addEventListener('click', () => navigate(key)); nav.append(b); }
    }
  }
  function navigate(p) {
    if (!PAGES.some(x => x[0] === p)) p = 'dashboard';
    page = p; for (const v of document.querySelectorAll('.view')) v.classList.toggle('active', v.id === p);
    const meta = PAGES.find(x => x[0] === p); $('page-title').textContent = meta[1]; $('page-subtitle').textContent = meta[2];
    if (location.hash !== '#' + p) history.replaceState(null, '', '#' + p);
    buildNav();
  }
  function toast(message) { if (!$('notify-toggle').checked) return; const t = $('toast'); t.textContent = message; t.classList.add('show'); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove('show'), 3200); }

  function addEvent(key, at, title, sub, warn) { if (!events.has(key)) events.set(key, {at: at || new Date().toISOString(), title, sub, warn: !!warn}); }
  function collectEvents() {
    const snap = current(), acct = account();
    for (const f of snap?.history?.items || []) addEvent('fill:' + f.instrument + f.tradeUtc + f.quantity + f.reduction, f.tradeUtc, (f.reduction ? 'SPRZEDAŻ ' : 'KUPNO ') + f.instrument, fmt(f.quantity) + ' po ' + fmt(f.price) + ' EUR · opłata ' + fmt(f.fee) + ' ' + f.feeCurrency, false);
    if (!healthy) return;
    if (status.lastResult && status.lastResult !== seen.lastResult) { if (seen.lastResult !== null || status.lastResult !== 'NOT_CONNECTED') addEvent('cmd:' + Date.now() + status.lastResult, null, 'Polecenie: ' + status.lastResult, 'Wynik lokalnego polecenia', /REFUSED|DENIED|CANCEL|STOP/.test(status.lastResult)); seen.lastResult = status.lastResult; }
    const mode = acct?.mode || null; if (mode && mode !== seen.mode) { if (seen.mode !== null) addEvent('mode:' + Date.now(), null, 'Tryb bota: ' + mode, mode === 'RUNNING' ? 'Bot działa według strategii' : 'Bot zatrzymany', mode !== 'RUNNING'); seen.mode = mode; }
    const ev = dash?.evaluation; if (ev && ev.completed !== seen.completed) { if (ev.completed > 0) addEvent('eval:' + ev.completed, null, 'Ocena strategii #' + ev.completed + ': ' + ev.code, 'WAIT ' + ev.wait + ' · wejścia ' + ev.entry + ' · wyjścia ' + ev.exit, false); seen.completed = ev.completed; }
    const econ = snap?.observation?.economicUtc; if (econ && econ !== seen.economic) { addEvent('rec:' + econ, econ, 'Rekonsyliacja z OKX', 'Saldo, zlecenia i ochrona porównane z księgą bota', false); seen.economic = econ; }
    const ob = (dash?.obligations || []).join(','); if (ob !== seen.obligations) { if (ob) addEvent('ob:' + ob, null, 'Zobowiązanie: ' + ob, 'Bot wstrzymuje nowe wejścia do wyjaśnienia', true); seen.obligations = ob; }
  }
  function renderFeed() {
    const feed = $('activity-feed'); feed.replaceChildren();
    const rows = [...events.values()].sort((a, b) => new Date(b.at) - new Date(a.at)).slice(0, 8);
    if (!rows.length) { feed.append(el('div', 'empty', 'Brak zdarzeń — panel pokazuje wyłącznie rzeczywiste zdarzenia bota.')); return; }
    for (const r of rows) { const row = el('div', 'feed-row'); const main = el('div'); main.append(el('div', 'feed-main', r.title), el('div', 'feed-sub', r.sub)); row.append(el('span', 'feed-icon' + (r.warn ? ' warn' : ''), r.warn ? '!' : '✓'), main, el('span', 'feed-time', clock(r.at))); feed.append(row); }
  }

  function mark(instrument) { const m = (dash?.marks || []).find(x => x.instrument === instrument); return num(m?.bid); }
  function protectionText(inv, snap) {
    const live = (snap?.protection?.items || []).find(p => p.instrument === inv.instrument && p.state === 'live');
    if (inv.protection === 'OBSERVED_PROTECTION' && live) return 'Aktywna · SL ' + fmt(live.stop) + ' / TP ' + fmt(live.takeProfit);
    return ({OBSERVED_PROTECTION:'Aktywna', PROTECTION_GAP:'BRAK OCHRONY — bot wstrzymuje wejścia', NO_INVENTORY:'Brak ilości (zlecenie w toku)', UNPROTECTED_QUARANTINED_FULL_LOSS_RESERVED:'Bez ochrony (kwarantanna, pełna strata zarezerwowana)'})[inv.protection] || inv.protection;
  }
  function positionRows(full) {
    const body = $(full ? 'positions-full' : 'positions-dash'); body.replaceChildren(); const snap = current(); const cols = full ? 8 : 6;
    const empty = text => { const tr = el('tr'), td = el('td', 'empty', text); td.colSpan = cols; tr.append(td); body.append(tr); };
    if (!snap) { empty('— Brak połączenia z OKX (Observe): pozycje niedostępne.'); return; }
    const items = (snap.inventory?.items || []).filter(i => i.quantity > 0 || i.pending > 0);
    if (!items.length) { empty('Brak otwartych pozycji bota.'); return; }
    for (const i of items) {
      const bid = mark(i.instrument), avg = i.quantity > 0 ? i.allocatedCost / i.quantity : null, unreal = bid != null && i.quantity > 0 ? i.quantity * bid - i.allocatedCost : null;
      const tr = el('tr'); const sym = el('div', 'symbol'); const base = i.instrument.split('-')[0];
      sym.append(el('span', 'coin ' + base.toLowerCase(), base.slice(0, 1)), Object.assign(el('div'), {}));
      sym.lastChild.append(el('b', null, i.instrument), el('small', null, 'SPOT · własna'));
      const cells = full ? [fmt(i.quantity), fmt(i.pending), eur(i.allocatedCost), avg == null ? '—' : fmt(avg, 4), bid == null ? '—' : fmt(bid, 4), signed(unreal), protectionText(i, snap)]
                         : [fmt(i.quantity), avg == null ? '—' : fmt(avg, 4), bid == null ? '—' : fmt(bid, 4), signed(unreal), protectionText(i, snap)];
      const tdSym = el('td'); tdSym.append(sym); tr.append(tdSym);
      cells.forEach((c, n) => { const td = el('td', null, c); if (c === signed(unreal) && unreal != null) td.className = unreal >= 0 ? 'pos' : 'neg'; if (n === cells.length - 1 && i.protection !== 'OBSERVED_PROTECTION') td.className = 'neg'; tr.append(td); });
      body.append(tr);
    }
  }
  function historyRows() {
    const body = $('history-rows'); body.replaceChildren(); const snap = current();
    const empty = text => { const tr = el('tr'), td = el('td', 'empty', text); td.colSpan = 8; tr.append(td); body.append(tr); };
    if (!snap) { empty('— Brak połączenia z OKX (Observe): historia niedostępna.'); $('history-desc').textContent = 'Własne transakcje bota (fills) z opłatami. Tylko odczyt.'; return; }
    const h = snap.history || {items: [], total: 0, truncated: false};
    $('history-desc').textContent = 'Własne transakcje bota (fills) z opłatami. Tylko odczyt.' + (h.truncated ? ' Pokazano ostatnie ' + h.items.length + ' z ' + h.total + '.' : '');
    if (!h.items.length) { empty('Brak transakcji bota.'); return; }
    const quote = dash?.limits?.quote || 'EUR';
    for (const f of h.items) {
      const gross = f.quantity * f.price, base = f.instrument.split('-')[0], feeQuote = f.feeCurrency === quote;
      const net = feeQuote ? (f.reduction ? signed(gross - f.fee) : signed(-(gross + f.fee))) : (f.reduction ? signed(gross) + ' · −' + fmt(f.fee) + ' ' + f.feeCurrency : signed(-gross) + ' · ' + fmt(f.quantity - (f.feeCurrency === base ? f.fee : 0)) + ' ' + base + ' netto' + (f.feeCurrency === base ? '' : ' · −' + fmt(f.fee) + ' ' + f.feeCurrency));
      const tr = el('tr'); [time(f.tradeUtc), f.instrument, f.reduction ? 'SPRZEDAŻ' : 'KUPNO', fmt(f.quantity), fmt(f.price, 4), fmt(gross, 2, 2), fmt(f.fee) + ' ' + f.feeCurrency, net].forEach((c, n) => { const td = el('td', null, c); if (n === 1) td.className = 'symbol-cell'; tr.append(td); }); body.append(tr);
    }
  }
  // Realized result from visible own fills only (average cost per instrument). No series is invented.
  function realizedSeries(snap) {
    const quote = dash?.limits?.quote || 'EUR', book = {}, points = []; let total = 0, skipped = 0;
    const incomplete = () => ({points: [], skipped: 1, truncated: !!snap?.history?.truncated});
    if (snap?.history?.truncated) return incomplete();
    for (const f of [...(snap?.history?.items || [])].reverse()) {
      const b = book[f.instrument] ||= {qty: 0, cost: 0}; const base = f.instrument.split('-')[0];
      if (f.feeCurrency !== quote && f.feeCurrency !== base) return incomplete();
      if (!f.reduction) { b.qty += f.quantity - (f.feeCurrency === base ? f.fee : 0); b.cost += f.quantity * f.price + (f.feeCurrency === quote ? f.fee : 0); continue; }
      const sold = f.quantity + (f.feeCurrency === base ? f.fee : 0);
      if (b.qty <= 0 || sold > b.qty) return incomplete();
      const avg = b.cost / b.qty, proceeds = f.quantity * f.price - (f.feeCurrency === quote ? f.fee : 0);
      total += proceeds - avg * sold; b.cost -= avg * sold; b.qty -= sold; points.push({at: f.tradeUtc, v: total});
    }
    return {points, skipped, truncated: !!snap?.history?.truncated};
  }
  function drawChart() {
    const snap = current(), svg = $('equity-chart'); svg.replaceChildren();
    const s = snap ? realizedSeries(snap) : {points: [], skipped: 0, truncated: false};
    hide($('chart-wrap'), s.points.length === 0); hide($('chart-empty'), s.points.length > 0);
    $('chart-empty').textContent = snap ? s.skipped ? '— Niepełna historia lub nieobsługiwana waluta opłaty — wynik nie jest obliczany.' : 'Brak zamkniętych transakcji bota — wykres pojawi się po pierwszym wyjściu.' : '— Brak aktualnych danych z OKX (Observe): wynik niedostępny.';
    $('chart-note').textContent = s.points.length ? 'Szacunek z widocznych własnych transakcji (średni koszt, opłaty uwzględnione); nie jest wyceną portfela.' + (s.truncated || s.skipped ? ' Okno transakcji jest niepełne — wynik może być niepełny.' : '') : '';
    if (!s.points.length) { setText('chart-value', '—', true); $('chart-change').textContent = ''; return; }
    const last = s.points.at(-1).v; setText('chart-value', signed(last)); $('chart-change').textContent = s.points.length + ' zamkn. wyjść'; $('chart-change').className = last >= 0 ? 'pos' : 'neg';
    const vals = [0, ...s.points.map(p => p.v)], w = 760, h = 230, pad = 14, lo = Math.min(...vals), hi = Math.max(...vals), span = hi - lo || 1;
    const pts = vals.map((v, i) => [pad + i * (w - 2 * pad) / Math.max(1, vals.length - 1), pad + (hi - v) * (h - 2 * pad) / span]);
    const ns = 'http://www.w3.org/2000/svg', mk = (t, a) => { const e = document.createElementNS(ns, t); for (const k in a) e.setAttribute(k, a[k]); svg.append(e); return e; };
    const zero = pad + (hi - 0) * (h - 2 * pad) / span; mk('line', {x1: 0, y1: zero.toFixed(1), x2: w, y2: zero.toFixed(1), stroke: '#2b3b4d', 'stroke-dasharray': '4 7'});
    const d = pts.map((p, i) => (i ? 'L' : 'M') + p[0].toFixed(1) + ' ' + p[1].toFixed(1)).join(' ');
    mk('path', {d, fill: 'none', stroke: last >= 0 ? '#55e2b4' : '#ff7e8d', 'stroke-width': 3, 'stroke-linejoin': 'round', 'stroke-linecap': 'round'});
    const e = pts.at(-1); mk('circle', {cx: e[0], cy: e[1], r: 5.5, fill: last >= 0 ? '#55e2b4' : '#ff7e8d', stroke: '#0c1928', 'stroke-width': 3});
  }
  function renderCards() {
    const snap = current(), L = dash?.limits, E = dash?.equity;
    const quote = snap?.capital?.quote || 'EUR';
    setText('capital-cash', snap ? eur(snap.capital?.cash) : '—', !snap);
    $('capital-foot').textContent = snap ? 'Wolne: ' + eur(snap.capital?.free) + ' · w pozycjach (koszt): ' + eur(snap.capital?.allocatedCost) : 'Gotówka bota — dostępna po połączeniu (Observe)';
    const balances = snap?.balances?.items || [], q = balances.find(b => b.currency === quote), excluded = balances.filter(b => b.excluded && num(b.total) > 0);
    setText('account-eur', snap && q ? fmt(q.total, 2, 2) + ' ' + quote : '—', !(snap && q));
    $('account-excluded').textContent = !snap ? 'Wykluczone waluty: —' : excluded.length ? 'Wykluczone (nie kapitał bota, nie sprzedawane): ' + excluded.map(b => b.currency + ' ' + fmt(b.total)).join(' · ') : 'Brak wykluczonych walut';
    const items = snap ? (snap.inventory?.items || []).filter(i => i.quantity > 0) : null;
    setText('open-count', items ? String(items.length) : '—', !items);
    const daily = snap && E && num(E.liquidationEquity) != null && num(E.dayStartEquity) != null ? E.liquidationEquity - E.dayStartEquity : null;
    hide($('daily-card'), daily == null);
    if (daily != null) { setText('daily-pnl', signed(daily)); $('daily-pnl').className = 'num ' + (daily >= 0 ? 'up' : 'down'); $('daily-foot').textContent = 'Wycena ' + eur(E.liquidationEquity) + ' · początek dnia ' + eur(E.dayStartEquity) + ' (UTC ' + (E.dayKey || '—') + ')'; }
    setText('daily-limit', snap && L ? fmt(L.dailyPct, 2) + '%' : '—', !(snap && L));
    $('daily-remaining').textContent = snap && E?.dailyLatch ? 'Limit dzienny osiągnięty — nowe wejścia wstrzymane' : snap && num(E?.dailyRemaining) != null ? 'Pozostało dziś: ' + eur(E.dailyRemaining) : 'Limit dzienny z konfiguracji bota';
  }
  function renderMarkets() {
    const box = $('markets'); box.replaceChildren(); const instruments = dash?.strategy?.instruments || ['BTC-EUR', 'ETH-EUR', 'SOL-EUR'], snap = current();
    for (const i of instruments) { const m = snap ? (dash?.marks || []).find(x => x.instrument === i) : null; const row = el('div', 'market-row'); row.append(el('b', null, i.replace('-', ' / ')), el('b', null, m && num(m.bid) != null ? fmt(m.bid, 4) + ' EUR' : '—'), el('span', 'muted', m && num(m.bid) != null ? clock(m.reconciledUtc) : 'brak wyceny')); box.append(row); }
  }
  function renderStrategy() {
    const s = dash?.strategy; $('strategy-name').textContent = s ? s.name + ' · ' + s.entryDays + '/' + s.exitDays + '/' + s.btcSmaDays : '—';
    $('strategy-title').textContent = s ? s.name + ' — wybicie dzienne (D1)' : '—';
    $('strategy-desc').textContent = s ? 'Wejście: zamknięcie dnia powyżej najwyższego maksimum z poprzednich ' + s.entryDays + ' dni, tylko gdy BTC zamyka się powyżej swojej średniej ' + s.btcSmaDays + '-dniowej. Wyjście: zamknięcie poniżej najniższego minimum z poprzednich ' + s.exitDays + ' dni albo SL/TP. Wybór strategii jest stały; panel jej nie zmienia.' : '—';
    const kv = $('strategy-params'); kv.replaceChildren();
    if (s) for (const [k, v] of [['Wejście (dni)', s.entryDays], ['Wyjście (dni)', s.exitDays], ['Filtr BTC — średnia (dni)', s.btcSmaDays], ['Stop loss', s.stopAtr + ' × ATR(' + s.atrDays + ')'], ['Take profit', s.takeProfitR + 'R'], ['Pary', s.instruments.join(', ')], ['Wybór strategii', 'stały (tylko odczyt)']]) kv.append(el('span', null, k), el('b', null, String(v)));
  }
  function renderSettings() {
    const L = current() ? dash?.limits : null, s = dash?.strategy, acct = account(), kv = $('risk-limits'); kv.replaceChildren();
    const rows = L ? [['Ryzyko na transakcję', fmt(L.tradePct, 2) + '% kapitału'], ['Limit straty dziennej', fmt(L.dailyPct, 2) + '%'], ['Maks. obsunięcie (drawdown)', fmt(L.drawdownPct, 2) + '%'], ['Dźwignia maks.', fmt(L.maxLeverage, 2) + '× (cash SPOT)'], ['Maks. pozycji łącznie', String(L.centralMax)], ['Maks. pozycji — flota Alpha / Bravo', L.alphaMax + ' / ' + L.bravoMax], ['Limit kwoty jednego wejścia (DEMO)', eur(L.ordinaryEntryCapQuote)], ['SL / TP', s ? s.stopAtr + '×ATR(' + s.atrDays + ') / ' + s.takeProfitR + 'R' : '—'], ['Opłata wejścia / wyjścia', fmt(L.entryFeeRate * 100, 4) + '% / ' + fmt(L.exitFeeRate * 100, 4) + '%']]
                   : [['Limity', '— (odczyt po połączeniu Observe)']];
    for (const [k, v] of rows) kv.append(el('span', null, k), el('b', null, v));
    const ci = $('connection-info'); ci.replaceChildren();
    const key = acct ? ({CIPHERTEXT_PRESENT_NOT_VERIFIED: 'zapisany (Windows DPAPI)', METADATA_ONLY_NO_KEY: 'brak zapisanego klucza'})[acct.keyState] || acct.keyState : '—';
    for (const [k, v] of [['Giełda', 'OKX (EEA)'], ['Środowisko', 'DEMO (handel symulowany)'], ['Rynek', 'cash SPOT · bez pożyczek i marginu'], ['Waluta kapitału', 'EUR'], ['Tryb', modeText()], ['Zakres', acct?.scopeId || '—'], ['Połączenie', !healthy ? '—' : acct?.connected ? 'połączony (Observe)' : 'rozłączony'], ['Klucz API', key]]) ci.append(el('span', null, k), el('b', null, v));
  }
  function modeText() { const m = status?.executionMode; return !healthy ? 'Tryb: —' : m === 'LOCAL' ? 'Tryb: Lokalnie (ten PC)' : m === 'VPS' ? 'Tryb: VPS' : 'Tryb: nie wybrano'; }

  function stepState() {
    const acct = account(), attached = !!acct?.localOrdinary, connected = !!acct?.connected, armed = !!acct?.armed, running = acct?.mode === 'RUNNING', stop = acct?.mode === 'STOP';
    const prepared = attached && connected && (acct?.instruments || []).length === 3, vps = status?.executionMode === 'VPS', busy = pending.size > 0;
    const can = {'create-local': !!status?.localCreateAvailable, 'attach-local': !!acct && !attached && !connected, connect: attached && !connected, prepare: attached && connected && stop && !armed && !prepared, arm: prepared && stop && !armed, start: armed && stop};
    const done = {'create-local': !!acct, 'attach-local': attached, connect: connected, prepare: prepared, arm: armed || running, start: running};
    return {acct, can, done, vps, busy, running, armed};
  }
  function renderControls() {
    const st = stepState(), list = $('stepper'); list.replaceChildren(); let currentMarked = false;
    for (const [action, title, note] of STEPS) {
      const li = el('li', st.done[action] ? 'done' : ''); const text = el('div'); text.append(el('div', 'step-title', title), el('div', 'step-note', note));
      const b = el('button', 'btn ' + (action === 'start' ? 'btn-primary' : 'btn-ghost'), action === 'start' ? '▶ START' : st.done[action] ? 'Gotowe' : 'Wykonaj');
      b.type = 'button'; b.dataset.action = action;
      const why = !healthy ? 'Brak potwierdzonego statusu' : st.vps ? 'Ten PC jest w trybie VPS — użyj sekcji „Wersja VPS” w Ustawieniach' : st.busy ? 'Trwa lokalne polecenie' : !st.can[action] ? (st.done[action] ? 'Krok wykonany' : 'Najpierw wykonaj poprzedni krok') : '';
      b.disabled = !!why; b.title = why; b.addEventListener('click', () => command(action));
      if (!currentMarked && !st.done[action] && st.can[action] && healthy) { li.classList.add('current'); currentMarked = true; }
      li.append(text, b); list.append(li);
    }
    const acct = st.acct, mode = acct?.mode || null, keep = !!status?.keepAwake;
    const label = !healthy ? '—' : mode === 'RUNNING' ? 'RUNNING' : st.armed ? 'ARMED' : 'STOP';
    const klass = !healthy ? '' : label === 'RUNNING' ? 'green' : label === 'ARMED' ? 'amber' : '';
    for (const id of ['state-pill', 'bot-pill']) { const p = $(id); p.className = 'pill ' + klass; p.replaceChildren(el('span', 'dot'), document.createTextNode(' ' + label + (keep && id === 'state-pill' ? ' · uśpienie zablokowane' : ''))); }
    $('mode-pill').textContent = modeText(); $('side-mode').textContent = modeText(); $('mode-badge').textContent = modeText().replace('Tryb: ', '');
    $('keepawake').textContent = !healthy ? '—' : keep ? 'zablokowane (bot działa)' : 'normalne';
    $('bot-title').textContent = !healthy ? 'Stan nieznany' : label === 'RUNNING' ? 'Bot pracuje (OKX DEMO)' : label === 'ARMED' ? 'Uzbrojony — czeka na START' : acct ? 'Bot zatrzymany (STOP)' : status?.localCreateAvailable ? 'Brak lokalnego bota — pierwsze uruchomienie' : 'Bot zatrzymany (STOP)';
    $('bot-desc').textContent = !healthy ? 'Brak potwierdzonego statusu — sterowanie zwiększające ryzyko niedostępne.' : (acct?.code ? 'Kod: ' + acct.code : '') + (status?.lastResult ? (acct?.code ? ' · ' : '') + 'ostatnie polecenie: ' + status.lastResult : '');
    $('bot-icon').classList.toggle('on', label === 'RUNNING');
    hide($('running-notice'), label !== 'RUNNING');
    $('stop-button').disabled = pending.has('stop');
  }
  function renderNotice() {
    const n = $('data-notice'), snap = current(); let text = '', klass = 'notice';
    if (!healthy) { text = 'Brak potwierdzonego statusu — wszystkie wartości pokazane jako „—”. Sprawdź, czy okno APEX działa.'; klass += ' err'; }
    else if (dash?.snapshotState === 'CURRENT' && dash.snapshot?.connected && staleObservation()) { text = 'Dane nieaktualne: brak poprawnego, świeżego czasu odczytu konta — wartości ukryte („—”).'; klass += ' warn'; }
    else if (!snap) { text = dash?.snapshotState === 'CHANGED_RETRY' ? 'Stan bota zmienia się — odczyt zostanie ponowiony.' : 'Bot nie jest połączony z OKX (Observe) — saldo, pozycje i historia niedostępne („—”). Wykonaj kroki w „Sterowanie botem”.'; klass += ' warn'; }
    n.className = klass; n.textContent = text; hide(n, !text);
  }
  function render() { collectEvents(); renderNotice(); renderCards(); drawChart(); positionRows(false); positionRows(true); historyRows(); renderMarkets(); renderStrategy(); renderSettings(); renderControls(); renderFeed(); }

  async function getJson(url) { const abort = new AbortController(), t = setTimeout(() => abort.abort(), 5000); try { const r = await fetch(url, {cache: 'no-store', signal: abort.signal}); if (!r.ok) throw new Error('HTTP ' + r.status); return await r.json(); } finally { clearTimeout(t); } }
  async function refresh() {
    if (polling) return; polling = true;
    try { const [s, d] = await Promise.all([getJson('/api/naval/okx/status'), getJson('/api/naval/okx/local-dashboard')]); if (!s || !Array.isArray(s.accounts) || !d || d.schema !== 'apex-local-dashboard-v1') throw new Error('Niepoprawna odpowiedź'); status = s; dash = d; healthy = true; }
    catch { healthy = false; }
    finally { polling = false; render(); }
  }
  async function command(action) {
    const acct = account();
    if (action === 'stop' && !confirm(STOP_CONFIRM)) return;
    const url = action === 'create-local' ? '/api/naval/okx/create-local' : action === 'stop' && !acct ? '/api/naval/okx/cancel' : '/api/naval/okx/' + encodeURIComponent(acct?.scopeId || '') + '/' + action;
    if (action !== 'create-local' && action !== 'stop' && !acct) { toast('Brak lokalnego bota — polecenie nie zostało wysłane.'); return; }
    pending.add(action); render(); toast('Wysłano: ' + action + '. Okna Windows (klucz, potwierdzenie, hasło) pojawią się osobno — sprawdź pasek zadań.');
    const abort = new AbortController(), t = setTimeout(() => abort.abort(), 390000);
    try {
      const r = await fetch(url, {method: 'POST', headers: {'Content-Type': 'application/json', 'X-Apex-Local': '1'}, body: JSON.stringify({commandId: crypto.randomUUID()}), signal: abort.signal});
      let body = null; try { body = await r.json(); } catch { body = null; }
      toast((body?.code || body?.reasonCode || 'HTTP ' + r.status) + ' — brak automatycznego ponowienia.');
    } catch (e) { toast('Niepewny wynik polecenia ' + action + ' — sprawdź stan; nie ponawiaj w ciemno.'); }
    finally { clearTimeout(t); pending.delete(action); void refresh(); }
  }

  $('stop-button').addEventListener('click', () => command('stop'));
  for (const b of document.querySelectorAll('[data-goto]')) b.addEventListener('click', () => navigate(b.dataset.goto));
  $('vps-details').addEventListener('toggle', () => { if (!$('vps-details').open || vpsLoaded) return; vpsLoaded = true; const s = document.createElement('script'); s.src = '/remote-controller.js'; s.defer = true; document.body.append(s); });
  window.addEventListener('beforeunload', e => { if (account()?.mode === 'RUNNING') { e.preventDefault(); e.returnValue = 'Bot APEX DZIAŁA. Zamknięcie panelu nie zatrzymuje bota; zamknięcie okna APEX przerwie jego pracę. Najpierw STOP.'; return e.returnValue; } });
  window.addEventListener('hashchange', () => navigate(location.hash.slice(1)));
  navigate(location.hash.slice(1) || 'dashboard'); render(); void refresh(); setInterval(refresh, 2000);
})();
