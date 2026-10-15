'use strict';
/* APEX Trading AI – panel (bez zewnętrznych bibliotek). Wszystkie dane z serwera wstawiane przez textContent. */

const TOKEN = document.querySelector('meta[name="apex-token"]').content;
const S = {
  data: null,
  host: null,
  account: null,
  view: 'dashboard',
  lastOk: 0,
  dirty: {},
  connTab: 'ctrader',
};

const VIEW_TITLES = {
  dashboard: 'Pulpit',
  positions: 'Pozycje i wyniki',
  strategies: 'Strategie',
  instruments: 'Instrumenty',
  risk: 'Ryzyko i bezpieczeństwo',
  connections: 'Połączenia platform',
  diagnostics: 'Diagnostyka i dziennik',
};

/* ------------------------------------------------------------------ pomocnicze */

function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  if (attrs) {
    for (const [k, v] of Object.entries(attrs)) {
      if (v === undefined || v === null || v === false) continue;
      if (k === 'class') node.className = v;
      else if (k === 'text') node.textContent = v;
      else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2), v);
      else if (k === 'style') node.style.cssText = v; // CSSOM – zgodne z CSP (bez inline style)
      else node.setAttribute(k, v === true ? '' : v);
    }
  }
  for (const c of children.flat()) {
    if (c === null || c === undefined || c === false) continue;
    node.appendChild(typeof c === 'string' || typeof c === 'number' ? document.createTextNode(String(c)) : c);
  }
  return node;
}

const SVGNS = 'http://www.w3.org/2000/svg';
function svg(tag, attrs) {
  const n = document.createElementNS(SVGNS, tag);
  for (const [k, v] of Object.entries(attrs || {})) n.setAttribute(k, v);
  return n;
}

function icon(name) {
  const paths = {
    play: 'M7 5v14l11-7z',
    stop: 'M6 6h12v12H6z',
    pause: 'M7 5h4v14H7zM13 5h4v14h-4z',
    x: 'M6 6l12 12M18 6 6 18',
    alert: 'M12 3 2 21h20L12 3Zm0 6v5m0 3v.5',
    check: 'm5 12 4 4 10-10',
    info: 'M12 8v.5M12 11v6M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Z',
    copy: 'M8 8h11v11H8zM5 16V5h11',
    refresh: 'M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7',
    shield: 'M12 3 4 6v6c0 4.5 3.4 8.3 8 9 4.6-.7 8-4.5 8-9V6l-8-3Z',
    test: 'M9 3h6M10 3v6L5 19a1 1 0 0 0 .9 1.5h12.2A1 1 0 0 0 19 19l-5-10V3',
  };
  const s = svg('svg', { viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', 'stroke-width': '2', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' });
  const p = svg('path', { d: paths[name] || paths.info });
  if (name === 'play' || name === 'stop' || name === 'pause') { p.setAttribute('fill', 'currentColor'); p.setAttribute('stroke', 'none'); }
  s.appendChild(p);
  return s;
}

const nf = (d) => new Intl.NumberFormat('pl-PL', { minimumFractionDigits: d, maximumFractionDigits: d });
function money(v, ccy) { if (v === null || v === undefined || isNaN(v)) return '—'; return nf(2).format(v) + (ccy ? ' ' + ccy : ''); }
function signed(v, d = 2) { if (v === null || v === undefined || isNaN(v)) return '—'; return (v > 0 ? '+' : '') + nf(d).format(v); }
function pct(v, d = 2) { if (v === null || v === undefined || isNaN(v)) return '—'; return nf(d).format(v) + '%'; }
function cls(v) { return v > 0 ? 'pos' : v < 0 ? 'neg' : ''; }
function time(t) { if (!t) return '—'; const d = new Date(t); return isNaN(d) ? '—' : d.toLocaleString('pl-PL', { dateStyle: 'short', timeStyle: 'medium' }); }
function shortTime(t) { if (!t) return '—'; const d = new Date(t); return isNaN(d) ? '—' : d.toLocaleTimeString('pl-PL'); }

function lsGet(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }
function lsSet(k, v) { try { localStorage.setItem(k, v); } catch (e) { /* tryb prywatny */ } }

function toast(msg) {
  const t = el('div', { class: 'toast', role: 'status', text: msg });
  document.getElementById('toasts').appendChild(t);
  setTimeout(() => t.remove(), 6500);
}

function confirmModal(title, body, confirmText, danger, extra) {
  return new Promise((resolve) => {
    const back = el('div', { class: 'modal-back', role: 'dialog', 'aria-modal': 'true', 'aria-label': title });
    const ok = el('button', { class: 'btn ' + (danger ? 'danger solid' : 'primary'), text: confirmText });
    const cancel = el('button', { class: 'btn ghost', text: 'Anuluj' });
    const close = (v) => { back.remove(); document.removeEventListener('keydown', esc); resolve(v); };
    const esc = (e) => { if (e.key === 'Escape') close(null); };
    ok.addEventListener('click', () => close(extra ? extra.value : true));
    cancel.addEventListener('click', () => close(null));
    back.addEventListener('click', (e) => { if (e.target === back) close(null); });
    document.addEventListener('keydown', esc);
    back.appendChild(el('div', { class: 'modal' }, el('h3', { text: title }), el('p', { text: body }), extra || null, el('div', { class: 'actions' }, cancel, ok)));
    document.body.appendChild(back);
    (extra || ok).focus();
  });
}

