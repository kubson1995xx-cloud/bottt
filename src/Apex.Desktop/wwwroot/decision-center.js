'use strict';
/*
 * Centrum decyzji – wizualizacja procesu decyzyjnego silnika APEX.
 * WYŁĄCZNIE ODCZYT: dane z /api/accounts/{konto}/decisions i /instrument/{symbol}.
 * Każdy impuls, licznik i wykres odpowiada rzeczywistemu zdarzeniu lub wartości policzonej przez silnik.
 * Moduł nie wysyła żadnych poleceń handlowych.
 */
(function () {
  const STAGES = ['Data', 'Indicators', 'Regime', 'Strategy', 'Ranking', 'Risk', 'Execution'];
  const STRATS = ['TrendPullback', 'DonchianBreakout', 'MeanReversion', 'MomentumImpulse', 'IndexDipBuy', 'DailyTrend'];
  const STAGE_PL = { Data: 'Dane rynkowe', Indicators: 'Wskaźniki', Regime: 'Reżim rynku', Strategy: 'Strategia', Ranking: 'Ranking i filtry', Risk: 'RiskEngine', Execution: 'Wykonanie' };
  const REDUCED = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const C = { blue: '#2a78d6', sell: '#eb6834', wait: '#94a3b8', pending: '#6250d6', critical: '#d03b3b', warning: '#fab219', serious: '#ec835a', grid: '#e8ecf2', ink: '#0f172a', ink2: '#475569', muted: '#8a94a6', up: '#2a78d6', down: '#e34948', ema50: '#eb6834', ema200: '#6250d6', card: '#ffffff' };

  const D = {
    root: null, accountKey: null, lastSeq: 0, events: new Map(), instruments: [], strategies: [], scoring: null, risk: null,
    selected: null, tf: 'h1', filter: 'all', search: '', logFilter: 'all', openKey: null, timer: null, loaded: false,
    detail: null, detailFor: null, detailAt: 0, queue: [], launching: false, tiles: new Map(), lastEvalSeen: new Map(), kpiPrev: {},
    els: {},
  };

  const fmt = (v, d) => (v === null || v === undefined || isNaN(v) ? '—' : nf(d).format(v));
  const keyOf = (e) => e.signalId ? 'sig:' + e.signalId : 'seq:' + e.seq;

  function verdictChip(e) {
    if (!e) return el('span', { class: 'dc-verdict wait' }, '—');
    const v = e.verdict || e.lastVerdict;
    const blocked = e.riskFailures && e.riskFailures.length > 0;
    if (v === 'Buy') return el('span', { class: 'dc-verdict buy' }, '▲ BUY');
    if (v === 'Sell') return el('span', { class: 'dc-verdict sell' }, '▼ SELL');
    if (v === 'Pending') return el('span', { class: 'dc-verdict pending' }, '● OCZEKUJE');
    if (blocked) return el('span', { class: 'dc-verdict blocked' }, '■ WAIT · RYZYKO');
    return el('span', { class: 'dc-verdict wait' }, '■ WAIT');
  }

  function shortStrat(id) {
    const s = D.strategies.find((x) => x.id === id);
    return s ? s.name : id;
  }

  /* ------------------------------------------------------------------ montaż */

  function mount() {
    if (D.root) return D.root;
    const root = el('div', { class: 'dc' });
    D.els.live = el('span', { class: 'dc-live off' }, el('span', { class: 'pulse' }), el('span', { text: 'łączenie…' }));
    root.appendChild(el('div', { class: 'dc-hero' },
      el('div', null, el('div', { class: 'dc-title' }, el('span', { text: 'Centrum decyzji' })),
        el('div', { class: 'dc-desc', text: 'Na żywo: jak silnik APEX analizuje instrumenty, liczy wskaźniki, ocenia strategie, sprawdza RiskEngine i dochodzi do decyzji BUY, SELL lub WAIT.' })),
      el('div', { style: 'margin-left:auto;display:flex;flex-direction:column;align-items:flex-end;gap:6px' }, D.els.live,
        el('span', { class: 'dc-readonly' }, icon('shield'), 'Wizualizacja tylko do odczytu – nie wpływa na handel'))));

    D.els.kpis = el('div', { class: 'dc-kpis' });
    root.appendChild(D.els.kpis);

    D.els.net = el('div', { class: 'dc-net' });
    root.appendChild(el('div', { class: 'dc-card' },
      el('h3', null, 'Sieć modułów analitycznych'),
      el('p', { class: 'dc-sub', text: 'Każdy impuls to rzeczywista ocena: instrument × strategia na zamkniętej świecy. Impuls zatrzymuje się w module, w którym zapadła decyzja.' }),
      D.els.net,
      el('div', { class: 'dc-net-legend' },
        el('span', null, el('i', { style: 'background:' + C.blue }), 'BUY (pozycja otwarta)'),
        el('span', null, el('i', { style: 'background:' + C.sell }), 'SELL (pozycja otwarta)'),
        el('span', null, el('i', { style: 'background:' + C.pending }), 'sygnał w toku'),
        el('span', null, el('i', { style: 'background:' + C.wait }), 'WAIT'),
        el('span', null, el('i', { style: 'background:' + C.critical }), 'zablokowane przez RiskEngine'))));
    buildNetwork();

    D.els.scanner = el('div', { class: 'dc-tiles' });
    D.els.scanTools = el('div', { class: 'dc-toolbar' });
    root.appendChild(el('div', { class: 'dc-card' },
      el('h3', null, 'Skaner rynku'),
      el('p', { class: 'dc-sub', text: 'Instrumenty aktualnie skanowane przez silnik: notowania, zmiana 24h (H1), reżim rynku i ostatnia decyzja. Kliknij, aby zobaczyć szczegóły.' }),
      D.els.scanTools, D.els.scanner));

    D.els.detail = el('div', { class: 'dc-card' });
    D.els.gauges = el('div', { class: 'dc-card' });
    root.appendChild(el('div', { class: 'dc-grid-2' }, D.els.detail, D.els.gauges));

    D.els.strats = el('div', { class: 'dc-card' });
    D.els.score = el('div', { class: 'dc-card' });
    root.appendChild(el('div', { class: 'dc-grid-2b' }, D.els.strats, D.els.score));

    D.els.log = el('div', { class: 'dc-card' });
    root.appendChild(D.els.log);
    D.root = root;
    renderToolbar();
    return root;
  }

  /* ------------------------------------------------------------------ sieć modułów */

  const NET = { W: 1240, H: 410 };
  const NODES = {
    Data: { x: 16, y: 180, w: 150, h: 54, t: 'Dane rynkowe', s: 'świece, notowania' },
    Indicators: { x: 196, y: 180, w: 150, h: 54, t: 'Wskaźniki', s: 'EMA · ATR · ADX · RSI' },
    Regime: { x: 376, y: 180, w: 150, h: 54, t: 'Reżim rynku', s: 'trend · zmienność' },
    Ranking: { x: 806, y: 180, w: 150, h: 54, t: 'Ranking i filtry', s: 'koszt · spread · ocena' },
    Risk: { x: 986, y: 180, w: 120, h: 54, t: 'RiskEngine', s: 'limity · korelacja' },
    Buy: { x: 1136, y: 92, w: 90, h: 42, t: 'BUY', s: '' },
    Sell: { x: 1136, y: 186, w: 90, h: 42, t: 'SELL', s: '' },
    Wait: { x: 1136, y: 300, w: 90, h: 42, t: 'WAIT', s: '' },
  };
  STRATS.forEach((id, k) => { NODES['S_' + id] = { x: 568, y: 14 + k * 66, w: 200, h: 50, t: id, s: '' }; });

  const midR = (n) => [n.x + n.w, n.y + n.h / 2];
  const midL = (n) => [n.x, n.y + n.h / 2];
  function curve(a, b) {
    const [x1, y1] = midR(NODES[a]);
    const [x2, y2] = midL(NODES[b]);
    const dx = (x2 - x1) * 0.5;
    return `M ${x1} ${y1} C ${x1 + dx} ${y1}, ${x2 - dx} ${y2}, ${x2} ${y2}`;
  }

  function buildNetwork() {
    const s = svg('svg', { viewBox: `0 0 ${NET.W} ${NET.H}`, role: 'img', 'aria-label': 'Sieć modułów analitycznych APEX' });
    const defs = svg('defs', {});
    const grad = svg('linearGradient', { id: 'dcEdgeGrad', x1: '0', x2: '1' });
    grad.appendChild(svg('stop', { offset: '0', 'stop-color': '#9cc3f5' }));
    grad.appendChild(svg('stop', { offset: '1', 'stop-color': C.blue }));
    defs.appendChild(grad);
    const glow = svg('filter', { id: 'dcGlow', x: '-50%', y: '-50%', width: '200%', height: '200%' });
    glow.appendChild(svg('feGaussianBlur', { stdDeviation: '3', result: 'b' }));
    const merge = svg('feMerge', {});
    merge.appendChild(svg('feMergeNode', { in: 'b' }));
    merge.appendChild(svg('feMergeNode', { in: 'SourceGraphic' }));
    glow.appendChild(merge);
    defs.appendChild(glow);
    s.appendChild(defs);

    D.edges = {};
    const edgeLayer = svg('g', {});
    const addEdge = (a, b) => {
      const d = curve(a, b);
      const base = svg('path', { d, class: 'edge' });
      const flow = svg('path', { d, class: 'edge flow' });
      edgeLayer.appendChild(base);
      edgeLayer.appendChild(flow);
      D.edges[a + '>' + b] = { base, flow };
    };
    addEdge('Data', 'Indicators');
    addEdge('Indicators', 'Regime');
    STRATS.forEach((id) => { addEdge('Regime', 'S_' + id); addEdge('S_' + id, 'Ranking'); });
    addEdge('Ranking', 'Risk');
    addEdge('Risk', 'Buy');
    addEdge('Risk', 'Sell');
    addEdge('Risk', 'Wait');
    addEdge('Ranking', 'Wait');
    s.appendChild(edgeLayer);

    D.nodeEls = {};
    const nodeLayer = svg('g', {});
    for (const [k, n] of Object.entries(NODES)) {
      const g = svg('g', { class: 'node' + (k === 'Buy' ? ' term buy' : k === 'Sell' ? ' term sell' : k === 'Wait' ? ' term wait' : ''), tabindex: '0' });
      g.appendChild(svg('rect', { x: n.x, y: n.y, width: n.w, height: n.h, rx: 12 }));
      const title = svg('text', { x: n.x + 12, y: n.y + (n.s || k.startsWith('S_') ? 21 : n.h / 2 + 5), class: 't' });
      title.textContent = n.t;
      g.appendChild(title);
      const sub = svg('text', { x: n.x + 12, y: n.y + 38, class: 's' });
      sub.textContent = n.s;
      g.appendChild(sub);
      const cnt = svg('text', { x: n.x + n.w - 10, y: n.y + (n.s || k.startsWith('S_') ? 21 : n.h / 2 + 5), class: 'n', 'text-anchor': 'end' });
      cnt.textContent = '0';
      g.appendChild(cnt);
      g.addEventListener('pointerenter', (ev) => showNodeTip(k, ev));
      g.addEventListener('pointermove', (ev) => moveTip(ev));
      g.addEventListener('pointerleave', hideTip);
      g.addEventListener('focus', (ev) => showNodeTip(k, ev));
      g.addEventListener('blur', hideTip);
      nodeLayer.appendChild(g);
      D.nodeEls[k] = { g, title, sub, cnt };
    }
    s.appendChild(nodeLayer);
    D.pulseLayer = svg('g', {});
    s.appendChild(D.pulseLayer);
    D.els.tip = el('div', { class: 'dc-tip' });
    D.els.net.replaceChildren(s, D.els.tip);
    D.svg = s;
  }

  const NODE_HELP = {
    Data: 'Pobiera zamknięte świece i notowania z platformy; sprawdza kompletność, aktualność i poprawność danych.',
    Indicators: 'Liczy wskaźniki (EMA, ATR, ADX/DI, RSI, Bollinger, Donchian, Efficiency Ratio) na zamkniętych świecach.',
    Regime: 'Klasyfikuje reżim: silny/słaby trend, konsolidacja, chaos; stan zmienności z percentyla ATR.',
    Ranking: 'Filtry przed wejściem (rynek, spread, koszt, wiadomości, wielkość pozycji) i ocena okazji 0–100 (ranking, nie prawdopodobieństwo).',
    Risk: 'RiskEngine: limit dzienny, maks. obsunięcie (najgorszy scenariusz), liczba pozycji, dźwignia, margin, korelacje, koncentracja walut.',
    Buy: 'Pozycja KUPNA faktycznie otwarta u brokera (z SL).',
    Sell: 'Pozycja SPRZEDAŻY faktycznie otwarta u brokera (z SL).',
    Wait: 'Brak działania: brak sygnału, odrzucenie przez filtr, blokada RiskEngine lub błąd danych.',
  };

  function showNodeTip(k, ev) {
    const tip = D.els.tip;
    let title = NODES[k].t, body = NODE_HELP[k] || '';
    if (k.startsWith('S_')) {
      const st = D.strategies.find((x) => x.id === k.slice(2));
      if (st) {
        title = st.name;
        const c = st.counters || {};
        body = (st.status === 'Rejected' ? 'Odrzucona przez testy walk-forward – silnik jej NIE ocenia. ' : st.status === 'UserDisabled' ? 'Wyłączona przez użytkownika. ' : 'Aktywna dla: ' + (st.activeClasses.join(', ') || '—') + '. ') +
          `Oceny: ${c.evaluations || 0}, sygnały: ${c.signals || 0}, wykonane: ${c.executed || 0}.`;
      }
    }
    tip.replaceChildren(el('strong', { text: title }), document.createTextNode(body));
    tip.style.opacity = '1';
    moveTip(ev);
  }
  function moveTip(ev) {
    const r = D.els.net.getBoundingClientRect();
    const x = (ev.clientX || r.left + r.width / 2) - r.left + 14;
    const y = (ev.clientY || r.top + 40) - r.top + 14;
    D.els.tip.style.left = Math.min(x, r.width - 290) + 'px';
    D.els.tip.style.top = y + 'px';
  }
  function hideTip() { D.els.tip.style.opacity = '0'; }

  function updateNetworkCounts() {
    const sum = (f) => D.strategies.reduce((a, s) => a + ((s.counters && s.counters[f]) || 0), 0);
    const ev = sum('evaluations'), sk = sum('skipped'), sig = sum('signals'), filt = sum('filtered'), rb = sum('riskBlocked');
    const buy = sum('executedBuy'), sell = sum('executedSell'), fail = sum('executionFailed');
    const set = (k, v) => { if (D.nodeEls[k]) D.nodeEls[k].cnt.textContent = nf(0).format(v); };
    set('Data', ev);
    set('Indicators', ev - sk);
    set('Regime', ev - sk);
    set('Ranking', sig);
    set('Risk', Math.max(0, sig - filt));
    set('Buy', buy);
    set('Sell', sell);
    set('Wait', sum('noSignal') + sk + filt + rb + fail);
    for (const id of STRATS) {
      const st = D.strategies.find((x) => x.id === id);
      const n = D.nodeEls['S_' + id];
      if (!st || !n) continue;
      n.title.textContent = st.name;
      const inactive = st.status === 'Rejected' || st.status === 'UserDisabled';
      n.sub.textContent = st.status === 'Rejected' ? 'odrzucona przez testy · nieoceniana' : st.status === 'UserDisabled' ? 'wyłączona przez użytkownika' : st.status === 'Experimental' ? 'eksperymentalna · tylko DEMO' : 'zweryfikowana';
      n.cnt.textContent = inactive ? '–' : nf(0).format((st.counters.evaluations || 0) - (st.counters.skipped || 0));
      n.g.classList.toggle('inactive', inactive);
      n.g.classList.toggle('active', !inactive);
      for (const kk of ['Regime>S_' + id, 'S_' + id + '>Ranking']) {
        const e = D.edges[kk];
        if (e) { e.flow.style.display = inactive ? 'none' : ''; e.base.classList.toggle('dim', inactive); }
      }
    }
    for (const k of ['Data', 'Indicators', 'Regime', 'Ranking', 'Risk']) D.nodeEls[k].g.classList.add('active');
  }

  /* impulsy – wyłącznie dla nowych, rzeczywistych zdarzeń */
  function routeFor(e) {
    const stageIdx = STAGES.indexOf(e.stage);
    const hops = [];
    const nodes = ['Data'];
    if (stageIdx >= 1) nodes.push('Indicators');
    if (stageIdx >= 2) nodes.push('Regime');
    if (stageIdx >= 3) nodes.push('S_' + e.strategy);
    if (stageIdx >= 4) nodes.push('Ranking');
    if (stageIdx >= 5) nodes.push('Risk');
    let color = C.wait, end = null;
    if (e.verdict === 'Buy') { nodes.push('Buy'); color = C.blue; end = 'Buy'; }
    else if (e.verdict === 'Sell') { nodes.push('Sell'); color = C.sell; end = 'Sell'; }
    else if (e.verdict === 'Pending') { color = C.pending; }
    else if (e.riskFailures && e.riskFailures.length) { color = C.critical; }
    else if (stageIdx >= 4 && e.verdict === 'Wait') { color = C.warning; }
    for (let i = 0; i < nodes.length - 1; i++) {
      const edge = D.edges[nodes[i] + '>' + nodes[i + 1]];
      if (edge) hops.push({ path: edge.base, to: nodes[i + 1] });
    }
    return { hops, color, end, stop: nodes[nodes.length - 1] };
  }

  function enqueuePulse(e) {
    if (REDUCED) { flashNode(routeFor(e).stop); return; }
    D.queue.push(e);
    if (D.queue.length > 40) D.queue.splice(0, D.queue.length - 40);
    if (!D.launching) launchNext();
  }

  function launchNext() {
    const e = D.queue.shift();
    if (!e) { D.launching = false; return; }
    D.launching = true;
    animatePulse(routeFor(e));
    setTimeout(launchNext, 150);
  }

  function flashNode(k) {
    const n = D.nodeEls[k];
    if (!n) return;
    n.g.classList.add('hit');
    setTimeout(() => n.g.classList.remove('hit'), 650);
  }

  function animatePulse(route) {
    if (!route.hops.length) { flashNode(route.stop); return; }
    const dot = svg('circle', { r: '5', fill: route.color, class: 'pulse-dot' });
    const tail = svg('circle', { r: '9', fill: route.color, opacity: '0.18' });
    D.pulseLayer.appendChild(tail);
    D.pulseLayer.appendChild(dot);
    const speed = 0.55; // px na ms
    let hop = 0, start = null;
    const lens = route.hops.map((h) => h.path.getTotalLength());
    function frame(ts) {
      if (start === null) start = ts;
      const len = lens[hop];
      const t = Math.min(1, ((ts - start) * speed) / len);
      const p = route.hops[hop].path.getPointAtLength(t * len);
      dot.setAttribute('cx', p.x); dot.setAttribute('cy', p.y);
      tail.setAttribute('cx', p.x); tail.setAttribute('cy', p.y);
      if (t >= 1) {
        flashNode(route.hops[hop].to);
        hop++;
        start = ts;
        if (hop >= route.hops.length) { finish(p); return; }
      }
      requestAnimationFrame(frame);
    }
    function finish(p) {
      const ring = svg('circle', { cx: p.x, cy: p.y, r: '6', fill: 'none', stroke: route.color, 'stroke-width': '2', opacity: '0.8' });
      D.pulseLayer.appendChild(ring);
      const t0 = performance.now();
      (function grow(now) {
        const k = Math.min(1, (now - t0) / 650);
        ring.setAttribute('r', String(6 + 18 * k));
        ring.setAttribute('opacity', String(0.8 * (1 - k)));
        dot.setAttribute('opacity', String(1 - k));
        tail.setAttribute('opacity', String(0.18 * (1 - k)));
        if (k < 1) requestAnimationFrame(grow); else { ring.remove(); dot.remove(); tail.remove(); }
      })(t0);
    }
    requestAnimationFrame(frame);
  }

  /* ------------------------------------------------------------------ KPI */

  function renderKpis(snapshot) {
    const sum = (f) => D.strategies.reduce((a, s) => a + ((s.counters && s.counters[f]) || 0), 0);
    const items = [
      ['instr', 'Skanowane instrumenty', D.instruments.length],
      ['eval', 'Oceny (od startu silnika)', sum('evaluations')],
      ['sig', 'Sygnały strategii', sum('signals')],
      ['filt', 'Odrzucone przez filtry', sum('filtered')],
      ['risk', 'Zablokowane przez RiskEngine', sum('riskBlocked')],
      ['exec', 'Wykonane (BUY/SELL)', sum('executed')],
    ];
    D.els.kpis.replaceChildren(...items.map(([k, l, v]) => {
      const box = el('div', { class: 'dc-kpi' + (D.kpiPrev[k] !== undefined && D.kpiPrev[k] !== v ? ' flash' : '') }, el('div', { class: 'l', text: l }), el('div', { class: 'v', text: nf(0).format(v) }));
      D.kpiPrev[k] = v;
      return box;
    }));
    const live = D.els.live;
    const ok = snapshot.connected && snapshot.engineReady;
    live.classList.toggle('off', !ok);
    live.lastChild.textContent = ok ? 'na żywo · cykl ' + (snapshot.lastCycleUtc ? new Date(snapshot.lastCycleUtc).toLocaleTimeString('pl-PL') : '—') : snapshot.engineReady ? 'most rozłączony' : 'silnik w przygotowaniu';
  }

  /* ------------------------------------------------------------------ skaner */

  function renderToolbar() {
    const chips = [['all', 'Wszystkie'], ['signals', 'Sygnały i decyzje'], ['positions', 'Z pozycją'], ['open', 'Rynek otwarty']];
    const search = el('input', { class: 'dc-search', type: 'search', placeholder: 'Szukaj instrumentu…', 'aria-label': 'Szukaj instrumentu', value: D.search, oninput: (ev) => { D.search = ev.target.value; renderScanner(); } });
    D.els.scanTools.replaceChildren(...chips.map(([k, n]) => el('button', { class: 'dc-chip', 'aria-pressed': String(D.filter === k), onclick: () => { D.filter = k; renderToolbar(); renderScanner(); } }, n)), search);
  }

  function sparkline(closes) {
    const s = svg('svg', { class: 'spark', viewBox: '0 0 200 34', preserveAspectRatio: 'none', 'aria-hidden': 'true' });
    if (!closes || closes.length < 2) return s;
    const lo = Math.min(...closes), hi = Math.max(...closes), span = hi - lo || 1;
    const pts = closes.map((c, i) => [(i / (closes.length - 1)) * 200, 30 - ((c - lo) / span) * 26]);
    const up = closes[closes.length - 1] >= closes[0];
    const color = up ? C.up : C.down;
    const d = pts.map((p, i) => (i ? 'L' : 'M') + p[0].toFixed(1) + ' ' + p[1].toFixed(1)).join(' ');
    s.appendChild(svg('path', { d: d + ' L 200 34 L 0 34 Z', fill: color, opacity: '0.08' }));
    s.appendChild(svg('path', { d, fill: 'none', stroke: color, 'stroke-width': '1.6', 'vector-effect': 'non-scaling-stroke', 'stroke-linejoin': 'round' }));
    return s;
  }

  function digitsFor(v) { return v >= 1000 ? 1 : v >= 100 ? 2 : v >= 10 ? 3 : 5; }

  function renderScanner() {
    let list = D.instruments.slice();
    if (D.filter === 'signals') list = list.filter((x) => x.lastVerdict && x.lastVerdict !== 'Wait');
    if (D.filter === 'positions') list = list.filter((x) => x.hasPosition);
    if (D.filter === 'open') list = list.filter((x) => x.marketOpen);
    if (D.search) list = list.filter((x) => x.symbol.toLowerCase().includes(D.search.toLowerCase()));
    if (!list.length) {
      D.els.scanner.replaceChildren(el('div', { class: 'dc-empty', text: D.instruments.length ? 'Brak instrumentów dla wybranego filtra.' : 'Silnik nie skanuje jeszcze żadnych instrumentów (brak zweryfikowanej strategii dla klas dostępnych u brokera lub trwa ładowanie danych).' }));
      return;
    }
    const nodes = list.map((x) => {
      const fresh = x.lastEvaluationUtc && D.lastEvalSeen.get(x.symbol) && D.lastEvalSeen.get(x.symbol) !== x.lastEvaluationUtc;
      D.lastEvalSeen.set(x.symbol, x.lastEvaluationUtc);
      const px = x.bid ? nf(digitsFor(x.bid)).format(x.bid) : '—';
      const chg = x.change24h;
      const tile = el('button', { class: 'dc-tile' + (fresh ? ' fresh' : ''), 'aria-pressed': String(D.selected === x.symbol), onclick: () => select(x.symbol) },
        el('div', { class: 'row' }, el('span', { class: 'sym', text: x.symbol }), x.hasPosition ? el('span', { class: 'dc-pos', text: 'POZYCJA' }) : null, el('span', { class: 'cls', text: x.className })),
        el('div', { class: 'row' }, el('span', { class: 'px', text: px }), el('span', { class: 'chg ' + (chg > 0 ? 'dc-up' : chg < 0 ? 'dc-down' : ''), style: 'margin-left:auto', text: chg === null || chg === undefined ? '' : (chg > 0 ? '+' : '') + nf(2).format(chg) + '% 24h' })),
        sparkline(x.closes),
        el('div', { class: 'row', style: 'margin-top:6px;flex-wrap:wrap' },
          x.regime ? el('span', { class: 'dc-regime', text: x.regime.regimeName }) : el('span', { class: 'dc-regime', text: 'reżim: brak oceny' }),
          x.lastVerdict ? verdictChip({ verdict: x.lastVerdict }) : null),
        el('div', { class: 'meta', text: (x.marketOpen ? 'rynek otwarty' : 'rynek zamknięty') + (x.lastEvaluationUtc ? ' · ocena ' + new Date(x.lastEvaluationUtc).toLocaleTimeString('pl-PL') : ' · czeka na zamknięcie świecy') }));
      tile.title = x.lastOutcome || '';
      return tile;
    });
    D.els.scanner.replaceChildren(...nodes);
  }

  function select(symbol) {
    D.selected = symbol;
    D.detail = null;
    D.detailFor = null;
    renderScanner();
    loadDetail(true);
  }

  /* ------------------------------------------------------------------ szczegóły instrumentu */

  async function loadDetail(force) {
    if (!D.selected || !D.accountKey) { renderDetail(); return; }
    if (!force && D.detailFor === D.selected && Date.now() - D.detailAt < 30000) return;
    try {
      const r = await fetch('/api/accounts/' + encodeURIComponent(D.accountKey) + '/instrument/' + encodeURIComponent(D.selected), { headers: { 'X-Apex-Token': TOKEN } });
      D.detail = r.ok ? await r.json() : null;
    } catch (e) { D.detail = null; }
    D.detailFor = D.selected;
    D.detailAt = Date.now();
    renderDetail();
  }

  function renderDetail() {
    const box = D.els.detail;
    if (!D.selected) {
      box.replaceChildren(el('h3', null, 'Analiza instrumentu'), el('div', { class: 'dc-empty', text: 'Wybierz instrument w skanerze, aby zobaczyć wykres i wskaźniki, na których silnik opiera decyzje.' }));
      renderGauges(null);
      return;
    }
    const series = D.detail ? D.detail[D.tf] : null;
    const seg = el('div', { class: 'dc-seg', role: 'group', 'aria-label': 'Interwał' }, ...[['h1', 'H1'], ['h4', 'H4'], ['d1', 'D1']].map(([k, n]) =>
      el('button', { 'aria-pressed': String(D.tf === k), onclick: () => { D.tf = k; renderDetail(); } }, n)));
    const instr = D.instruments.find((x) => x.symbol === D.selected);
    box.replaceChildren(
      el('div', { class: 'dc-detail-head' }, el('span', { class: 'sym', text: D.selected }), instr ? el('span', { class: 'dc-regime', text: instr.className }) : null, seg),
      series ? candleChart(series) : el('div', { class: 'dc-empty', text: 'Brak danych świecowych dla tego interwału.' }),
      el('p', { class: 'dc-note', text: 'Świece i średnie EMA liczone z tych samych zamkniętych świec, które analizuje silnik. Znaczniki ▲/▼ – pozycje otwarte przez APEX na tym instrumencie.' }));
    renderGauges(series);
  }

  function candleChart(series) {
    const bars = series.bars.slice(-120);
    const wrap = el('div', { class: 'dc-chart' });
    const W = 760, H = 300, L = 8, R = 64, T = 12, B = 24;
    const lows = bars.map((b) => b[3]), highs = bars.map((b) => b[2]);
    const emaVals = bars.flatMap((b) => [b[5], b[6]]).filter((v) => v !== null);
    let lo = Math.min(...lows, ...emaVals), hi = Math.max(...highs, ...emaVals);
    const pad = (hi - lo) * 0.06 || 1;
    lo -= pad; hi += pad;
    const n = bars.length;
    const step = (W - L - R) / n;
    const X = (i) => L + step * (i + 0.5);
    const Y = (v) => T + (1 - (v - lo) / (hi - lo)) * (H - T - B);
    const s = svg('svg', { viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: 'none', role: 'img', 'aria-label': 'Wykres świecowy z EMA50 i EMA200' });
    const ticks = niceTicks(lo, hi, 5).filter((t) => t >= lo && t <= hi);
    const tstep = ticks.length > 1 ? ticks[1] - ticks[0] : 1;
    const td = tstep >= 1 ? 0 : tstep >= 0.1 ? 1 : tstep >= 0.01 ? 2 : tstep >= 0.001 ? 3 : 5;
    for (const t of ticks) {
      s.appendChild(svg('line', { x1: L, x2: W - R, y1: Y(t), y2: Y(t), stroke: C.grid, 'stroke-width': '1', 'vector-effect': 'non-scaling-stroke' }));
      const lb = svg('text', { x: W - R + 6, y: Y(t) + 4, fill: C.muted, 'font-size': '11' });
      lb.textContent = nf(td).format(t);
      s.appendChild(lb);
    }
    const bw = Math.max(1.5, Math.min(8, step * 0.62));
    bars.forEach((b, i) => {
      const up = b[4] >= b[1];
      const col = up ? C.up : C.down;
      s.appendChild(svg('line', { x1: X(i), x2: X(i), y1: Y(b[2]), y2: Y(b[3]), stroke: col, 'stroke-width': '1', 'vector-effect': 'non-scaling-stroke' }));
      const y1 = Y(Math.max(b[1], b[4])), y2 = Y(Math.min(b[1], b[4]));
      s.appendChild(svg('rect', { x: X(i) - bw / 2, y: y1, width: bw, height: Math.max(1, y2 - y1), fill: col, rx: '1' }));
    });
    const line = (idx, color) => {
      let d = '';
      bars.forEach((b, i) => { if (b[idx] !== null) d += (d ? 'L' : 'M') + X(i).toFixed(1) + ' ' + Y(b[idx]).toFixed(1) + ' '; });
      if (d) s.appendChild(svg('path', { d, fill: 'none', stroke: color, 'stroke-width': '2', 'stroke-linejoin': 'round', 'vector-effect': 'non-scaling-stroke' }));
    };
    line(5, C.ema50);
    line(6, C.ema200);
    // Znaczniki rzeczywistych wejść APEX (zdarzenia BUY/SELL dla tego instrumentu w zakresie wykresu).
    const t0 = bars[0][0], tN = bars[n - 1][0];
    for (const e of D.events.values()) {
      if (e.symbol !== D.selected || (e.verdict !== 'Buy' && e.verdict !== 'Sell')) continue;
      const t = Date.parse(e.updatedUtc);
      if (t < t0 || t > tN + 86400000) continue;
      let idx = bars.findIndex((b) => b[0] >= t);
      if (idx < 0) idx = n - 1;
      const buy = e.verdict === 'Buy';
      const y = buy ? Y(bars[idx][3]) + 12 : Y(bars[idx][2]) - 12;
      const tri = buy ? `M ${X(idx)} ${y - 6} l 6 10 l -12 0 Z` : `M ${X(idx)} ${y + 6} l 6 -10 l -12 0 Z`;
      s.appendChild(svg('path', { d: tri, fill: buy ? C.blue : C.sell, stroke: C.card, 'stroke-width': '1.5' }));
    }
    const cross = svg('line', { y1: T, y2: H - B, stroke: '#94a3b8', 'stroke-width': '1', opacity: '0', 'vector-effect': 'non-scaling-stroke' });
    s.appendChild(cross);
    const tip = el('div', { class: 'dc-ctip' });
    const hit = svg('rect', { x: L, y: T, width: W - L - R, height: H - T - B, fill: 'transparent', tabindex: '0', 'aria-label': 'Najedź, aby zobaczyć wartości świecy' });
    const show = (clientX) => {
      const r = s.getBoundingClientRect();
      const vx = ((clientX - r.left) / r.width) * W;
      const i = Math.max(0, Math.min(n - 1, Math.floor((vx - L) / step)));
      const b = bars[i];
      cross.setAttribute('x1', X(i)); cross.setAttribute('x2', X(i)); cross.setAttribute('opacity', '1');
      const d = digitsFor(b[4]);
      tip.replaceChildren(el('div', { text: new Date(b[0]).toLocaleString('pl-PL', { dateStyle: 'short', timeStyle: 'short' }) }),
        el('div', { text: `O ${nf(d).format(b[1])}  H ${nf(d).format(b[2])}  L ${nf(d).format(b[3])}  C ${nf(d).format(b[4])}` }),
        el('div', { text: `EMA50 ${b[5] === null ? '—' : nf(d).format(b[5])}  ·  EMA200 ${b[6] === null ? '—' : nf(d).format(b[6])}` }));
      tip.style.left = Math.min(((X(i) / W) * r.width) + 12, r.width - 260) + 'px';
      tip.style.top = '8px';
      tip.style.opacity = '1';
    };
    hit.addEventListener('pointermove', (ev) => show(ev.clientX));
    hit.addEventListener('pointerleave', () => { cross.setAttribute('opacity', '0'); tip.style.opacity = '0'; });
    hit.addEventListener('focus', () => { const r = s.getBoundingClientRect(); show(r.right - 80); });
    hit.addEventListener('blur', () => { tip.style.opacity = '0'; });
    s.appendChild(hit);
    wrap.appendChild(s);
    wrap.appendChild(tip);
    wrap.appendChild(el('div', { class: 'dc-legend' },
      el('span', null, el('i', { style: 'background:' + C.up }), 'świeca wzrostowa'),
      el('span', null, el('i', { style: 'background:' + C.down }), 'świeca spadkowa'),
      el('span', null, el('i', { style: 'background:' + C.ema50 }), 'EMA 50'),
      el('span', null, el('i', { style: 'background:' + C.ema200 }), 'EMA 200')));
    return wrap;
  }

  function gaugeArc(value, max, marks) {
    const s = svg('svg', { viewBox: '0 0 160 76', 'aria-hidden': 'true' });
    const cx = 80, cy = 72, r = 60;
    const pt = (v) => { const a = Math.PI * (1 - Math.max(0, Math.min(max, v)) / max); return [cx + r * Math.cos(a), cy - r * Math.sin(a)]; };
    const [sx, sy] = pt(0), [ex, ey] = pt(max);
    s.appendChild(svg('path', { d: `M ${sx} ${sy} A ${r} ${r} 0 0 1 ${ex} ${ey}`, fill: 'none', stroke: '#e9eef5', 'stroke-width': '10', 'stroke-linecap': 'round' }));
    const [vx, vy] = pt(value);
    if (value > 0) s.appendChild(svg('path', { d: `M ${sx} ${sy} A ${r} ${r} 0 0 1 ${vx} ${vy}`, fill: 'none', stroke: C.blue, 'stroke-width': '10', 'stroke-linecap': 'round' }));
    for (const m of marks || []) {
      const a = Math.PI * (1 - m / max);
      s.appendChild(svg('line', { x1: cx + (r - 9) * Math.cos(a), y1: cy - (r - 9) * Math.sin(a), x2: cx + (r + 9) * Math.cos(a), y2: cy - (r + 9) * Math.sin(a), stroke: C.ink2, 'stroke-width': '1.5' }));
    }
    return s;
  }

  function barGauge(value, max, marks) {
    const b = el('div', { class: 'dc-bar' }, el('span', { style: 'width:' + Math.max(0, Math.min(100, (value / max) * 100)).toFixed(1) + '%' }));
    for (const m of marks || []) b.appendChild(el('i', { style: 'left:' + ((m / max) * 100).toFixed(1) + '%' }));
    return b;
  }

  function renderGauges(series) {
    const box = D.els.gauges;
    const r = series && series.regime;
    if (!r) {
      box.replaceChildren(el('h3', null, 'Reżim rynku i wskaźniki'), el('div', { class: 'dc-empty', text: D.selected ? 'Za mało świec, aby policzyć reżim na tym interwale.' : 'Wybierz instrument w skanerze.' }));
      return;
    }
    const dirTxt = r.trendDirection > 0 ? 'wzrostowy' : r.trendDirection < 0 ? 'spadkowy' : 'brak';
    box.replaceChildren(
      el('h3', null, 'Reżim rynku i wskaźniki ', el('span', { class: 'dc-regime', text: r.regimeName })),
      el('p', { class: 'dc-sub', text: `Interwał ${r.tf}, ostatnia zamknięta świeca. Progi jak w silniku: konsolidacja ADX < 20, trend ≥ 20, silny trend ≥ 25.` }),
      el('div', { class: 'dc-gauges' },
        el('div', { class: 'dc-gauge' }, el('div', { class: 'l', text: 'Siła trendu – ADX(14)' }), gaugeArc(r.adx, 60, [20, 25]), el('div', { class: 'v', text: fmt(r.adx, 1) }), el('div', { class: 'd', text: `+DI ${fmt(r.plusDi, 1)} · −DI ${fmt(r.minusDi, 1)}` })),
        el('div', { class: 'dc-gauge' }, el('div', { class: 'l', text: 'Kierunek (EMA50/EMA200)' }), el('div', { class: 'v', text: dirTxt }), el('div', { class: 'd', text: `nachylenie EMA50: ${fmt(r.fastSlopeAtr, 3)} ATR/świecę` }), barGauge(Math.abs(r.fastSlopeAtr), 0.3, [])),
        el('div', { class: 'dc-gauge' }, el('div', { class: 'l', text: 'Efektywność ruchu (Kaufman ER)' }), el('div', { class: 'v', text: fmt(r.efficiencyRatio, 2) }), el('div', { class: 'd', text: '1 = czysty trend, 0 = szum' }), barGauge(r.efficiencyRatio, 1, [])),
        el('div', { class: 'dc-gauge' }, el('div', { class: 'l', text: 'Zmienność – percentyl ATR(14)' }), el('div', { class: 'v', text: fmt(r.atrPercentile * 100, 0) + '%' }), el('div', { class: 'd', text: `stan: ${({ Low: 'niska', Normal: 'normalna', High: 'wysoka' })[r.volatility] || 'nieznana'} · ATR ${fmt(r.atr, digitsFor(r.atr * 100))}` }), barGauge(r.atrPercentile, 1, [0.15, 0.9]))),
      el('p', { class: 'dc-note', text: 'Uzasadnienie klasyfikacji: ' + (r.reason || '—') }));
  }

  /* ------------------------------------------------------------------ strategie */

  const OUTCOMES = [
    ['noSignal', 'Brak sygnału', '#cbd5e1'],
    ['skipped', 'Pominięte (dane/pozycja)', '#e2e8f0'],
    ['filtered', 'Odrzucone przez filtry', C.warning],
    ['riskBlocked', 'Zablokowane przez RiskEngine', C.critical],
    ['executionFailed', 'Błąd wykonania', C.serious],
    ['executed', 'Wykonane', C.blue],
  ];

  function renderStrategies() {
    const box = D.els.strats;
    const rows = STRATS.map((id) => {
      const st = D.strategies.find((x) => x.id === id) || { name: id, status: 'Rejected', counters: {}, activeClasses: [] };
      const c = st.counters || {};
      const pending = Math.max(0, (c.signals || 0) - (c.filtered || 0) - (c.riskBlocked || 0) - (c.executed || 0) - (c.executionFailed || 0));
      const badge = st.status === 'Validated' ? el('span', { class: 'dc-badge ok', text: '✓ ZWERYFIKOWANA' })
        : st.status === 'Experimental' ? el('span', { class: 'dc-badge exp', text: '⚠ EKSPERYMENTALNA – DEMO' })
          : st.status === 'UserDisabled' ? el('span', { class: 'dc-badge off', text: 'WYŁĄCZONA' })
            : el('span', { class: 'dc-badge rej', text: '✕ ODRZUCONA PRZEZ TESTY' });
      const total = OUTCOMES.reduce((a, [k]) => a + (c[k] || 0), 0) + pending;
      let bar;
      if (st.status === 'Rejected') bar = el('div', { class: 'dc-empty', style: 'padding:0;text-align:left;font-size:12px', text: 'Nie oceniana – nie spełniła kryteriów walk-forward.' });
      else if (total === 0) bar = el('div', { class: 'dc-stack', title: 'Brak ocen od startu silnika' });
      else {
        const segs = OUTCOMES.map(([k, n, col]) => [k, n, col, c[k] || 0]).concat([['pending', 'W toku', C.pending, pending]]).filter((x) => x[3] > 0);
        bar = el('div', { class: 'dc-stack', role: 'img', 'aria-label': segs.map((x) => `${x[1]}: ${x[3]}`).join(', ') },
          ...segs.map(([k, n, col, v]) => el('span', { style: `flex-grow:${v};background:${col}`, title: `${n}: ${v}` })));
      }
      return el('div', { class: 'dc-strat' },
        el('div', null, el('div', { class: 'name', text: st.name }), el('div', { class: 'st' }, badge, st.activeClasses.length ? el('span', { text: st.activeClasses.join(', ') }) : null)),
        bar,
        el('div', { class: 'tot', title: 'liczba ocen', text: st.status === 'Rejected' ? '–' : nf(0).format(total) }));
    });
    box.replaceChildren(
      el('h3', null, 'Ocena strategii'),
      el('p', { class: 'dc-sub', text: 'Wyniki rzeczywistych ocen od startu silnika. Strategie odrzucone przez testy nie są oceniane (bot nie generuje nimi sygnałów).' }),
      el('div', { class: 'dc-legend-row' }, ...OUTCOMES.concat([['pending', 'W toku', C.pending]]).map(([k, n, col]) => el('span', null, el('i', { style: 'background:' + col }), n))),
      ...rows);
  }

  /* ------------------------------------------------------------------ ocena okazji */

  function renderScore() {
    const box = D.els.score;
    const open = D.openKey ? D.events.get(D.openKey) : null;
    const candidate = open && open.score ? open : Array.from(D.events.values()).filter((e) => e.score).sort((a, b) => b.seq - a.seq)[0];
    if (!candidate || !D.scoring) {
      box.replaceChildren(el('h3', null, 'Ocena okazji (ranking)'),
        el('div', { class: 'dc-empty', text: 'Brak ocenionych kandydatów od startu silnika – żadna strategia nie wygenerowała sygnału, który przeszedłby filtry wstępne.' }),
        el('p', { class: 'dc-note', text: 'Ocena 0–100 to ranking jakości warunków według jawnych reguł, a nie prawdopodobieństwo zysku.' }));
      return;
    }
    const sc = candidate.score, w = D.scoring.weights, tw = D.scoring.totalWeight || 100;
    const comps = [
      ['mtfAlignment', 'Zgodność interwałów'], ['trendStrength', 'Siła trendu'], ['momentum', 'Momentum'], ['volatility', 'Zmienność'],
      ['cost', 'Koszt transakcji'], ['rewardRisk', 'Zysk/ryzyko'], ['regimeFit', 'Dopasowanie do reżimu'],
    ];
    const min = D.scoring.minScore;
    box.replaceChildren(
      el('h3', null, 'Ocena okazji (ranking) ', verdictChip(candidate)),
      el('p', { class: 'dc-sub', text: `${candidate.symbol} · ${shortStrat(candidate.strategy)} · ${new Date(candidate.updatedUtc).toLocaleString('pl-PL')}` }),
      el('div', { class: 'dc-total' }, el('span', { class: 'big', text: nf(1).format(sc.total) }), el('span', { class: 'of', text: '/ 100 · próg ' + nf(0).format(min) })),
      el('div', { class: 'dc-threshold' }, el('span', { style: 'width:' + Math.max(0, Math.min(100, sc.total)).toFixed(1) + '%' }), el('i', { style: 'left:' + min + '%' }), el('b', { style: 'left:' + min + '%', text: 'próg' })),
      el('div', { class: 'dc-score' }, ...comps.map(([k, n]) => {
        const v = sc[k] || 0, pts = (v * w[k]) / tw * 100;
        return el('div', { class: 'row' }, el('span', { class: 'lbl', text: n }), el('div', { class: 'track', title: `składowa ${nf(2).format(v)} × waga ${w[k]}` }, el('span', { style: 'width:' + (v * 100).toFixed(1) + '%' })), el('span', { class: 'pts', text: nf(1).format(pts) + ' pkt' }));
      })),
      el('p', { class: 'dc-note', text: `Kara za korelację z otwartymi pozycjami: −${nf(1).format(sc.correlationPenalty || 0)} pkt. Koszt transakcji = ${nf(0).format((sc.costToRisk || 0) * 100)}% ryzyka. Ocena to ranking według jawnych reguł – NIE jest prawdopodobieństwem zysku.` }));
  }

  /* ------------------------------------------------------------------ uzasadnienia */

  function regimeKv(r, title) {
    if (!r) return null;
    return el('div', null, el('div', { class: 'k', text: title }), el('div', { class: 'v', text: `${r.regimeName} · ADX ${fmt(r.adx, 1)} · ER ${fmt(r.efficiencyRatio, 2)}` }));
  }

  function renderLog() {
    const box = D.els.log;
    const chips = [['all', 'Wszystkie'], ['signals', 'Sygnały'], ['trades', 'BUY / SELL'], ['blocked', 'Odrzucone / zablokowane']];
    let list = Array.from(D.events.values()).sort((a, b) => b.seq - a.seq);
    if (D.logFilter === 'signals') list = list.filter((e) => STAGES.indexOf(e.stage) >= 3 && (e.signalId || e.verdict !== 'Wait'));
    if (D.logFilter === 'trades') list = list.filter((e) => e.verdict === 'Buy' || e.verdict === 'Sell');
    if (D.logFilter === 'blocked') list = list.filter((e) => e.verdict === 'Wait' && STAGES.indexOf(e.stage) >= 4);
    list = list.slice(0, 120);
    const items = list.map((e) => {
      const k = keyOf(e);
      const open = D.openKey === k;
      const body = el('div', { class: 'body' });
      if (open) {
        body.appendChild(el('ul', { class: 'dc-gates' }, ...e.gates.map((g) => el('li', null, el('span', { class: g.passed ? 'ok' : 'no', text: g.passed ? '✓' : '✕', 'aria-label': g.passed ? 'spełnione' : 'niespełnione' }), el('span', { class: 'gname', text: g.name }), el('span', { class: 'gdet', text: g.detail })))));
        const kv = [];
        if (e.direction) kv.push(el('div', null, el('div', { class: 'k', text: 'Kierunek' }), el('div', { class: 'v', text: e.direction === 'Long' ? 'KUPNO' : 'SPRZEDAŻ' })));
        if (e.referencePrice) kv.push(el('div', null, el('div', { class: 'k', text: 'Cena odniesienia' }), el('div', { class: 'v', text: String(e.referencePrice) })));
        if (e.stopDistance) kv.push(el('div', null, el('div', { class: 'k', text: 'Odległość SL' }), el('div', { class: 'v', text: nf(5).format(e.stopDistance) })));
        if (e.takeProfitDistance) kv.push(el('div', null, el('div', { class: 'k', text: 'Odległość TP' }), el('div', { class: 'v', text: nf(5).format(e.takeProfitDistance) })));
        if (e.plannedRewardRisk) kv.push(el('div', null, el('div', { class: 'k', text: 'Planowany zysk/ryzyko' }), el('div', { class: 'v', text: nf(2).format(e.plannedRewardRisk) })));
        if (e.units) kv.push(el('div', null, el('div', { class: 'k', text: 'Wolumen (jedn.)' }), el('div', { class: 'v', text: nf(2).format(e.units) })));
        if (e.riskAmount) kv.push(el('div', null, el('div', { class: 'k', text: 'Ryzyko do SL' }), el('div', { class: 'v', text: nf(2).format(e.riskAmount) })));
        if (e.costToRisk !== null && e.costToRisk !== undefined) kv.push(el('div', null, el('div', { class: 'k', text: 'Koszt / ryzyko' }), el('div', { class: 'v', text: nf(1).format(e.costToRisk * 100) + '%' })));
        const r1 = regimeKv(e.signalRegime, 'Reżim ' + (e.signalTf || ''));
        const r2 = regimeKv(e.filterRegime, 'Reżim ' + (e.filterTf || '') + ' (filtr)');
        if (r1) kv.push(r1);
        if (r2) kv.push(r2);
        if (kv.length) body.appendChild(el('div', { class: 'dc-kv' }, ...kv));
        if (e.strategyReason) body.appendChild(el('p', { class: 'dc-note', text: 'Uzasadnienie strategii: ' + e.strategyReason }));
        if (e.riskFailures && e.riskFailures.length) body.appendChild(el('p', { class: 'dc-note', text: 'RiskEngine: ' + e.riskFailures.join('; ') }));
      }
      const item = el('div', { class: 'dc-ev' + (open ? ' open' : '') + (e._new ? ' new' : '') },
        el('button', { 'aria-expanded': String(open), onclick: () => { D.openKey = open ? null : k; renderLog(); renderScore(); } },
          verdictChip(e),
          el('span', null, el('span', { class: 'sym', text: e.symbol })),
          el('span', { class: 'out', text: `${shortStrat(e.strategy)} (${e.signalTf}) · ${STAGE_PL[e.stage] || e.stage} · ${e.outcome}` }),
          el('span', { class: 'tm', text: new Date(e.updatedUtc).toLocaleTimeString('pl-PL') })),
        body);
      e._new = false;
      return item;
    });
    box.replaceChildren(
      el('h3', null, 'Uzasadnienie decyzji'),
      el('p', { class: 'dc-sub', text: 'Każda ocena z listą punktów kontrolnych i ich rzeczywistymi wynikami. Kliknij wpis, aby rozwinąć.' }),
      el('div', { class: 'dc-toolbar' }, ...chips.map(([k, n]) => el('button', { class: 'dc-chip', 'aria-pressed': String(D.logFilter === k), onclick: () => { D.logFilter = k; renderLog(); } }, n))),
      items.length ? el('div', { class: 'dc-log' }, ...items) : el('div', { class: 'dc-empty', text: 'Brak zdarzeń – silnik ocenia instrumenty po zamknięciu każdej świecy interwału sygnałowego (np. co godzinę dla H1, raz dziennie dla D1).' }));
  }

  /* ------------------------------------------------------------------ odpytywanie (tylko GET) */

  async function poll() {
    if (!D.accountKey) return;
    let snap;
    try {
      const r = await fetch('/api/accounts/' + encodeURIComponent(D.accountKey) + '/decisions?since=' + D.lastSeq, { headers: { 'X-Apex-Token': TOKEN } });
      if (!r.ok) return;
      snap = await r.json();
    } catch (e) { return; }
    const first = !D.loaded;
    for (const e of snap.events || []) {
      const k = keyOf(e);
      e._new = !first;
      D.events.set(k, e);
      if (!first) enqueuePulse(e);
    }
    if (D.events.size > 800) {
      const keep = Array.from(D.events.values()).sort((a, b) => b.seq - a.seq).slice(0, 600);
      D.events = new Map(keep.map((e) => [keyOf(e), e]));
    }
    D.lastSeq = snap.lastSeq || D.lastSeq;
    D.instruments = snap.instruments || [];
    D.strategies = snap.strategies || [];
    D.scoring = snap.scoring;
    D.risk = snap.risk;
    D.loaded = true;
    renderKpis(snap);
    updateNetworkCounts();
    renderScanner();
    renderStrategies();
    renderScore();
    if ((snap.events || []).length || first) renderLog();
    if (!D.selected && D.instruments.length) select(D.instruments[0].symbol);
    else loadDetail(false);
  }

  function setAccount(acc) {
    const key = acc ? acc.key : null;
    if (key === D.accountKey) return;
    D.accountKey = key;
    D.lastSeq = 0;
    D.events = new Map();
    D.loaded = false;
    D.selected = null;
    D.detail = null;
    D.openKey = null;
    D.kpiPrev = {};
    D.lastEvalSeen = new Map();
    if (D.root) { renderDetail(); poll(); }
  }

  function start() {
    if (D.timer) return;
    poll();
    D.timer = setInterval(poll, 2000);
  }

  function stop() {
    if (D.timer) clearInterval(D.timer);
    D.timer = null;
  }

  window.ApexDC = { mount, setAccount, start, stop };
})();