async function api(path, body) {
  const opts = { headers: { 'X-Apex-Token': TOKEN } };
  if (body !== undefined) { opts.method = 'POST'; opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
  const r = await fetch(path, opts);
  if (r.status === 401) throw new Error('Brak autoryzacji – odśwież stronę.');
  return r.json();
}

async function command(action, args, confirm) {
  const acc = currentAccount();
  if (!acc) { toast('Brak wybranego konta.'); return; }
  if (confirm) {
    const ok = await confirmModal(confirm.title, confirm.body, confirm.button, confirm.danger);
    if (!ok) return;
  }
  try {
    const res = await api('/api/accounts/' + encodeURIComponent(acc.key) + '/command', { action, args: args || {} });
    toast(res.message || 'Wykonano.');
    poll();
  } catch (e) { toast('Błąd: ' + e.message); }
}

function currentAccount() {
  const list = (S.data && S.data.data && S.data.data.accounts) || [];
  return list.find((a) => a.key === S.account) || list[0] || null;
}

/* ------------------------------------------------------------------ nawigacja i motyw */

document.querySelectorAll('#nav button').forEach((b) => b.addEventListener('click', () => setView(b.dataset.view)));
function setView(v) {
  S.view = v;
  document.querySelectorAll('#nav button').forEach((b) => b.toggleAttribute('aria-current', b.dataset.view === v));
  document.querySelectorAll('#nav button').forEach((b) => { if (b.dataset.view === v) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current'); });
  document.querySelectorAll('.view').forEach((s) => s.classList.toggle('active', s.id === 'view-' + v));
  document.getElementById('viewTitle').textContent = VIEW_TITLES[v];
  S.dirty = {};
  render(true);
}

function applyTheme(t) {
  document.documentElement.setAttribute('data-theme', t);
  document.querySelectorAll('[data-theme-set]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.themeSet === t)));
  lsSet('apex-theme', t);
}
document.querySelectorAll('[data-theme-set]').forEach((b) => b.addEventListener('click', () => { applyTheme(b.dataset.themeSet); render(true); }));
applyTheme(lsGet('apex-theme') || (window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'));

/* ------------------------------------------------------------------ odpytywanie */

async function poll() {
  try {
    S.data = await api('/api/status');
    S.host = S.data.host;
    S.lastOk = Date.now();
    const accs = S.data.data.accounts || [];
    if (!S.account || !accs.some((a) => a.key === S.account)) S.account = lsGet('apex-account') && accs.some((a) => a.key === lsGet('apex-account')) ? lsGet('apex-account') : (accs[0] && accs[0].key) || null;
    render(false);
  } catch (e) {
    const hs = document.getElementById('hostState');
    hs.replaceChildren(el('span', { class: 'badge critical' }, el('span', { class: 'dot' }), 'brak połączenia z APEX'));
  }
}
setInterval(poll, 1500);
poll();

/* ------------------------------------------------------------------ renderowanie */

function render(force) {
  if (!S.data) return;
  renderChrome();
  const view = document.getElementById('view-' + S.view);
  // Nie przebudowujemy widoku, gdy użytkownik edytuje formularz.
  const active = document.activeElement;
  const editing = active && view.contains(active) && (active.tagName === 'INPUT' || active.tagName === 'SELECT' || active.tagName === 'TEXTAREA');
  if (!force && (editing || S.dirty[S.view])) return;
  const builders = { dashboard: viewDashboard, positions: viewPositions, strategies: viewStrategies, instruments: viewInstruments, risk: viewRisk, connections: viewConnections, diagnostics: viewDiagnostics };
  const scroll = window.scrollY;
  view.replaceChildren(builders[S.view]());
  window.scrollTo(0, scroll);
}

function modeBadge(st, acc) {
  if (!acc || !acc.engine) return el('span', { class: 'badge neutral' }, el('span', { class: 'dot' }), acc ? acc.modeText : 'brak konta');
  const m = acc.engine.mode;
  const kind = m === 'Running' ? 'good' : m === 'PausedNewEntries' ? 'warning' : m === 'EmergencyStopped' ? 'critical' : 'neutral';
  return el('span', { class: 'badge ' + kind, title: acc.engine.modeReason || '' }, el('span', { class: 'dot' }), acc.modeText);
}

function envBadge(acc) {
  const live = acc && acc.engine && acc.engine.isLive;
  return live ? el('span', { class: 'badge live', title: 'Konto rzeczywiste' }, el('span', { class: 'dot' }), 'LIVE')
              : el('span', { class: 'badge demo', title: 'Konto demonstracyjne' }, el('span', { class: 'dot' }), 'DEMO');
}

function renderChrome() {
  const accs = S.data.data.accounts || [];
  const tabs = document.getElementById('accountTabs');
  tabs.replaceChildren(...accs.map((a) => {
    const e = a.engine || {};
    const b = el('button', { class: 'account-tab', 'aria-pressed': String(a.key === S.account), title: a.key },
      el('span', { class: 'badge ' + (a.connected ? 'good' : 'critical'), style: 'padding:2px 6px' }, el('span', { class: 'dot' })),
      `${a.platform} · ${e.accountNumber || a.key.split(':').pop()}`,
      e.isLive ? el('span', { class: 'badge live', style: 'padding:1px 7px' }, 'LIVE') : el('span', { class: 'badge demo', style: 'padding:1px 7px' }, 'DEMO'));
    b.addEventListener('click', () => { S.account = a.key; lsSet('apex-account', a.key); S.dirty = {}; render(true); });
    return b;
  }));
  const age = (Date.now() - S.lastOk) / 1000;
  document.getElementById('updated').textContent = 'Aktualizacja ' + new Date(S.lastOk).toLocaleTimeString('pl-PL') + (age > 5 ? ' (opóźnienie!)' : '');
  const hs = document.getElementById('hostState');
  hs.replaceChildren(el('span', { class: 'badge good' }, el('span', { class: 'dot' }), 'APEX ' + S.host.version + ' działa'));
}

function card(title, sub, ...children) {
  return el('div', { class: 'card' }, el('h2', null, title, sub ? el('span', { class: 'sub', text: sub }) : null), ...children);
}

function waitingView() {
  return el('div', { class: 'card waiting' },
    el('div', { class: 'spinner', 'aria-hidden': 'true' }),
    el('h2', { text: 'Czekam na połączenie platformy' }),
    el('p', { class: 'muted', text: 'Podłącz cTrader lub MetaTrader 5 za pomocą mostu APEX. Instrukcja krok po kroku jest w zakładce „Połączenia platform”.' }),
    el('div', { style: 'margin-top:14px' }, el('button', { class: 'btn primary', onclick: () => setView('connections') }, 'Połącz platformę')));
}

/* ------------------------------------------------------------------ Pulpit */

function severity(frac) { return frac >= 0.85 ? 'critical' : frac >= 0.6 ? 'warning' : ''; }

function meter(label, value, max, fmt) {
  const frac = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
  return el('div', null,
    el('div', { class: 'meter-head' }, el('span', { text: label }), el('span', { class: 'v', text: fmt(value) + ' / ' + fmt(max) })),
    el('div', { class: 'meter ' + severity(frac), role: 'meter', 'aria-valuemin': '0', 'aria-valuemax': String(max), 'aria-valuenow': String(value), 'aria-label': label },
      el('span', { style: 'width:' + (frac * 100).toFixed(1) + '%' })));
}

function tile(label, value, delta, valueClass) {
  return el('div', { class: 'card tile' }, el('div', { class: 'label', text: label }), el('div', { class: 'value ' + (valueClass || ''), text: value }), delta ? el('div', { class: 'delta', text: delta }) : null);
}

function viewDashboard() {
  const acc = currentAccount();
  if (!acc) return waitingView();
  const e = acc.engine;
  const wrap = el('div', { class: 'stack' });
  if (!e) {
    wrap.appendChild(card('Przygotowanie konta', null, el('p', { class: 'muted', text: acc.buildError ? 'Błąd: ' + acc.buildError : 'Most połączony – trwa pobieranie listy instrumentów i historii notowań…' })));
    return wrap;
  }
  const ccy = e.currency;
  if (!e.guardAllowed) {
    wrap.appendChild(el('div', { class: 'callout critical' }, icon('shield'), el('div', null, el('strong', { text: 'Handel zablokowany przez strażnika konta. ' }), e.guardMessage)));
  }
  if (!acc.connected) wrap.appendChild(el('div', { class: 'callout warning' }, icon('alert'), el('div', null, el('strong', { text: 'Most rozłączony. ' }), 'Wszystkie operacje są wstrzymane do ponownego połączenia (pozycje są chronione przez SL u brokera).')));
  if (e.dailyLimitHit) wrap.appendChild(el('div', { class: 'callout warning' }, icon('alert'), el('div', null, el('strong', { text: 'Osiągnięto dzienny limit straty. ' }), 'Nowe transakcje są zablokowane do następnego dnia handlowego.')));
  if (e.maxDrawdownHit) wrap.appendChild(el('div', { class: 'callout critical' }, icon('alert'), el('div', null, el('strong', { text: 'Przekroczono maksymalne obsunięcie. ' }), 'Handel zatrzymany – wymagany ręczny reset po analizie sytuacji.')));

  // Hero
  const heroMain = el('div', { class: 'card hero-main' },
    el('div', { class: 'hero-row' }, envBadge(acc), modeBadge(e, acc), el('span', { class: 'muted', text: `${acc.platform} · ${e.broker} · konto ${e.accountNumber}` })),
    el('div', null, el('div', { class: 'hero-label', text: 'Kapitał (equity)' }), el('div', { class: 'hero-value' }, nf(2).format(e.equity), el('span', { class: 'ccy', text: ccy }))),
    el('div', { class: 'hero-row' },
      el('span', { class: 'badge ' + (e.dailyPnl >= 0 ? 'good' : 'critical') }, el('span', { class: 'dot' }), 'Dziś ' + signed(e.dailyPnl) + ' ' + ccy + ' (' + signed(e.dailyPnlPct) + '%)'),
      el('span', { class: 'muted', text: 'Saldo ' + money(e.balance, ccy) })),
    el('div', { class: 'controls' },
      el('button', { class: 'btn primary', onclick: () => command('start'), disabled: e.mode === 'Running' || !e.guardAllowed }, icon('play'), 'START'),
      el('button', { class: 'btn', onclick: () => command('pause'), disabled: e.mode !== 'Running' }, icon('pause'), 'Wstrzymaj nowe'),
      el('button', { class: 'btn', onclick: () => command('stop', null, { title: 'Zatrzymać bota?', body: 'Bot przestanie otwierać i zarządzać pozycjami. Otwarte pozycje pozostaną chronione przez SL/TP u brokera.', button: 'Zatrzymaj' }) }, icon('stop'), 'STOP'),
      el('button', { class: 'btn danger', onclick: () => command('closeAll', null, { title: 'Zamknąć pozycje bota?', body: 'Zostaną zamknięte WSZYSTKIE pozycje otwarte przez APEX na tym koncie. Pozycje ręczne i innych botów nie są dotykane.', button: 'Zamknij pozycje bota', danger: true }) }, icon('x'), 'Zamknij pozycje bota'),
      e.mode === 'EmergencyStopped'
        ? el('button', { class: 'btn', onclick: () => command('resetEmergency', null, { title: 'Zresetować stan awaryjny?', body: 'Bot przejdzie w stan ZATRZYMANY. Wznowienie handlu wymaga osobnego naciśnięcia START.', button: 'Resetuj' }) }, icon('refresh'), 'Resetuj awarię')
        : el('button', { class: 'btn danger solid', onclick: () => command('emergency', null, { title: 'AWARYJNE ZATRZYMANIE', body: 'Natychmiast zamknie wszystkie pozycje bota i zablokuje handel do ręcznego resetu.', button: 'ZATRZYMAJ AWARYJNIE', danger: true }) }, icon('alert'), 'AWARYJNY STOP')));

  const meters = card('Wykorzystanie limitów ryzyka', 'aktualizacja na żywo', el('div', { class: 'meters' },
    meter('Dzienna strata', Math.max(0, -e.dailyPnlPct), e.maxDailyLossPct, (v) => pct(v)),
    meter('Obsunięcie od szczytu', e.drawdownPct, e.maxDrawdownPct, (v) => pct(v)),
    meter('Otwarte pozycje bota', e.ownPositionCount, e.maxPositions, (v) => String(v)),
    meter('Dźwignia efektywna', e.effectiveLeverage, e.maxEffectiveLeverage || 5, (v) => nf(2).format(v) + 'x')),
    el('p', { class: 'note', text: `Ryzyko na transakcję: ${pct(e.riskPerTradePct)} kapitału. Bot otwiera pozycję tylko gdy najgorszy scenariusz nie przekracza limitów.` }));
  wrap.appendChild(el('div', { class: 'hero' }, heroMain, meters));

  wrap.appendChild(el('div', { class: 'grid kpis' },
    tile('Wynik dziś', signed(e.dailyPnl) + ' ' + ccy, signed(e.dailyPnlPct) + '% od początku dnia', cls(e.dailyPnl)),
    tile('Wynik łączny', signed(e.totalPnl) + ' ' + ccy, signed(e.totalPnlPct) + '% od startu bota', cls(e.totalPnl)),
    tile('Obsunięcie', pct(e.drawdownPct), 'limit ' + pct(e.maxDrawdownPct)),
    tile('Ekspozycja', nf(2).format(e.effectiveLeverage) + 'x', 'limit ' + nf(2).format(e.maxEffectiveLeverage || 5) + 'x'),
    tile('Pozycje bota', `${e.ownPositionCount} / ${e.maxPositions}`, 'zamkniętych: ' + e.tradesTotal),
    tile('Skanowane instrumenty', String(Object.values(e.universeByClass || {}).reduce((a, b) => a + b, 0)), Object.entries(e.universeByClass || {}).map(([k, v]) => `${k} ${v}`).join(' · ') || 'brak zweryfikowanych strategii')));

  wrap.appendChild(el('div', { class: 'grid cols-2' },
    card('Kapitał w czasie', 'próbki co 5 minut', equityChart(acc.equity || [], ccy)),
    card('Otwarte pozycje bota', `${e.openPositions.length}`, positionsList(e.openPositions))));

  wrap.appendChild(el('div', { class: 'grid cols-3' },
    card('Ostatnie sygnały', 'ranking i decyzje', feed(e.recentSignals, false)),
    card('Komunikaty i błędy', null, feed(e.recentErrors, true)),
    card('Ważne wiadomości', e.newsStatus ? '' : null, newsList())));
  return wrap;
}

function feed(items, err) {
  if (!items || items.length === 0) return el('div', { class: 'empty', text: err ? 'Brak błędów.' : 'Brak sygnałów – bot czeka na warunki spełniające kryteria.' });
  return el('div', { class: 'feed' }, items.map((s) => el('div', { class: 'item' + (err ? ' err' : ''), text: s })));
}

function newsList() {
  const n = S.host.news;
  const box = el('div', { class: 'stack', style: 'gap:8px' });
  box.appendChild(el('div', { class: 'muted', style: 'font-size:12px', text: n.enabled ? (n.updated ? `Kalendarz: ${n.count} wydarzeń, aktualizacja ${time(n.updated)}` : 'Kalendarz jeszcze nie pobrany') + (n.error ? ` – błąd: ${n.error}` : '') : 'Filtr wiadomości wyłączony' }));
  if (!n.upcoming || n.upcoming.length === 0) box.appendChild(el('div', { class: 'empty', text: 'Brak nadchodzących ważnych publikacji.' }));
  else box.appendChild(el('div', { class: 'feed' }, n.upcoming.map((x) => el('div', { class: 'item' }, el('span', { class: 't', text: time(x.timeUtc) }), el('strong', { text: x.currency + ' ' }), x.title, ' ', el('span', { class: 'muted', text: '(' + x.impact + ')' })))));
  box.appendChild(el('p', { class: 'note', text: 'Bot nie otwiera nowych pozycji na powiązanych instrumentach od 30 min przed do 30 min po publikacji o wysokim wpływie.' }));
  return box;
}

function positionsList(list) {
  if (!list || list.length === 0) return el('div', { class: 'empty', text: 'Brak otwartych pozycji bota.' });
  return el('div', { class: 'feed' }, list.map((p) => el('div', { class: 'item mono', text: p })));
}

/* ------------------------------------------------------------------ wykres kapitału (jedna seria) */

function niceTicks(min, max, n) {
  if (min === max) { min -= 1; max += 1; }
  const span = max - min;
  const step0 = span / Math.max(1, n);
  const mag = Math.pow(10, Math.floor(Math.log10(step0)));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= step0) || step0;
  const ticks = [];
  for (let v = Math.floor(min / step) * step; v <= max + step * 0.5; v += step) ticks.push(v);
  return ticks;
}

function equityChart(points, ccy) {
  const box = el('div', { class: 'chart' });
  if (!points || points.length < 2) {
    box.appendChild(el('div', { class: 'empty', text: 'Za mało danych – wykres pojawi się po kilku próbkach kapitału.' }));
    return box;
  }
  const W = 640, H = 230, L = 64, R = 74, T = 12, B = 26;
  const xs = points.map((p) => p.t), ys = points.map((p) => p.v);
  let lo = Math.min(...ys), hi = Math.max(...ys);
  const minSpan = Math.max(1, Math.abs((lo + hi) / 2) * 0.002); // płaska seria nie jest "przybliżana" do szumu
  if (hi - lo < minSpan) { const mid = (lo + hi) / 2; lo = mid - minSpan / 2; hi = mid + minSpan / 2; }
  const ticks = niceTicks(lo, hi, 4);
  const step = ticks.length > 1 ? ticks[1] - ticks[0] : 1;
  const tickDigits = step >= 1 ? 0 : step >= 0.1 ? 1 : 2;
  const y0 = ticks[0], y1 = ticks[ticks.length - 1];
  const x0 = xs[0], x1 = xs[xs.length - 1];
  const X = (t) => L + (x1 === x0 ? 0 : (t - x0) / (x1 - x0)) * (W - L - R);
  const Y = (v) => T + (1 - (v - y0) / (y1 - y0 || 1)) * (H - T - B);
  const s = svg('svg', { viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: 'none', role: 'img', 'aria-label': 'Wykres kapitału w czasie' });
  const style = getComputedStyle(document.documentElement);
  const c = (n) => style.getPropertyValue(n).trim();
  for (const t of ticks) {
    s.appendChild(svg('line', { x1: L, x2: W - R, y1: Y(t), y2: Y(t), stroke: c('--grid'), 'stroke-width': '1', 'vector-effect': 'non-scaling-stroke' }));
    const lbl = svg('text', { x: L - 8, y: Y(t) + 4, 'text-anchor': 'end', fill: c('--muted'), 'font-size': '11' });
    lbl.textContent = nf(tickDigits).format(t);
    s.appendChild(lbl);
  }
  for (const [t, anchor] of [[x0, 'start'], [x1, 'end']]) {
    const lbl = svg('text', { x: X(t), y: H - 6, 'text-anchor': anchor, fill: c('--muted'), 'font-size': '11' });
    lbl.textContent = new Date(t).toLocaleString('pl-PL', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
    s.appendChild(lbl);
  }
  const line = points.map((p, i) => (i ? 'L' : 'M') + X(p.t).toFixed(1) + ' ' + Y(p.v).toFixed(1)).join(' ');
  const area = line + ` L ${X(x1).toFixed(1)} ${Y(y0).toFixed(1)} L ${X(x0).toFixed(1)} ${Y(y0).toFixed(1)} Z`;
  s.appendChild(svg('path', { d: area, fill: c('--accent'), opacity: '0.10' }));
  s.appendChild(svg('path', { d: line, fill: 'none', stroke: c('--accent'), 'stroke-width': '2', 'stroke-linejoin': 'round', 'stroke-linecap': 'round', 'vector-effect': 'non-scaling-stroke' }));
  const last = points[points.length - 1];
  s.appendChild(svg('circle', { cx: X(last.t), cy: Y(last.v), r: '4', fill: c('--accent'), stroke: c('--surface-1'), 'stroke-width': '2' }));
  const endLbl = svg('text', { x: X(last.t) + 8, y: Y(last.v) + 4, fill: c('--text-1'), 'font-size': '12', 'font-weight': '600' });
  endLbl.textContent = nf(2).format(last.v);
  s.appendChild(endLbl);
  const cross = svg('line', { y1: T, y2: H - B, stroke: c('--axis'), 'stroke-width': '1', opacity: '0', 'vector-effect': 'non-scaling-stroke' });
  const dot = svg('circle', { r: '4', fill: c('--accent'), stroke: c('--surface-1'), 'stroke-width': '2', opacity: '0' });
  s.appendChild(cross);
  s.appendChild(dot);
  const tip = el('div', { class: 'tooltip' });
  const hit = svg('rect', { x: L, y: T, width: W - L - R, height: H - T - B, fill: 'transparent', tabindex: '0', 'aria-label': 'Najedź, aby zobaczyć wartości' });
  const show = (clientX) => {
    const r = s.getBoundingClientRect();
    const vx = ((clientX - r.left) / r.width) * W;
    const t = x0 + ((vx - L) / (W - L - R)) * (x1 - x0);
    let best = points[0];
    for (const p of points) if (Math.abs(p.t - t) < Math.abs(best.t - t)) best = p;
    cross.setAttribute('x1', X(best.t)); cross.setAttribute('x2', X(best.t)); cross.setAttribute('opacity', '1');
    dot.setAttribute('cx', X(best.t)); dot.setAttribute('cy', Y(best.v)); dot.setAttribute('opacity', '1');
    tip.replaceChildren(el('strong', { text: money(best.v, ccy) }), el('span', { text: new Date(best.t).toLocaleString('pl-PL') }));
    tip.style.left = ((X(best.t) / W) * r.width) + 'px';
    tip.style.top = ((Y(best.v) / H) * r.height) + 'px';
    tip.style.opacity = '1';
  };
  hit.addEventListener('pointermove', (ev) => show(ev.clientX));
  hit.addEventListener('pointerleave', () => { cross.setAttribute('opacity', '0'); dot.setAttribute('opacity', '0'); tip.style.opacity = '0'; });
  hit.addEventListener('focus', () => { const r = s.getBoundingClientRect(); show(r.right - 1); });
  hit.addEventListener('blur', () => { tip.style.opacity = '0'; });
  s.appendChild(hit);
  box.appendChild(s);
  box.appendChild(tip);
  return box;
}

/* ------------------------------------------------------------------ Pozycje i wyniki */

function viewPositions() {
  const acc = currentAccount();
  if (!acc) return waitingView();
  const e = acc.engine || {};
  const st = acc.stats || {};
  const wrap = el('div', { class: 'stack' });
  wrap.appendChild(el('div', { class: 'grid kpis' },
    tile('Transakcje', String(st.trades || 0)),
    tile('Win rate', st.trades ? pct((st.winRate || 0) * 100, 1) : '—'),
    tile('Profit factor', st.trades ? nf(2).format(st.profitFactor) : '—'),
    tile('Expectancy', st.trades ? signed(st.expectancyR, 3) + ' R' : '—', 'średni wynik w wielokrotnościach ryzyka'),
    tile('Maks. obsunięcie', st.trades ? pct(st.maxDrawdownPct) : '—'),
    tile('Śr. zysk / śr. strata', st.trades ? `${nf(2).format(st.avgWin || 0)} / ${nf(2).format(st.avgLoss || 0)}` : '—')));
  wrap.appendChild(card('Otwarte pozycje bota', null, positionsList(e.openPositions)));
  const rows = (acc.recentTrades || []).map((t) => el('tr', null,
    el('td', { text: time(t.exit) }), el('td', { text: t.symbol }), el('td', { text: t.dir }), el('td', { text: t.strategy }),
    el('td', { class: 'num ' + cls(t.net), text: signed(t.net) }), el('td', { class: 'num ' + cls(t.r), text: signed(t.r) + 'R' }),
    el('td', { text: t.reason, title: t.entryReason })));
  const table = rows.length ? el('div', { class: 'table-wrap' }, el('table', null,
    el('thead', null, el('tr', null, ...['Zamknięcie', 'Instrument', 'Kierunek', 'Strategia'].map((h) => el('th', { text: h })), el('th', { class: 'num', text: 'Wynik netto' }), el('th', { class: 'num', text: 'R' }), el('th', { text: 'Powód wyjścia' }))),
    el('tbody', null, rows))) : el('div', { class: 'empty', text: 'Brak zamkniętych transakcji.' });
  const dl = el('button', { class: 'btn small', onclick: () => downloadJournal(acc.key) }, 'Pobierz dziennik CSV');
  wrap.appendChild(card('Ostatnie zamknięte transakcje', null, table, el('div', { style: 'margin-top:12px' }, dl)));
  return wrap;
}

async function downloadJournal(key) {
  const r = await fetch('/api/accounts/' + encodeURIComponent(key) + '/journal.csv', { headers: { 'X-Apex-Token': TOKEN } });
  const blob = await r.blob();
  const a = el('a', { href: URL.createObjectURL(blob), download: 'apex-dziennik.csv' });
  document.body.appendChild(a); a.click(); a.remove();
}

/* ------------------------------------------------------------------ Strategie */

const STATUS = {
  Validated: ['good', 'ZWERYFIKOWANA'],
  Experimental: ['warning', 'EKSPERYMENTALNA – TYLKO DEMO'],
  Rejected: ['critical', 'ODRZUCONA PRZEZ TESTY'],
};

function viewStrategies() {
  const acc = currentAccount();
  if (!acc || !acc.engine) return waitingView();
  const e = acc.engine;
  const wrap = el('div', { class: 'stack' });
  wrap.appendChild(el('div', { class: 'callout' }, icon('info'), el('div', null,
    el('strong', { text: `Profil strategii ${acc.profileVersion}. ` }),
    'Strategie i parametry pochodzą z badań walk-forward na danych 2005–2017 z testem na odłożonym okresie 2018–2020. Bot nie zmienia parametrów samodzielnie. Ocena okazji (0–100) to ranking jakości warunków, a nie prawdopodobieństwo zysku.')));
  const byStrategy = {};
  for (const p of acc.profile || []) (byStrategy[p.strategy] = byStrategy[p.strategy] || []).push(p);
  const cards = Object.entries(byStrategy).map(([sid, entries]) => {
    const enabled = !(e.disabledStrategies || []).includes(sid);
    const anyUsable = entries.some((x) => x.status !== 'Rejected');
    const sw = el('label', { class: 'switch', title: anyUsable ? 'Włącz/wyłącz nowe wejścia tej strategii' : 'Strategia odrzucona przez testy – nie można jej włączyć' },
      el('input', { type: 'checkbox', checked: enabled && anyUsable, disabled: !anyUsable, onchange: (ev) => command('toggleStrategy', { strategy: sid, enabled: ev.target.checked }) }),
      el('span', { class: 'track' }), el('span', { class: 'muted', text: enabled && anyUsable ? 'aktywna' : 'wyłączona' }));
    return el('div', { class: 'card strategy-card' },
      el('div', { class: 'top' }, el('div', { class: 'name', text: entries[0].name }), el('div', { style: 'margin-left:auto' }, sw)),
      el('div', { class: 'stack', style: 'gap:8px' }, entries.map((x) => el('div', null,
        el('div', { class: 'chips' }, el('span', { class: 'badge neutral' }, x.assetClassName), el('span', { class: 'badge ' + STATUS[x.status][0] }, el('span', { class: 'dot' }), STATUS[x.status][1])),
        el('div', { class: 'evidence', text: x.evidence })))));
  });
  wrap.appendChild(el('div', { class: 'grid cols-2' }, cards));
  if ((e.suspended || []).length) {
    wrap.appendChild(card('Strategie wstrzymane przez monitor wyników', null, feed(e.suspended, true), el('div', { style: 'margin-top:10px' }, el('button', { class: 'btn', onclick: () => command('resumeSuspended') }, 'Przywróć wstrzymane'))));
  }
  return wrap;
}

/* ------------------------------------------------------------------ Instrumenty */

const CLASSES = [['Forex', 'Forex'], ['Metal', 'Metale'], ['Energy', 'Energia'], ['Index', 'Indeksy'], ['Stock', 'Akcje'], ['Etf', 'ETF'], ['Commodity', 'Towary'], ['Bond', 'Obligacje']];

function viewInstruments() {
  const acc = currentAccount();
  if (!acc || !acc.engine) return waitingView();
  const e = acc.engine;
  const wrap = el('div', { class: 'stack' });
  const classSwitches = CLASSES.map(([k, name]) => {
    const on = !(e.disabledClasses || []).includes(k);
    return el('label', { class: 'switch' }, el('input', { type: 'checkbox', checked: on, onchange: (ev) => command('toggleClass', { assetClass: k, enabled: ev.target.checked }) }), el('span', { class: 'track' }), el('span', { text: name }));
  });
  wrap.appendChild(card('Kategorie instrumentów', 'kryptowaluty są trwale wykluczone',
    el('div', { class: 'chips', style: 'gap:18px' }, classSwitches),
    el('p', { class: 'note', text: 'Wyłączenie kategorii zatrzymuje nowe wejścia na jej instrumentach. Kategorie bez zweryfikowanej strategii nie są skanowane.' })));

  const manual = acc.universeMode === 'manual';
  const selected = new Set((acc.universe || []).filter((u) => u.selected).map((u) => u.symbol));
  const modeSel = el('select', { 'aria-label': 'Tryb wyboru', onchange: () => { S.dirty.instruments = true; } }, el('option', { value: 'auto', selected: !manual }, 'Automatyczny (zalecany)'), el('option', { value: 'manual', selected: manual }, 'Ręczny'));
  const rows = (acc.universe || []).map((u) => el('tr', null,
    el('td', null, el('input', { type: 'checkbox', checked: u.selected, 'data-symbol': u.symbol, disabled: u.assetClass === 'Crypto' || u.assetClass === 'Unknown', 'aria-label': 'Wybierz ' + u.symbol, onchange: () => { S.dirty.instruments = true; } })),
    el('td', { text: u.symbol }), el('td', { text: u.className }), el('td', { class: 'muted', text: u.description || '' }), el('td', { class: 'muted', text: u.reason })));
  const save = el('button', { class: 'btn primary', onclick: () => {
    const syms = Array.from(document.querySelectorAll('#view-instruments input[data-symbol]:checked')).map((i) => i.dataset.symbol);
    S.dirty.instruments = false;
    command('setUniverse', { mode: modeSel.value, symbols: syms });
  } }, 'Zapisz wybór');
  wrap.appendChild(card('Instrumenty u brokera', `${selected.size} skanowanych`,
    el('div', { class: 'controls', style: 'margin-bottom:12px' }, el('div', { class: 'field' }, el('label', { text: 'Tryb wyboru' }), modeSel), el('div', { style: 'align-self:end' }, save)),
    el('div', { class: 'table-wrap', style: 'max-height:520px;overflow:auto' }, el('table', null,
      el('thead', null, el('tr', null, ...['', 'Symbol', 'Klasa', 'Opis', 'Status'].map((h) => el('th', { text: h })))),
      el('tbody', null, rows)))));
  return wrap;
}

/* ------------------------------------------------------------------ Ryzyko */

function numField(id, label, value, min, max, step, hint) {
  return el('div', { class: 'field' }, el('label', { for: id, text: label }),
    el('input', { id, type: 'number', value: String(value), min: String(min), max: String(max), step: String(step), oninput: () => { S.dirty.risk = true; } }),
    hint ? el('span', { class: 'hint', text: hint }) : null);
}

function viewRisk() {
  const acc = currentAccount();
  if (!acc || !acc.engine) return waitingView();
  const e = acc.engine;
  const wrap = el('div', { class: 'stack' });
  const form = el('div', { class: 'form' },
    numField('r-risk', 'Ryzyko na transakcję (% kapitału)', e.riskPerTradePct, 0.05, 2, 0.05, 'Domyślnie 0,5%. Maksymalnie 2%.'),
    numField('r-daily', 'Maksymalna dzienna strata (%)', e.maxDailyLossPct, 0.1, 10, 0.1, 'Po przekroczeniu – brak nowych transakcji do końca dnia.'),
    numField('r-dd', 'Maksymalne obsunięcie (%)', e.maxDrawdownPct, 0.5, 30, 0.5, 'Po przekroczeniu – zamknięcie pozycji bota i blokada do ręcznego resetu.'),
    numField('r-pos', 'Maksymalna liczba pozycji', e.maxPositions, 1, 20, 1, 'Domyślnie 3.'),
    numField('r-lev', 'Maksymalna dźwignia efektywna (x)', e.maxEffectiveLeverage || 5, 0.5, 30, 0.5, 'Suma wartości nominalnych / kapitał. Domyślnie 5.'));
  const save = el('button', { class: 'btn primary', onclick: () => {
    const v = (id) => parseFloat(document.getElementById(id).value.replace(',', '.'));
    S.dirty.risk = false;
    command('setLimits', { riskPerTradePct: v('r-risk'), maxDailyLossPct: v('r-daily'), maxDrawdownPct: v('r-dd'), maxOpenPositions: v('r-pos'), maxEffectiveLeverage: v('r-lev') },
      { title: 'Zapisać nowe limity ryzyka?', body: 'Nowe limity obowiązują natychmiast dla tego konta. Wartości spoza bezpiecznych zakresów zostaną odrzucone.', button: 'Zapisz limity' });
  } }, icon('check'), 'Zapisz limity');
  wrap.appendChild(card('Limity ryzyka konta', null, form, el('div', { style: 'margin-top:14px' }, save),
    el('p', { class: 'note', text: 'Bot nigdy nie stosuje martingale, uśredniania strat ani gridów. Każda pozycja ma obowiązkowy Stop Loss, a wolumen jest zaokrąglany wyłącznie w dół.' })));

  const live = e.isLive;
  const liveBox = el('div', { class: 'stack' });
  if (!live) {
    liveBox.appendChild(el('div', { class: 'callout good' }, icon('shield'), el('div', null, el('strong', { text: 'Konto DEMO. ' }), 'Handel odbywa się wyłącznie na środkach demonstracyjnych.')));
  } else if (!acc.liveFeature) {
    liveBox.appendChild(el('div', { class: 'callout critical' }, icon('shield'), el('div', null, el('strong', { text: 'Konto LIVE zablokowane. ' }), 'Handel na kontach rzeczywistych jest wyłączony globalnie. Odblokowanie wymaga świadomej edycji pliku apex.config.json ("LiveTradingFeature": true) przy zamkniętej aplikacji, a następnie potwierdzenia frazą w tym miejscu.')));
  } else {
    const input = el('input', { type: 'text', 'aria-label': 'Fraza potwierdzenia', placeholder: acc.requiredLivePhrase || '', style: 'width:100%;margin-top:8px;padding:9px 11px;border-radius:10px;border:1px solid var(--border-strong);background:var(--surface-2)' });
    liveBox.appendChild(el('div', { class: 'callout critical' }, icon('alert'), el('div', null, el('strong', { text: 'Konto LIVE. ' }), e.guardAllowed ? 'Konto zostało świadomie odblokowane. Handel odbywa się na PRAWDZIWYCH środkach.' : 'Handel zablokowany. Aby odblokować, wpisz dokładnie frazę potwierdzenia.')));
    if (!e.guardAllowed) {
      liveBox.appendChild(el('button', { class: 'btn danger', onclick: async () => {
        const phrase = await confirmModal('Odblokować handel LIVE?', 'Wpisz dokładnie frazę: ' + (acc.requiredLivePhrase || ''), 'Odblokuj LIVE', true, input);
        if (phrase) command('unlockLive', { phrase });
      } }, icon('shield'), 'Odblokuj konto LIVE…'));
    } else {
      liveBox.appendChild(el('button', { class: 'btn', onclick: () => command('lockLive') }, icon('shield'), 'Zablokuj konto LIVE'));
    }
  }
  wrap.appendChild(card('Tryb konta i ochrona LIVE', null, liveBox));
  return wrap;
}

/* ------------------------------------------------------------------ Połączenia */

function copyField(value, label) {
  const code = el('code', { text: value });
  return el('div', { class: 'field' }, el('label', { text: label }), el('div', { class: 'copy-field' }, code,
    el('button', { class: 'btn small', 'aria-label': 'Kopiuj ' + label, onclick: async () => { try { await navigator.clipboard.writeText(value); toast('Skopiowano: ' + label); } catch (e) { toast('Skopiuj ręcznie: ' + value); } } }, icon('copy'), 'Kopiuj')));
}

function viewConnections() {
  const h = S.host;
  const wrap = el('div', { class: 'stack' });
  const tabsDef = [['ctrader', 'cTrader'], ['mt5', 'MetaTrader 5'], ['other', 'Inna platforma']];
  const tabs = el('div', { class: 'tabs', role: 'tablist' }, tabsDef.map(([k, n]) => el('button', { role: 'tab', 'aria-selected': String(S.connTab === k), onclick: () => { S.connTab = k; render(true); } }, n)));
  let steps;
  if (S.connTab === 'ctrader') {
    steps = el('ol', { class: 'steps' },
      el('li', { text: 'W cTrader otwórz zakładkę Algo i zaimportuj plik ApexBridge.algo (folder instalacji APEX → platformy → cTrader).' }),
      el('li', { text: 'Dodaj instancję cBota „ApexBridge” na dowolnym wykresie (np. EURUSD H1) na koncie DEMO.' }),
      el('li', { text: 'W parametrach wklej Klucz parowania (poniżej). Adres serwera pozostaw domyślny.' }),
      el('li', { text: 'Uruchom cBota (Start). Konto pojawi się w APEX w ciągu kilku sekund.' }));
  } else if (S.connTab === 'mt5') {
    steps = el('ol', { class: 'steps' },
      el('li', { text: 'Skopiuj plik ApexBridge.mq5 do folderu MQL5\\Experts terminala (Plik → Otwórz folder danych) i skompiluj go w MetaEditorze (F7).' }),
      el('li', { text: 'W MT5: Narzędzia → Opcje → Doradcy: zaznacz „Zezwalaj na handel algorytmiczny”.' }),
      el('li', { text: 'Przeciągnij „ApexBridge” na wykres, w parametrach wpisz Klucz parowania i port TCP (poniżej).' }),
      el('li', { text: 'W APEX ustaw prowizję konta MT5 (MT5 nie udostępnia jej przez API) – bez tego bot nie otworzy pozycji.' }));
  } else {
    steps = el('ol', { class: 'steps' },
      el('li', { text: 'Każdą platformę można podłączyć prostym mostem w protokole APEX Bridge (JSON przez WebSocket lub TCP).' }),
      el('li', { text: 'Specyfikacja: docs/PROTOKOL_MOSTU.md – komunikaty hello, symbols, bars, quotes, account, positions, closed oraz polecenia open/modify/close.' }),
      el('li', { text: 'Wzorcowe implementacje: most cTrader (C#) i most MetaTrader 5 (MQL5).' }));
  }
  wrap.appendChild(card('Połącz platformę', 'kreator', tabs, el('div', { class: 'grid cols-2' },
    el('div', null, steps),
    el('div', { class: 'stack', style: 'gap:12px' },
      copyField(h.pairingKey, 'Klucz parowania'),
      copyField(h.wsUrl, 'Adres WebSocket (cTrader)'),
      copyField('127.0.0.1:' + h.tcpPort, 'Adres TCP (MetaTrader 5)'),
      el('button', { class: 'btn small', style: 'align-self:flex-start', onclick: async () => {
        const ok = await confirmModal('Wygenerować nowy klucz?', 'Nowe mosty będą wymagały nowego klucza. Aktywne połączenia pozostaną.', 'Generuj', false);
        if (ok) { const r = await api('/api/pairing/regenerate', {}); toast(r.message); poll(); }
      } }, icon('refresh'), 'Nowy klucz parowania')))));

  const accs = S.data.data.accounts || [];
  const rows = accs.map((a) => {
    const s = a.session || {};
    return el('tr', null,
      el('td', null, el('span', { class: 'badge ' + (a.connected ? 'good' : 'critical') }, el('span', { class: 'dot' }), a.connected ? 'połączony' : 'rozłączony')),
      el('td', { text: a.platform }), el('td', { text: a.key }), el('td', { text: (s.transport || '—') + ' ' + (s.bridgeVersion || '') }),
      el('td', { class: 'num', text: String(s.symbols || 0) }), el('td', { class: 'num', text: String(s.quotes || 0) }), el('td', { text: shortTime(s.lastMessageUtc) }));
  });
  wrap.appendChild(card('Połączone konta', `${accs.length}`, rows.length ? el('div', { class: 'table-wrap' }, el('table', null,
    el('thead', null, el('tr', null, ...['Stan', 'Platforma', 'Konto', 'Transport', 'Symbole', 'Notowania', 'Ostatni komunikat'].map((x) => el('th', { text: x })))),
    el('tbody', null, rows))) : el('div', { class: 'empty', text: 'Brak połączonych platform.' })));

  const acc = currentAccount();
  if (acc && /mt5|metatrader/i.test(acc.platform)) {
    const kind = el('select', { 'aria-label': 'Typ prowizji', onchange: () => { S.dirty.connections = true; } }, el('option', { value: 'UsdPerLot' }, 'USD za lot za stronę'), el('option', { value: 'None' }, 'Brak (konto Standard – koszt w spreadzie)'));
    const val = el('input', { type: 'number', min: '0', max: '100', step: '0.1', value: acc.commission ? String(acc.commission.value) : '3.5', 'aria-label': 'Wartość prowizji', oninput: () => { S.dirty.connections = true; } });
    if (acc.commission) kind.value = acc.commission.kind === 'None' ? 'None' : 'UsdPerLot';
    wrap.appendChild(card('Prowizja konta MetaTrader 5', acc.commission ? 'ustawiona' : 'WYMAGANA',
      el('div', { class: 'controls' }, el('div', { class: 'field' }, el('label', { text: 'Typ' }), kind), el('div', { class: 'field' }, el('label', { text: 'Wartość' }), val),
        el('div', { style: 'align-self:end' }, el('button', { class: 'btn primary', onclick: () => { S.dirty.connections = false; command('setCommission', { kind: kind.value, value: parseFloat(val.value) }); } }, 'Zapisz'))),
      el('p', { class: 'note', text: 'Przykład: Pepperstone Razor – 3,5 USD za lot za stronę. Bez znajomości kosztu transakcji bot nie otwiera pozycji.' })));
  }
  const rej = S.data.data.rejections || [];
  if (rej.length) wrap.appendChild(card('Odrzucone połączenia', null, feed(rej, true)));
  return wrap;
}

/* ------------------------------------------------------------------ Diagnostyka */

function viewDiagnostics() {
  const acc = currentAccount();
  const wrap = el('div', { class: 'stack' });
  if (acc && acc.engine) {
    const isLive = acc.engine.isLive;
    const test = el('div', { class: 'stack', style: 'gap:10px' },
      el('p', { class: 'muted', style: 'margin:0', text: 'Test otwiera pozycję o minimalnym wolumenie z SL i TP, po ok. 20 s przesuwa SL, a następnie zamyka pozycję. Każdy krok jest weryfikowany na podstawie faktycznego stanu u brokera.' }),
      el('div', { class: 'controls' }, el('button', { class: 'btn primary', disabled: isLive, onclick: () => command('executionTest', { symbol: acc.testSymbol }, { title: 'Uruchomić test wykonania na DEMO?', body: 'Zostanie otwarta pozycja o minimalnym wolumenie na ' + (acc.testSymbol || 'płynnym instrumencie FX') + ' i zamknięta po ok. minucie.', button: 'Uruchom test' }) }, icon('test'), 'Test wykonania (DEMO)'),
        isLive ? el('span', { class: 'muted', text: 'Niedostępny na koncie LIVE.' }) : null),
      feed(acc.executionTest, false));
    wrap.appendChild(card('Test otwarcia, zarządzania i zamknięcia pozycji', 'tylko DEMO', test));
    wrap.appendChild(el('div', { class: 'grid cols-2' },
      card('Zdarzenia konta', null, feed(acc.events, false)),
      card('Komunikaty mostu', null, feed((acc.session && acc.session.bridgeLog) || [], false))));
  }
  const logs = (S.data.log || []).map((l) => `${shortTime(l.time)} [${l.level}] ${l.message}`);
  wrap.appendChild(card('Dziennik aplikacji', S.host.dataDir, feed(logs, false)));
  return wrap;
}
