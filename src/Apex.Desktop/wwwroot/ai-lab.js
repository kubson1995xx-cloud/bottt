'use strict';
/*
 * AI & Auto-Updates – panel modułu APEX AI Lab (osobny proces).
 * Panel czyta stan AI Lab przez /api/ai/* (APEX Desktop przekazuje zapytania). Żadna zmiana nie trafia do działającego
 * silnika bez akceptacji użytkownika: zatwierdzenie buduje NOWE wydanie, aktywne po ponownym uruchomieniu APEX.
 */
(function () {
  const STAGES = [
    ['Analysis', 'Analiza'], ['Workspace', 'Izolowana kopia'], ['Baseline', 'Wersja bazowa'], ['Agent', 'Agent AI'],
    ['Audit', 'Audyt'], ['Build', 'Kompilacja'], ['Tests', 'Testy'], ['Backtest', 'Backtest'], ['Review', 'Przegląd'],
  ];
  const STATUS = {
    Running: ['W toku', 'accent'], AnalysisOnly: ['Tylko analiza', 'neutral'], NoChanges: ['Bez zmian', 'neutral'],
    ReadyForApproval: ['Gotowa do decyzji', 'good'], NeedsAttention: ['Wymaga uwagi', 'warning'], RejectedByChecks: ['Odrzucona przez kontrole', 'critical'],
    Failed: ['Błąd', 'critical'], Cancelled: ['Przerwana', 'neutral'], Throttled: ['Wstrzymana – limity', 'warning'], Deploying: ['Wdrażanie…', 'accent'],
    Deployed: ['Wdrożona', 'good'], RejectedByUser: ['Odrzucona przez Ciebie', 'neutral'], RolledBack: ['Wycofana', 'warning'],
  };
  const TRIGGER = { Manual: 'ręcznie', Schedule: 'harmonogram', StrategyDegradation: 'pogorszenie strategii', MarketChange: 'zmiana rynku', ExecutionErrors: 'błędy wykonania' };
  const PROVIDER = { Codex: 'Codex (ChatGPT)', ClaudeCode: 'Claude Code', OpenAiApi: 'OpenAI API', AnthropicApi: 'Anthropic API' };
  const PROVIDERS = ['Codex', 'ClaudeCode', 'OpenAiApi', 'AnthropicApi'];
  const EVENT_ICON = { stage: '◆', agent: '•', command: '›', file: '✎', plan: '☰', message: '💬', thinking: '…', warning: '!', error: '✕', usage: 'Σ' };
  const DAYS = [['Monday', 'poniedziałek'], ['Tuesday', 'wtorek'], ['Wednesday', 'środa'], ['Thursday', 'czwartek'], ['Friday', 'piątek'], ['Saturday', 'sobota'], ['Sunday', 'niedziela']];

  const A = {
    root: null, timer: null, evTimer: null, state: null, module: null, tab: lsGet('apex-ai-tab') || 'overview',
    selected: null, detail: null, detailAt: 0, events: [], eventsJob: null, lastEvent: 0, cfg: null, cfgDirty: false, els: {}, starting: false,
  };

  /** Zapytanie do AI Lab przez APEX Desktop. Zwraca dane JSON z polami ok (2xx) i httpStatus. */
  async function lab(path, body) {
    const opts = { headers: { 'X-Apex-Token': TOKEN } };
    if (body !== undefined) { opts.method = 'POST'; opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
    try {
      const r = await fetch('/api/ai/' + path, opts);
      let data = {};
      try { data = await r.json(); } catch (e) { data = {}; }
      if (data === null || typeof data !== 'object') data = { value: data };
      data.ok = r.ok;
      data.httpStatus = r.status;
      if (r.status === 401) data.message = 'Brak autoryzacji – odśwież stronę.';
      return data;
    } catch (e) {
      return { ok: false, message: 'Błąd połączenia: ' + e.message };
    }
  }

  function chip(status) {
    const [txt, kind] = STATUS[status] || [status, 'neutral'];
    return el('span', { class: 'badge ' + kind }, el('span', { class: 'dot' }), txt);
  }

  const fmtInt = (v) => (v === null || v === undefined ? '—' : nf(0).format(v));
  const tokens = (v) => (v >= 1e6 ? nf(2).format(v / 1e6) + ' mln' : v >= 1e3 ? nf(1).format(v / 1e3) + ' tys.' : fmtInt(v));
  const usd = (v) => (v === null || v === undefined ? '—' : nf(2).format(v) + ' USD');

  /* ------------------------------------------------------------------ montaż */

  function mount() {
    if (A.root) return A.root;
    const root = el('div', { class: 'ai stack' });
    A.els.hero = el('div', { class: 'card ai-hero' });
    A.els.tabs = el('div', { class: 'tabs', role: 'tablist', 'aria-label': 'Sekcje AI & Auto-Updates' });
    A.els.body = el('div', { class: 'stack' });
    root.append(A.els.hero, A.els.tabs, A.els.body);
    A.root = root;
    renderTabs();
    return root;
  }

  function renderTabs() {
    const tabs = [['overview', 'Przegląd i postęp'], ['proposals', 'Propozycje i historia zmian'], ['providers', 'Połączenia AI'], ['schedule', 'Harmonogram i limity'], ['settings', 'Repozytorium i wydania']];
    A.els.tabs.replaceChildren(...tabs.map(([id, label]) => {
      const b = el('button', { role: 'tab', 'aria-selected': String(A.tab === id), text: label });
      b.addEventListener('click', () => {
        if (A.cfgDirty && !window.confirm('Masz niezapisane zmiany ustawień. Porzucić je?')) return;
        A.tab = id; lsSet('apex-ai-tab', id); A.cfgDirty = false; A.cfg = null; renderTabs(); renderBody(true);
      });
      return b;
    }));
  }

  function start() {
    if (A.timer) return;
    refresh();
    A.timer = setInterval(refresh, 3000);
    A.evTimer = setInterval(pollEvents, 1500);
  }

  function stop() {
    clearInterval(A.timer); clearInterval(A.evTimer);
    A.timer = null; A.evTimer = null;
  }

  async function refresh() {
    try { A.module = await api('/api/ai-module'); } catch (e) { A.module = null; }
    if (A.module && A.module.running) {
      const st = await lab('state');
      if (st && !st.notRunning && st.config) A.state = st;
    } else A.state = null;
    renderHero();
    renderBody(false);
  }

  /* ------------------------------------------------------------------ nagłówek */

  function renderHero() {
    const m = A.module || {};
    const st = A.state;
    const running = !!m.running;
    const automation = st && st.config.automationEnabled;
    const job = st && st.running ? st.jobs.find((j) => j.id === st.running) : null;
    const left = el('div', null,
      el('div', { class: 'ai-title' }, 'APEX AI Lab'),
      el('div', { class: 'ai-desc', text: 'Automatyczna analiza skuteczności strategii i warunków rynkowych. Agenci AI (Codex lub Claude) przygotowują ulepszenia w izolowanej kopii, a AI Lab je weryfikuje: audyt, kompilacja, testy regresyjne i backtest. Wdrożenie wyłącznie po Twojej akceptacji.' }),
      el('div', { class: 'chips', style: 'margin-top:10px' },
        el('span', { class: 'badge ' + (running ? 'good' : 'critical') }, el('span', { class: 'dot' }), running ? 'Moduł AI Lab działa' + (m.version ? ' · ' + m.version : '') : 'Moduł AI Lab nie działa'),
        st ? el('span', { class: 'badge ' + (automation ? 'good' : 'neutral') }, el('span', { class: 'dot' }), automation ? 'Automatyzacja włączona' : 'Automatyzacja wyłączona') : null,
        st && automation && st.nextRunUtc ? el('span', { class: 'badge neutral' }, 'Następna analiza: ' + time(st.nextRunUtc)) : null,
        job ? el('span', { class: 'badge accent' }, el('span', { class: 'dot' }), 'Zadanie w toku: ' + (job.title || PROVIDER[job.provider] || 'analiza')) : null));
    const actions = el('div', { class: 'controls ai-hero-actions' });
    if (!running) {
      actions.append(el('button', { class: 'btn primary', disabled: !m.installed || A.starting, onclick: startModule }, icon('play'), A.starting ? 'Uruchamianie…' : 'Uruchom moduł AI'));
      if (!m.installed) actions.append(el('span', { class: 'muted', text: 'Nie znaleziono ApexAiLab.exe – zbuduj pakiet skryptem Zbuduj-APEX.ps1.' }));
    } else if (st) {
      actions.append(
        el('button', { class: 'btn', disabled: !!st.running, onclick: () => runNow('analysis') }, icon('refresh'), 'Analizuj teraz'),
        el('button', { class: 'btn primary', disabled: !!st.running, onclick: () => runNow('agent') }, icon('play'), 'Uruchom agenta teraz'),
        switchCtl('Automatyzacja', automation, async (v) => {
          const r = await lab('config', { automationEnabled: v });
          toast(r.message || 'Zapisano.');
          refresh();
        }));
    }
    A.els.hero.replaceChildren(el('div', { class: 'ai-hero-row' }, left, actions),
      el('div', { class: 'callout good ai-safety' }, icon('shield'), el('div', null,
        el('strong', { text: 'Bezpieczeństwo: ' }),
        'zmiany AI nigdy nie trafiają do działającego bota automatycznie. Zatwierdzenie buduje nowe wydanie, aktywne dopiero po ponownym uruchomieniu APEX. RiskEngine, limity bezpieczeństwa i aktywne strategie wymagają dodatkowego, wpisanego potwierdzenia. Konto REAL pozostaje zablokowane – audyt odrzuca każdą zmianę w logice dopuszczenia LIVE.')));
  }

  async function startModule() {
    A.starting = true; renderHero();
    try { const r = await api('/api/ai-module/start', {}); toast(r.message); } catch (e) { toast('Błąd: ' + e.message); }
    setTimeout(() => { A.starting = false; refresh(); }, 4000);
  }

  async function runNow(mode) {
    const st = A.state;
    if (mode === 'agent') {
      const sel = el('select', { 'aria-label': 'Dostawca' }, el('option', { value: '', text: 'Według ustawień (główny → zapasowy)' }),
        ...PROVIDERS.map((p) => el('option', { value: p, text: PROVIDER[p] })));
      const wrap = el('div', { class: 'field', style: 'margin-top:10px' }, el('label', { text: 'Dostawca AI' }), sel);
      const ok = await confirmModal('Uruchomić agenta AI?', 'Agent przeanalizuje bieżący raport i może przygotować zmianę w izolowanej kopii repozytorium. Zużyje limit subskrypcji lub tokeny API. Wynik trafi do „Propozycji” – nic nie zostanie wdrożone bez Twojej decyzji.', 'Uruchom agenta', false, wrap);
      if (ok === null) return;
      const r = await lab('run', { mode: 'agent', provider: sel.value || null });
      toast(r.message || 'Uruchomiono.');
      if (r.id) { A.selected = r.id; A.tab = 'overview'; renderTabs(); }
    } else {
      const r = await lab('run', { mode: 'analysis' });
      toast(r.message || 'Uruchomiono.');
      if (r.id) A.selected = r.id;
    }
    if (st) refresh();
  }

  function switchCtl(label, checked, onChange, disabled) {
    const input = el('input', { type: 'checkbox', checked: !!checked, disabled: !!disabled });
    input.addEventListener('change', () => onChange(input.checked));
    return el('label', { class: 'switch' }, input, el('span', { class: 'track' }), el('span', { text: label }));
  }

  /* ------------------------------------------------------------------ treść */

  function renderBody(force) {
    if (!A.root) return;
    const body = A.els.body;
    if (!A.module || !A.module.running || !A.state) {
      body.replaceChildren(notRunningView());
      return;
    }
    const active = document.activeElement;
    const editing = active && body.contains(active) && ['INPUT', 'SELECT', 'TEXTAREA'].includes(active.tagName);
    if (!force && (editing || A.cfgDirty) && ['providers', 'schedule', 'settings'].includes(A.tab)) {
      // Formularz w edycji – odświeżamy tylko części tylko-do-odczytu.
      if (A.els.liveUsage) A.els.liveUsage.replaceChildren(...usageBlock());
      return;
    }
    // Przebudowa tylko przy zmianie danych – dziennik zdarzeń odświeża się osobno i nie traci przewinięcia.
    const st = A.state;
    const sig = JSON.stringify([A.tab, A.selected, A.filter, st.running, st.jobs.slice(0, 20).map((j) => [j.id, j.status, j.files, (j.stages || []).map((x) => x.status).join('')]),
      st.analysis && st.analysis.id, st.usage.tokensToday, st.usage.runsToday, (st.limits || []).length, st.config.automationEnabled, st.providersCheckedUtc,
      st.codexLogin && [st.codexLogin.running, st.codexLogin.code, st.codexLogin.url, st.codexLogin.result], A.detail && [A.detail.job.id, A.detail.job.status], st.releases && st.releases.current]);
    if (!force && sig === A.sig) return;
    A.sig = sig;
    const views = { overview: viewOverview, proposals: viewProposals, providers: viewProviders, schedule: viewSchedule, settings: viewSettings };
    const scroll = window.scrollY;
    body.replaceChildren(views[A.tab]());
    window.scrollTo(0, scroll);
  }

  function notRunningView() {
    const m = A.module || {};
    return el('div', { class: 'card' },
      el('h2', null, 'Moduł AI Lab nie działa'),
      el('p', { class: 'muted', text: 'AI Lab to osobny proces – jego zatrzymanie lub awaria nie wpływa na handel. Uruchom go przyciskiem powyżej. Wymagania: Git, .NET 8 SDK oraz Codex CLI (logowanie kontem ChatGPT) lub Claude Code, ewentualnie klucz OpenAI/Anthropic API.' }),
      m.exePath ? el('p', { class: 'note', text: 'Program: ' + m.exePath }) : null);
  }

  /* ------------------------------------------------------------------ Przegląd */

  function viewOverview() {
    const st = A.state;
    const wrap = el('div', { class: 'stack' });
    const runningId = st.running;
    if (!A.selected || (runningId && A.selected !== runningId && !A.pinned)) A.selected = runningId || (st.jobs[0] && st.jobs[0].id) || null;
    const job = st.jobs.find((j) => j.id === A.selected);

    // Postęp zadania (na żywo).
    const progress = el('div', { class: 'card' },
      el('h2', null, 'Postęp pracy agentów', el('span', { class: 'sub', text: job ? (runningId === job.id ? 'na żywo' : 'zadanie ' + job.id) : '' })));
    if (!job) progress.append(el('p', { class: 'empty', text: 'Brak zadań. Użyj „Analizuj teraz” lub „Uruchom agenta teraz”.' }));
    else {
      progress.append(el('div', { class: 'ai-jobhead' },
        el('div', null, el('div', { class: 'ai-jobtitle', text: job.title || (job.provider ? 'Zadanie agenta' : 'Analiza') }),
          el('div', { class: 'muted ai-jobmeta', text: `${TRIGGER[job.trigger] || job.trigger} · ${job.provider ? PROVIDER[job.provider] + (job.model ? ' (' + job.model + ')' : '') : 'bez agenta'} · start ${time(job.createdUtc)}` })),
        el('div', { class: 'controls' }, chip(job.status),
          runningId === job.id ? el('button', { class: 'btn small danger', onclick: () => cancelJob(job.id) }, icon('stop'), 'Przerwij') : null,
          job.files > 0 ? el('button', { class: 'btn small', onclick: () => openProposal(job.id) }, 'Szczegóły zmiany') : null)));
      progress.append(stepper(job.stages));
      if (!A.els.log) A.els.log = el('div', { class: 'ai-log', role: 'log', 'aria-live': 'polite', 'aria-label': 'Dziennik pracy agenta' });
      progress.append(A.els.log);
      if (A.eventsJob !== job.id) { A.eventsJob = job.id; A.events = []; A.lastEvent = 0; pollEvents(true); } else drawEvents();
      const others = st.jobs.slice(0, 8);
      progress.append(el('div', { class: 'ai-jobpick' }, el('span', { class: 'muted', text: 'Pokaż zadanie: ' }),
        ...others.map((j) => el('button', { class: 'btn small ' + (j.id === A.selected ? 'primary' : 'ghost'), onclick: () => { A.selected = j.id; A.pinned = true; renderBody(true); } }, shortTime(j.createdUtc)))));
    }
    wrap.append(progress);

    // Analiza + zużycie.
    const an = st.analysis;
    const analysisCard = el('div', { class: 'card' }, el('h2', null, 'Ostatnia analiza', el('span', { class: 'sub', text: an ? time(an.timeUtc) : 'brak' })));
    if (!an) analysisCard.append(el('p', { class: 'empty', text: 'Analiza jeszcze nie była wykonana.' }));
    else {
      if (an.dataNote) analysisCard.append(el('div', { class: 'callout' }, icon('info'), el('div', { text: an.dataNote })));
      const fl = el('div', { class: 'feed', style: 'margin-top:10px' });
      if (!an.findings.length) fl.append(el('div', { class: 'item', text: 'Brak istotnych problemów wykrytych automatycznie.' }));
      for (const f of an.findings) fl.append(el('div', { class: 'item' + (f.level === 'critical' ? ' err' : '') }, el('span', { class: 'badge ' + (f.level === 'critical' ? 'critical' : f.level === 'warning' ? 'warning' : 'neutral') }, el('span', { class: 'dot' }), f.level === 'critical' ? 'krytyczne' : f.level === 'warning' ? 'ostrzeżenie' : 'info'), ' ', el('strong', { text: f.title }), ' – ' + f.detail));
      analysisCard.append(fl);
      if (an.marketSummary && an.marketSummary.length) analysisCard.append(el('p', { class: 'note', text: an.marketSummary.join(' · ') }));
      if (an.strategies && an.strategies.length) analysisCard.append(strategyTable(an.strategies));
    }
    A.els.liveUsage = el('div', { class: 'stack' }, ...usageBlock());
    wrap.append(el('div', { class: 'grid cols-2' }, analysisCard, el('div', { class: 'card' }, el('h2', null, 'Zużycie limitów', el('span', { class: 'sub', text: 'dziś / tydzień / miesiąc' })), A.els.liveUsage)));

    // Ostatnie propozycje.
    const recent = st.jobs.filter((j) => j.files > 0).slice(0, 5);
    wrap.append(el('div', { class: 'card' }, el('h2', null, 'Ostatnie propozycje zmian', el('span', { class: 'sub' }, el('button', { class: 'btn small ghost', onclick: () => { A.tab = 'proposals'; lsSet('apex-ai-tab', 'proposals'); renderTabs(); renderBody(true); } }, 'Wszystkie →'))),
      recent.length ? proposalTable(recent) : el('p', { class: 'empty', text: 'Brak propozycji zmian.' })));
    return wrap;
  }

  function stepper(stages) {
    const map = {};
    for (const s of stages || []) map[s.stage] = s.status;
    return el('ol', { class: 'ai-steps' }, ...STAGES.map(([id, label]) => {
      const s = map[id] || 'Pending';
      const cls = { Ok: 'ok', Warning: 'warn', Failed: 'fail', Running: 'run', Skipped: 'skip', Pending: 'pend' }[s] || 'pend';
      const mark = { ok: '✓', warn: '!', fail: '✕', run: '', skip: '–', pend: '' }[cls];
      return el('li', { class: 'ai-step ' + cls, title: label + ': ' + s }, el('span', { class: 'ai-step-dot', text: mark }), el('span', { class: 'ai-step-label', text: label }));
    }));
  }

  async function pollEvents(reset) {
    if (!A.eventsJob || A.tab !== 'overview') return;
    const st = A.state;
    const live = st && st.running === A.eventsJob;
    if (!live && A.wasLive) { A.wasLive = false; reset = true; } // zadanie właśnie się zakończyło – pełny dziennik z dysku
    if (live) A.wasLive = true;
    if (!live && !reset && A.events.length) return;
    const r = await lab('jobs/' + encodeURIComponent(A.eventsJob) + '/events' + (live && A.lastEvent ? '?since=' + A.lastEvent : ''));
    if (!r || !r.events) return;
    if (!live || reset) A.events = r.events; else A.events.push(...r.events);
    if (A.events.length > 1500) A.events = A.events.slice(-1500);
    A.lastEvent = r.last || A.lastEvent;
    drawEvents();
  }

  function drawEvents() {
    const log = A.els.log;
    if (!log) return;
    const atBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 40;
    log.replaceChildren(...(A.events.length ? A.events.slice(-400).map((e) =>
      el('div', { class: 'ai-ev ' + e.kind },
        el('span', { class: 'ai-ev-t', text: shortTime(e.timeUtc) }),
        el('span', { class: 'ai-ev-i', 'aria-hidden': 'true', text: EVENT_ICON[e.kind] || '•' }),
        el('span', { class: 'ai-ev-x', text: e.text }))) : [el('div', { class: 'empty', text: 'Brak zdarzeń.' })]));
    // Przewinięcie po dołączeniu do dokumentu (przy pierwszym rysowaniu element nie ma jeszcze wysokości).
    if (atBottom || A.events.length < 30) requestAnimationFrame(() => { log.scrollTop = log.scrollHeight; });
  }

  async function cancelJob(id) {
    const ok = await confirmModal('Przerwać zadanie?', 'Agent zostanie zatrzymany. Kopia robocza i dotychczasowe wyniki zostaną zachowane do wglądu.', 'Przerwij', true);
    if (!ok) return;
    const r = await lab('jobs/' + encodeURIComponent(id) + '/cancel', {});
    toast(r.message);
  }

  function strategyTable(rows) {
    return el('div', { class: 'table-wrap', style: 'margin-top:10px' }, el('table', null,
      el('thead', null, el('tr', null, ...['Strategia', 'Klasa', 'n', 'E[R] na żywo', 'Backtest OOS', 'Wynik'].map((h, i) => el('th', { class: i >= 2 ? 'num' : '', text: h })))),
      el('tbody', null, ...rows.map((s) => el('tr', null, el('td', { text: s.strategy + (s.suspended ? ' (wstrzymana)' : '') }), el('td', { text: s.assetClass }),
        el('td', { class: 'num', text: fmtInt(s.trades) }), el('td', { class: 'num ' + cls(s.expectancyR), text: signed(s.expectancyR, 3) + 'R' }),
        el('td', { class: 'num', text: s.backtestExpectancyR === null || s.backtestExpectancyR === undefined ? '—' : signed(s.backtestExpectancyR, 3) + 'R' }),
        el('td', { class: 'num ' + cls(s.netProfit), text: signed(s.netProfit) }))))));
  }

  /* ------------------------------------------------------------------ zużycie */

  function usageBlock() {
    const st = A.state;
    const u = st.usage;
    const b = st.config.budget;
    const items = [
      meter('Uruchomienia agenta dziś', u.runsToday, b.maxAgentRunsPerDay, fmtInt),
      meter('Uruchomienia agenta w tym tygodniu', u.runsWeek, b.maxAgentRunsPerWeek, fmtInt),
      meter('Tokeny dziś', u.tokensToday, b.maxTokensPerDay, tokens),
      meter('Koszt API w tym miesiącu (USD)', Math.round(u.costMonthUsd * 100) / 100, b.maxUsdPerMonth, (v) => nf(2).format(v)),
    ];
    const per = Object.entries(u.tokensByProviderMonth || {});
    items.push(el('p', { class: 'note', text: 'W tym miesiącu: ' + tokens(u.tokensMonth) + ' tokenów' + (per.length ? ' (' + per.map(([k, v]) => (PROVIDER[k] || k) + ': ' + tokens(v)).join(', ') + ')' : '') + '. Koszt liczony tylko dla dostawców API z wpisanymi cenami; subskrypcje ChatGPT/Claude rozliczane są limitami planu.' }));
    for (const l of st.limits || []) items.push(el('div', { class: 'callout warning' }, icon('alert'), el('div', null, el('strong', { text: (PROVIDER[l.provider] || l.provider) + ': limit wyczerpany do ' + time(l.limitedUntilUtc) + '. ' }), l.message)));
    return items;
  }

  function usageChart(daily) {
    const W = 640, H = 200, P = { l: 52, r: 12, t: 12, b: 28 };
    const max = Math.max(1, ...daily.map((d) => d.tokens));
    const step = niceStep(max);
    const top = Math.ceil(max / step) * step;
    const box = el('div', { class: 'chart ai-chart' });
    const s = svg('svg', { viewBox: `0 0 ${W} ${H}`, role: 'img', 'aria-label': 'Tokeny dziennie – ostatnie 14 dni' });
    const iw = W - P.l - P.r, ih = H - P.t - P.b;
    for (let v = 0; v <= top + 1e-9; v += step) {
      const y = P.t + ih - (v / top) * ih;
      s.appendChild(svg('line', { x1: P.l, x2: W - P.r, y1: y, y2: y, class: v === 0 ? 'axis' : 'gridline' }));
      const t = svg('text', { x: P.l - 8, y: y + 4, 'text-anchor': 'end', class: 'tick' }); t.textContent = tokens(v); s.appendChild(t);
    }
    const bw = iw / daily.length;
    const tip = el('div', { class: 'tooltip' });
    daily.forEach((d, i) => {
      const h = (d.tokens / top) * ih;
      const x = P.l + i * bw + bw * 0.22, w = bw * 0.56, y = P.t + ih - h;
      if (h > 0) {
        const r = Math.min(4, w / 2, h);
        s.appendChild(svg('path', { class: 'bar', d: `M${x},${P.t + ih} V${y + r} Q${x},${y} ${x + r},${y} H${x + w - r} Q${x + w},${y} ${x + w},${y + r} V${P.t + ih} Z` }));
      }
      const day = new Date(d.day);
      if (i % 2 === 0 || daily.length <= 8) { const t = svg('text', { x: x + w / 2, y: H - 8, 'text-anchor': 'middle', class: 'tick' }); t.textContent = day.toLocaleDateString('pl-PL', { day: '2-digit', month: '2-digit' }); s.appendChild(t); }
      const hit = svg('rect', { x: P.l + i * bw, y: P.t, width: bw, height: ih, fill: 'transparent' });
      hit.addEventListener('mouseenter', () => {
        tip.replaceChildren(el('strong', { text: fmtInt(d.tokens) + ' tokenów' }), el('span', { text: day.toLocaleDateString('pl-PL') + ' · uruchomień agenta: ' + d.runs }));
        tip.style.left = ((x + w / 2) / W * 100) + '%'; tip.style.top = (y / H * 100) + '%'; tip.style.opacity = '1';
      });
      hit.addEventListener('mouseleave', () => { tip.style.opacity = '0'; });
      s.appendChild(hit);
    });
    box.append(s, tip);
    return box;
  }

  function niceStep(max) {
    const raw = max / 4;
    const mag = Math.pow(10, Math.floor(Math.log10(raw)));
    const n = raw / mag;
    return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
  }

  /* ------------------------------------------------------------------ Propozycje */

  function proposalTable(jobs) {
    return el('div', { class: 'table-wrap' }, el('table', null,
      el('thead', null, el('tr', null, ...['Data', 'Zmiana', 'Wyzwalacz', 'AI', 'Pliki', 'Testy', 'Backtest', 'Status'].map((h) => el('th', { text: h })))),
      el('tbody', null, ...jobs.map((j) => {
        const tr = el('tr', { class: 'ai-row' + (A.detail && A.detail.job.id === j.id ? ' sel' : ''), tabindex: '0' },
          el('td', { text: time(j.createdUtc) }),
          el('td', null, el('div', { class: 'ai-rowtitle', text: j.title || '—' }), j.protectedTouched ? el('span', { class: 'badge warning', style: 'margin-top:4px' }, icon('shield'), 'moduł chroniony') : null),
          el('td', { text: TRIGGER[j.trigger] || j.trigger }),
          el('td', { text: j.provider ? PROVIDER[j.provider] : '—' }),
          el('td', { class: 'num', text: j.files ? `${j.files} (+${j.added}/−${j.removed})` : '—' }),
          el('td', { class: 'num', text: j.tests ? `${j.tests.passed}/${j.tests.total}` : '—' }),
          el('td', { text: j.backtest || '—' }),
          el('td', null, chip(j.status)));
        const open = () => openProposal(j.id);
        tr.addEventListener('click', open);
        tr.addEventListener('keydown', (e) => { if (e.key === 'Enter') open(); });
        return tr;
      }))));
  }

  function viewProposals() {
    const st = A.state;
    const wrap = el('div', { class: 'stack' });
    const filterSel = el('select', { 'aria-label': 'Filtr' }, ...[['changes', 'Tylko propozycje zmian'], ['all', 'Wszystkie zadania'], ['ready', 'Czekające na decyzję']].map(([v, t]) => el('option', { value: v, text: t, selected: (A.filter || 'changes') === v })));
    filterSel.addEventListener('change', () => { A.filter = filterSel.value; renderBody(true); });
    const f = A.filter || 'changes';
    const jobs = st.jobs.filter((j) => f === 'all' || (f === 'ready' ? ['ReadyForApproval', 'NeedsAttention'].includes(j.status) : j.files > 0));
    wrap.append(el('div', { class: 'card' }, el('h2', null, 'Historia zmian', el('span', { class: 'sub' }, filterSel)),
      jobs.length ? proposalTable(jobs) : el('p', { class: 'empty', text: 'Brak pozycji.' })));
    A.els.detail = el('div', { class: 'stack' });
    wrap.append(A.els.detail);
    if (A.detail) drawDetail();
    return wrap;
  }

  async function openProposal(id) {
    if (A.tab !== 'proposals') { A.tab = 'proposals'; lsSet('apex-ai-tab', 'proposals'); renderTabs(); }
    const r = await lab('jobs/' + encodeURIComponent(id));
    if (!r || !r.job) { toast(r && r.message ? r.message : 'Nie można wczytać propozycji.'); return; }
    A.detail = r;
    renderBody(true);
    setTimeout(() => { if (A.els.detail) A.els.detail.scrollIntoView({ behavior: 'smooth', block: 'start' }); }, 50);
  }

  function drawDetail() {
    const d = A.detail;
    const j = d.job;
    const box = A.els.detail;
    const head = el('div', { class: 'card ai-detail' },
      el('div', { class: 'ai-jobhead' },
        el('div', null, el('div', { class: 'ai-jobtitle', text: j.title || 'Zadanie ' + j.id }),
          el('div', { class: 'muted ai-jobmeta', text: `${j.id} · ${TRIGGER[j.trigger] || j.trigger} · ${j.provider ? PROVIDER[j.provider] + (j.model ? ' (' + j.model + ')' : '') : '—'} · gałąź ${j.branch || '—'}` })),
        el('div', { class: 'controls' }, chip(j.status), el('button', { class: 'btn small ghost', onclick: () => { A.detail = null; renderBody(true); } }, icon('x'), 'Zamknij'))),
      el('p', { class: 'ai-statustext', text: j.statusText }),
      stepper(j.stages),
      decisionBar(d));
    box.replaceChildren(head);

    const kv = el('div', { class: 'ai-kv' },
      kvItem('Pliki', j.files.length ? `${j.files.length} (+${sum(j.files, 'added')}/−${sum(j.files, 'removed')})` : '—'),
      kvItem('Kompilacja', j.build ? (j.build.ok ? 'OK' : 'błąd') : '—'),
      kvItem('Testy', j.tests ? `${j.tests.passed}/${j.tests.total}` + (j.tests.baselineTotal ? ` (baza ${j.tests.baselinePassed}/${j.tests.baselineTotal})` : '') : '—'),
      kvItem('Backtest', j.backtest ? (j.backtest.ran ? j.backtest.verdict : 'pominięty') : '—'),
      kvItem('Tokeny', tokens((j.usage.inputTokens || 0) + (j.usage.outputTokens || 0))),
      kvItem('Koszt', j.usage.costUsd === null || j.usage.costUsd === undefined ? 'subskrypcja / brak cen' : usd(j.usage.costUsd)));
    box.append(el('div', { class: 'card' }, el('h2', null, 'Weryfikacja AI Lab'), kv));

    if (j.agentSummary) box.append(el('div', { class: 'card' }, el('h2', null, 'Podsumowanie agenta'), el('pre', { class: 'ai-pre', text: j.agentSummary })));
    if (j.findings && j.findings.length) {
      const sev = { Blocking: ['blokujące', 'critical'], Protected: ['chronione', 'warning'], Warning: ['ostrzeżenie', 'serious'], Info: ['info', 'neutral'] };
      box.append(el('div', { class: 'card' }, el('h2', null, 'Audyt zmian', el('span', { class: 'sub', text: 'reguły bezpieczeństwa AI Lab' })),
        el('div', { class: 'table-wrap' }, el('table', null,
          el('thead', null, el('tr', null, ...['Waga', 'Reguła', 'Plik', 'Szczegóły'].map((h) => el('th', { text: h })))),
          el('tbody', null, ...j.findings.map((f) => el('tr', null,
            el('td', null, el('span', { class: 'badge ' + (sev[f.severity] || sev.Info)[1] }, el('span', { class: 'dot' }), (sev[f.severity] || sev.Info)[0])),
            el('td', { text: f.rule }), el('td', null, el('code', { text: f.file || '—' })), el('td', { text: f.detail }))))))));
    }
    if (j.backtest && j.backtest.ran) box.append(backtestCard(j.backtest));
    else if (j.backtest) box.append(el('div', { class: 'card' }, el('h2', null, 'Backtest'), el('div', { class: 'callout warning' }, icon('alert'), el('div', { text: 'Backtest pominięty: ' + j.backtest.reason }))));
    if (j.tests && j.tests.failedNames && j.tests.failedNames.length) box.append(el('div', { class: 'card' }, el('h2', null, 'Niezaliczone testy'), el('div', { class: 'feed' }, ...j.tests.failedNames.map((n) => el('div', { class: 'item err', text: n })))));
    if (j.build && !j.build.ok) box.append(el('div', { class: 'card' }, el('h2', null, 'Błędy kompilacji'), el('div', { class: 'feed' }, ...j.build.errors.map((n) => el('div', { class: 'item err', text: n })))));
    if (j.review) box.append(el('div', { class: 'card' }, el('h2', null, 'Niezależny przegląd', el('span', { class: 'sub', text: j.reviewProvider ? PROVIDER[j.reviewProvider] : '' })), el('pre', { class: 'ai-pre', text: j.review })));
    if (j.deployment) box.append(el('div', { class: 'card' }, el('h2', null, 'Wdrożenie', el('span', { class: 'sub', text: j.deployment.version })), ...j.deployment.notes.map((n) => el('div', { class: 'callout good', style: 'margin-top:8px' }, icon('info'), el('div', { text: n })))));
    if (d.diff) box.append(diffCard(d.diff, d.diffTruncated, j.files));
    if (d.analysisMarkdown) box.append(el('details', { class: 'card ai-collapse' }, el('summary', { text: 'Raport analizy przekazany agentowi' }), el('pre', { class: 'ai-pre', text: d.analysisMarkdown })));
  }

  function decisionBar(d) {
    const j = d.job;
    const bar = el('div', { class: 'controls ai-decide' });
    const canDecide = ['ReadyForApproval', 'NeedsAttention'].includes(j.status);
    if (canDecide) {
      bar.append(el('button', { class: 'btn primary', onclick: () => approve(d) }, icon('check'), 'Zatwierdź i przygotuj wdrożenie'),
        el('button', { class: 'btn danger', onclick: () => reject(j) }, icon('x'), 'Odrzuć'));
    }
    if (j.headCommit) {
      bar.append(el('button', { class: 'btn', onclick: async () => { const r = await lab('jobs/' + encodeURIComponent(j.id) + '/export', {}); toast(r.message); } }, icon('copy'), 'Eksportuj łatkę'),
        el('button', { class: 'btn ghost', onclick: () => pushSource(j) }, 'Dodaj gałąź do repozytorium źródłowego'));
    }
    if (j.status === 'Running') bar.append(el('button', { class: 'btn danger', onclick: () => cancelJob(j.id) }, icon('stop'), 'Przerwij'));
    if (canDecide && d.requiredPhrase) bar.append(el('span', { class: 'badge warning' }, icon('shield'), 'Wymaga wpisania frazy potwierdzenia'));
    return bar;
  }

  async function approve(d) {
    const j = d.job;
    let extra = null;
    const note = el('input', { type: 'text', placeholder: 'Notatka (opcjonalnie)', 'aria-label': 'Notatka' });
    let phraseInput = null;
    if (d.requiredPhrase) {
      phraseInput = el('input', { type: 'text', placeholder: d.requiredPhrase, 'aria-label': 'Fraza potwierdzenia', autocomplete: 'off' });
      extra = el('div', { class: 'stack', style: 'margin-top:10px;gap:8px' },
        el('div', { class: 'callout warning' }, icon('shield'), el('div', { text: j.protectedTouched
          ? 'Zmiana dotyczy modułów chronionych (RiskEngine, limity, bezpieczeństwo, wykonanie lub aktywne strategie). Wpisz: ' + d.requiredPhrase
          : 'Weryfikacja jest niepełna (np. bez backtestu). Wpisz: ' + d.requiredPhrase })),
        phraseInput, note);
    } else extra = el('div', { style: 'margin-top:10px' }, note);
    const holder = { value: true };
    extra.value = holder;
    const ok = await confirmModal('Zatwierdzić zmianę?', 'AI Lab połączy zmianę z gałęzią wdrożeń i zbuduje NOWE wydanie APEX Desktop (kilka minut). Działający bot nie zostanie zmieniony – nowa wersja zacznie działać po ponownym uruchomieniu APEX przez Ciebie. Poprzednią wersję można przywrócić jednym kliknięciem.', 'Zatwierdź', false, extra);
    if (ok === null) return;
    toast('Budowanie wydania… to może potrwać kilka minut.');
    const r = await lab('jobs/' + encodeURIComponent(j.id) + '/approve', { confirmation: phraseInput ? phraseInput.value : '', note: note.value });
    toast(r.message || 'Gotowe.');
    openProposal(j.id);
  }

  async function reject(j) {
    const note = el('input', { type: 'text', placeholder: 'Powód (opcjonalnie)', 'aria-label': 'Powód odrzucenia' });
    const extra = el('div', { style: 'margin-top:10px' }, note);
    extra.value = true;
    const ok = await confirmModal('Odrzucić propozycję?', 'Zmiana nie zostanie wdrożona. Gałąź z kodem zostanie zachowana do wglądu.', 'Odrzuć', true, extra);
    if (ok === null) return;
    const r = await lab('jobs/' + encodeURIComponent(j.id) + '/reject', { note: note.value });
    toast(r.message);
    openProposal(j.id);
  }

  async function pushSource(j) {
    const ok = await confirmModal('Dodać gałąź do repozytorium źródłowego?', `Gałąź ai/${j.id} zostanie dodana do Twojego repozytorium źródłowego. Bieżąca gałąź i pliki robocze nie zostaną zmienione.`, 'Dodaj gałąź', false);
    if (!ok) return;
    const r = await lab('jobs/' + encodeURIComponent(j.id) + '/push-source', {});
    toast(r.message);
  }

  function kvItem(k, v) { return el('div', null, el('div', { class: 'k', text: k }), el('div', { class: 'v', text: v })); }
  function sum(list, key) { return list.reduce((a, x) => a + (x[key] || 0), 0); }

  function backtestCard(b) {
    const rows = [];
    for (const period of ['research', 'holdout']) {
      const base = b.baseline.find((x) => x.period === period && x.minScore === 50);
      const cand = b.candidate.find((x) => x.period === period && x.minScore === 50);
      if (!base || !cand) continue;
      const name = period === 'research' ? 'Badawczy 2005–2017' : 'Odłożony 2018–2020.05';
      for (const [label, k, d, suffix] of [['Transakcje', 'trades', 0, ''], ['E[R]', 'expectancyR', 3, 'R'], ['Profit factor', 'profitFactor', 2, ''], ['Maks. obsunięcie', 'maxEquityDrawdownPct', 2, '%'], ['Wynik netto', 'netProfit', 2, '']]) {
        const dv = cand[k] - base[k];
        rows.push(el('tr', null, el('td', { text: name }), el('td', { text: label }), el('td', { class: 'num', text: nf(d).format(base[k]) + suffix }), el('td', { class: 'num', text: nf(d).format(cand[k]) + suffix }),
          el('td', { class: 'num ' + (k === 'maxEquityDrawdownPct' ? cls(-dv) : cls(dv)), text: (dv > 0 ? '+' : '') + nf(d).format(dv) + suffix })));
      }
    }
    const verdictKind = { lepszy: 'good', gorszy: 'critical', 'bez zmian': 'neutral', niejednoznaczny: 'warning' }[b.verdict] || 'neutral';
    return el('div', { class: 'card' }, el('h2', null, 'Backtest: wersja bazowa vs propozycja', el('span', { class: 'sub' }, el('span', { class: 'badge ' + verdictKind }, el('span', { class: 'dot' }), b.verdict))),
      el('div', { class: 'table-wrap' }, el('table', null,
        el('thead', null, el('tr', null, ...['Okres', 'Miara', 'Obecna wersja', 'Propozycja', 'Zmiana'].map((h, i) => el('th', { class: i >= 2 ? 'num' : '', text: h })))),
        el('tbody', null, ...rows))),
      ...b.notes.map((n) => el('p', { class: 'note', text: n })),
      el('p', { class: 'note', text: 'Ocena przy minimalnej ocenie okazji 50 (jak w handlu), te same koszty i ten sam silnik co na żywo. Ranking 0–100 nie jest prawdopodobieństwem zysku.' }));
  }

  function diffCard(diff, truncated, files) {
    const parts = [];
    let cur = null;
    for (const line of diff.split('\n')) {
      if (line.startsWith('diff --git ')) { cur = { name: line.replace(/^diff --git a\/(.+?) b\/.+$/, '$1'), lines: [] }; parts.push(cur); continue; }
      if (cur) cur.lines.push(line);
    }
    const card = el('div', { class: 'card' }, el('h2', null, 'Zmiany w kodzie', el('span', { class: 'sub', text: parts.length + ' plików' + (truncated ? ' (podgląd skrócony)' : '') })));
    for (const p of parts) {
      const meta = (files || []).find((f) => f.path === p.name) || {};
      const det = el('details', { class: 'ai-diff' }, el('summary', null, el('code', { text: p.name }), el('span', { class: 'pos', text: ' +' + (meta.added || 0) }), el('span', { class: 'neg', text: ' −' + (meta.removed || 0) }), meta.protected ? el('span', { class: 'badge warning', style: 'margin-left:8px' }, icon('shield'), 'chroniony') : null));
      det.addEventListener('toggle', () => {
        if (!det.open || det.dataset.drawn) return;
        det.dataset.drawn = '1';
        const pre = el('pre', { class: 'ai-code' });
        for (const l of p.lines) {
          const c = l.startsWith('+') && !l.startsWith('+++') ? 'add' : l.startsWith('-') && !l.startsWith('---') ? 'del' : l.startsWith('@@') ? 'hunk' : l.startsWith('+++') || l.startsWith('---') || l.startsWith('index ') || l.startsWith('new file') ? 'meta' : '';
          pre.appendChild(el('span', { class: 'ln ' + c, text: l + '\n' }));
        }
        det.appendChild(pre);
      });
      card.appendChild(det);
    }
    return card;
  }

  /* ------------------------------------------------------------------ Połączenia AI */

  function cfgCopy() {
    if (!A.cfg) A.cfg = JSON.parse(JSON.stringify(A.state.config));
    return A.cfg;
  }

  function markDirty() { A.cfgDirty = true; if (A.els.saveBar) A.els.saveBar.classList.add('dirty'); }

  function field(label, input, hint) {
    input.addEventListener('input', markDirty);
    input.addEventListener('change', markDirty);
    return el('div', { class: 'field' }, el('label', { text: label }), input, hint ? el('div', { class: 'hint', text: hint }) : null);
  }

  function num(obj, key, attrs) {
    const i = el('input', Object.assign({ type: 'number', value: String(obj[key] ?? 0) }, attrs || {}));
    i.addEventListener('input', () => { obj[key] = i.value === '' ? 0 : Number(i.value); });
    return i;
  }

  function txt(obj, key, attrs) {
    const i = el('input', Object.assign({ type: 'text', value: obj[key] || '' }, attrs || {}));
    i.addEventListener('input', () => { obj[key] = i.value; });
    return i;
  }

  function check(obj, key, label) {
    const i = el('input', { type: 'checkbox', checked: !!obj[key] });
    i.addEventListener('change', () => { obj[key] = i.checked; markDirty(); });
    return el('label', { class: 'switch' }, i, el('span', { class: 'track' }), el('span', { text: label }));
  }

  function sel(obj, key, options) {
    const s = el('select', null, ...options.map(([v, t]) => el('option', { value: v, text: t, selected: String(obj[key] ?? '') === String(v) })));
    s.addEventListener('change', () => { obj[key] = s.value === '' ? null : s.value; });
    return s;
  }

  function saveBar(sections) {
    const btn = el('button', { class: 'btn primary', onclick: async () => {
      const c = A.cfg;
      if (!c) return;
      const body = {};
      for (const s of sections) body[s] = c[s];
      const r = await lab('config', body);
      if (r && r.message) toast(r.message);
      if (r && r.ok) { A.cfgDirty = false; A.cfg = null; await refresh(); renderBody(true); }
    } }, icon('check'), 'Zapisz ustawienia');
    const reset = el('button', { class: 'btn ghost', onclick: () => { A.cfgDirty = false; A.cfg = null; renderBody(true); } }, 'Cofnij zmiany');
    A.els.saveBar = el('div', { class: 'controls ai-savebar' }, btn, reset, el('span', { class: 'muted', text: 'Zmiany zostaną zapisane w AI Lab (nie wpływają na działający silnik).' }));
    return A.els.saveBar;
  }

  function viewProviders() {
    const st = A.state;
    const c = cfgCopy();
    const wrap = el('div', { class: 'stack' });
    const pre = st.prerequisites || {};
    wrap.append(el('div', { class: 'card' }, el('h2', null, 'Wymagania', el('span', { class: 'sub', text: st.providersCheckedUtc ? 'sprawdzono ' + time(st.providersCheckedUtc) : '' })),
      el('div', { class: 'chips' }, preChip('Git', pre.git), preChip('.NET 8 SDK', pre.dotnet), preChip('Node.js (dla Codex CLI)', pre.node),
        el('button', { class: 'btn small', onclick: async () => { const r = await lab('providers/refresh', {}); toast(r.message); refresh(); } }, icon('refresh'), 'Sprawdź ponownie'))));

    const roles = el('div', { class: 'form' },
      field('Główny dostawca', sel(c, 'primaryProvider', PROVIDERS.map((p) => [p, PROVIDER[p]]))),
      field('Dostawca zapasowy (gdy limit wyczerpany)', sel(c, 'fallbackProvider', [['', 'brak'], ...PROVIDERS.map((p) => [p, PROVIDER[p]])])),
      field('Niezależny przegląd zmian (drugi model)', sel(c, 'reviewProvider', [['', 'wyłączony'], ...PROVIDERS.map((p) => [p, PROVIDER[p]])]), 'Drugi model ocenia każdą propozycję (zużywa dodatkowy limit).'));
    wrap.append(el('div', { class: 'card' }, el('h2', null, 'Role dostawców AI'), roles));

    const grid = el('div', { class: 'grid cols-2' });
    for (const id of PROVIDERS) grid.append(providerCard(id, c, st));
    wrap.append(grid, saveBar(['primaryProvider', 'fallbackProvider', 'reviewProvider', 'providers']));
    return wrap;
  }

  function preChip(name, p) {
    const ok = p && p.found;
    return el('span', { class: 'badge ' + (ok ? 'good' : 'critical'), title: p && p.version ? p.version : '' }, el('span', { class: 'dot' }), name + (ok ? '' : ' – brak'));
  }

  function providerCard(id, c, st) {
    const p = c.providers[id];
    const status = (st.providers || []).find((x) => x.id === id) || {};
    const sugg = (st.modelSuggestions || {})[id] || [];
    const listId = 'ai-models-' + id;
    const modelInput = txt(p, 'model', { list: listId, placeholder: id === 'Codex' || id === 'ClaudeCode' ? 'domyślny model narzędzia' : 'identyfikator modelu' });
    const card = el('div', { class: 'card ai-provider' },
      el('div', { class: 'ai-prov-head' },
        el('div', null, el('div', { class: 'ai-prov-name', text: PROVIDER[id] }), el('div', { class: 'muted', text: providerTagline(id) })),
        check(p, 'enabled', 'Włączony')),
      el('div', { class: 'chips' },
        el('span', { class: 'badge ' + (status.installed ? 'good' : 'critical') }, el('span', { class: 'dot' }), status.installed ? (status.version ? 'zainstalowany · ' + status.version : 'dostępny') : 'nie zainstalowano'),
        el('span', { class: 'badge ' + (status.loggedIn ? 'good' : 'warning') }, el('span', { class: 'dot' }), status.loggedIn ? status.authMode : (id === 'OpenAiApi' || id === 'AnthropicApi' ? 'brak klucza' : 'niezalogowany')),
        status.keyMasked ? el('span', { class: 'badge neutral', text: 'klucz ' + status.keyMasked }) : null),
      status.detail ? el('p', { class: 'note', text: status.detail }) : null,
      el('datalist', { id: listId }, ...sugg.filter((s) => s).map((s) => el('option', { value: s }))));

    const actions = el('div', { class: 'controls', style: 'margin:10px 0' });
    if (id === 'Codex') {
      actions.append(
        el('button', { class: 'btn primary small', disabled: !status.installed, onclick: () => login(id, 'browser') }, 'Zaloguj kontem ChatGPT'),
        el('button', { class: 'btn small', disabled: !status.installed, onclick: () => login(id, 'device') }, 'Logowanie kodem urządzenia'),
        el('button', { class: 'btn small ghost', disabled: !status.loggedIn, onclick: () => logout(id) }, 'Wyloguj'));
      if (!status.installed) card.append(installHint('npm install -g @openai/codex', 'Wymaga Node.js (winget install OpenJS.NodeJS.LTS). Po instalacji kliknij „Sprawdź ponownie”.'));
      const lg = st.codexLogin;
      if (lg && (lg.running || lg.url || lg.result)) {
        card.append(el('div', { class: 'callout' }, icon('info'), el('div', null,
          lg.url ? el('div', null, 'Otwórz: ', el('code', { text: lg.url })) : null,
          lg.code ? el('div', null, 'Kod: ', el('strong', { class: 'ai-code-big', text: lg.code })) : null,
          el('div', { class: 'muted', text: lg.running ? 'Czekam na potwierdzenie logowania w przeglądarce…' : lg.result }))));
      }
      card.append(actions, apiKeyRow(id, 'Klucz OpenAI API (opcjonalnie – zamiast subskrypcji ChatGPT)'));
    } else if (id === 'ClaudeCode') {
      actions.append(
        el('button', { class: 'btn primary small', disabled: !status.installed, onclick: () => login(id, 'claudeai') }, 'Zaloguj subskrypcją Claude'),
        el('button', { class: 'btn small', disabled: !status.installed, onclick: () => login(id, 'console') }, 'Zaloguj – Anthropic Console (API)'),
        el('button', { class: 'btn small ghost', disabled: !status.loggedIn, onclick: () => logout(id) }, 'Wyloguj'));
      if (!status.installed) card.append(installHint('npm install -g @anthropic-ai/claude-code', 'Alternatywnie instalator z claude.com/claude-code. Po instalacji kliknij „Sprawdź ponownie”.'));
      card.append(actions);
    } else {
      card.append(apiKeyRow(id, 'Klucz API'), el('div', { class: 'controls', style: 'margin:6px 0' }, el('button', { class: 'btn small ghost', disabled: !status.loggedIn, onclick: () => logout(id) }, 'Usuń klucz')));
    }
    const form = el('div', { class: 'form' },
      field('Model', modelInput, id === 'Codex' ? 'Puste = model domyślny Codex (zalecane). Dostępność modeli zależy od planu ChatGPT.' : id === 'ClaudeCode' ? 'Alias (opus, sonnet, fable) lub pełna nazwa; puste = domyślny.' : 'Wymagany dla API.'),
      field('Limit czasu zadania (min)', num(p, 'timeoutMinutes', { min: 5, max: 240 })));
    if (id === 'ClaudeCode') form.append(field('Limit kosztu na uruchomienie (USD, tylko rozliczenie API)', num(p, 'maxBudgetUsdPerRun', { min: 0, step: '0.5' }), '0 = bez limitu kwotowego (subskrypcja ma własne limity).'));
    form.append(
      field('Cena wejścia (USD / 1 mln tokenów)', num(p, 'priceInputPerMTok', { min: 0, step: '0.01' }), 'Wpisz cenę z cennika dostawcy – do kontroli kosztów.'),
      field('Cena wyjścia (USD / 1 mln tokenów)', num(p, 'priceOutputPerMTok', { min: 0, step: '0.01' })));
    if (id === 'OpenAiApi' || id === 'AnthropicApi') form.append(
      field('Maks. długość odpowiedzi (tokeny)', num(p, 'maxOutputTokens', { min: 1000, max: 128000, step: '1000' })),
      field('Rundy poprawek po błędach', num(p, 'maxRepairRounds', { min: 0, max: 3 }), 'Błędy kompilacji/testów wracają do modelu (dodatkowe tokeny).'));
    if (id === 'Codex' || id === 'ClaudeCode') form.append(field('Ścieżka programu (opcjonalnie)', txt(p, 'executablePath', { placeholder: 'wykrywana automatycznie' })));
    card.append(form);
    return card;
  }

  function providerTagline(id) {
    return {
      Codex: 'Agent Codex CLI – logowanie kontem ChatGPT (bez klucza API). Pracuje w piaskownicy z zapisem tylko do kopii roboczej.',
      ClaudeCode: 'Agent Claude Code – subskrypcja Claude lub Anthropic Console. Ograniczony zestaw narzędzi (pliki, kompilacja, testy).',
      OpenAiApi: 'OpenAI API (klucz) – tryb bezpieczny: model zwraca pliki, AI Lab je weryfikuje i zapisuje. Rozliczenie za tokeny.',
      AnthropicApi: 'Anthropic API (klucz) – tryb bezpieczny: model zwraca pliki, AI Lab je weryfikuje i zapisuje. Rozliczenie za tokeny.',
    }[id];
  }

  function installHint(cmd, note) {
    const code = el('code', { text: cmd });
    return el('div', { class: 'stack', style: 'gap:6px;margin:8px 0' },
      el('div', { class: 'copy-field' }, code, el('button', { class: 'btn small', onclick: () => { try { navigator.clipboard.writeText(cmd); toast('Skopiowano: ' + cmd); } catch (e) { toast('Skopiuj ręcznie: ' + cmd); } } }, icon('copy'), 'Kopiuj')),
      el('p', { class: 'note', text: note }));
  }

  function apiKeyRow(id, label) {
    const input = el('input', { type: 'password', placeholder: 'wklej klucz', autocomplete: 'off', 'aria-label': label });
    return el('div', { class: 'field', style: 'margin:8px 0' }, el('label', { text: label }),
      el('div', { class: 'copy-field' }, input, el('button', { class: 'btn small', onclick: async () => {
        if (!input.value.trim()) { toast('Wklej klucz.'); return; }
        const r = await lab('providers/' + id + '/apikey', { key: input.value.trim() });
        input.value = '';
        toast(r.message); refresh();
      } }, 'Zapisz klucz')),
      el('div', { class: 'hint', text: 'Klucz jest szyfrowany (Windows DPAPI, tylko Twoje konto) i nigdy nie jest wyświetlany w całości.' }));
  }

  async function login(id, mode) {
    const r = await lab('providers/' + id + '/login', { mode });
    toast(r.message);
    setTimeout(refresh, 1500);
  }

  async function logout(id) {
    const ok = await confirmModal('Wylogować?', 'Agent tego dostawcy nie będzie mógł pracować do ponownego zalogowania.', 'Wyloguj', true);
    if (!ok) return;
    const r = await lab('providers/' + id + '/logout', {});
    toast(r.message); refresh();
  }

  /* ------------------------------------------------------------------ Harmonogram i limity */

  function viewSchedule() {
    const st = A.state;
    const c = cfgCopy();
    const s = c.schedule;
    const b = c.budget;
    const wrap = el('div', { class: 'stack' });
    const sched = el('div', { class: 'form' },
      field('Częstotliwość analiz', sel(s, 'kind', [['Off', 'Wyłączone'], ['EveryHours', 'Co N godzin'], ['Daily', 'Codziennie'], ['Weekly', 'Co tydzień']])),
      field('Co ile godzin (dla „Co N godzin”)', num(s, 'everyHours', { min: 1, max: 336 })),
      field('Godzina (czas lokalny)', num(s, 'hour', { min: 0, max: 23 })),
      field('Minuta', num(s, 'minute', { min: 0, max: 59 })),
      field('Dzień tygodnia (dla „Co tydzień”)', sel(s, 'weeklyDay', DAYS)),
      field('Minimalny odstęp między automatycznymi uruchomieniami agenta (godz.)', num(s, 'minHoursBetweenAgentRuns', { min: 1, max: 720 })),
      field('Migawki warunków rynkowych co (min)', num(s, 'marketSnapshotMinutes', { min: 15, max: 1440 }), 'Lekki odczyt reżimów D1 z APEX Desktop – podstawa wykrywania zmian rynku.'));
    wrap.append(el('div', { class: 'card' }, el('h2', null, 'Harmonogram analiz', el('span', { class: 'sub', text: st.nextRunUtc ? 'następna: ' + time(st.nextRunUtc) : (st.config.automationEnabled ? '' : 'automatyzacja wyłączona') })),
      sched,
      el('div', { class: 'stack', style: 'margin-top:14px;gap:10px' },
        check(s, 'agentOnlyWithFindings', 'Uruchamiaj agenta z harmonogramu tylko, gdy analiza wykryje problem (oszczędza limity)'),
        check(s, 'triggerOnStrategyDegradation', 'Wyzwalacz: pogorszenie wyników strategii na żywo'),
        check(s, 'triggerOnMarketChange', 'Wyzwalacz: zmiana warunków rynkowych (reżim / zmienność)'),
        check(s, 'triggerOnExecutionErrors', 'Wyzwalacz: powtarzające się błędy wykonania')),
      el('p', { class: 'note', text: 'Wyzwalacze są sprawdzane co godzinę. Każdy uruchamia agenta najwyżej raz na „minimalny odstęp”. Wszystkie uruchomienia podlegają limitom poniżej.' })));

    const budget = el('div', { class: 'form' },
      field('Maks. uruchomień agenta dziennie', num(b, 'maxAgentRunsPerDay', { min: 0, max: 50 })),
      field('Maks. uruchomień agenta tygodniowo', num(b, 'maxAgentRunsPerWeek', { min: 0, max: 200 })),
      field('Maks. tokenów dziennie', num(b, 'maxTokensPerDay', { min: 0, step: '100000' }), 'Suma wszystkich dostawców; 0 = bez limitu.'),
      field('Maks. koszt API miesięcznie (USD)', num(b, 'maxUsdPerMonth', { min: 0, step: '1' }), 'Dotyczy OpenAI API i Anthropic API (wymaga wpisanych cen).'));
    A.els.liveUsage = el('div', { class: 'stack' }, ...usageBlock());
    wrap.append(el('div', { class: 'grid cols-2' },
      el('div', { class: 'card' }, el('h2', null, 'Limity zużycia'), budget, el('div', { style: 'margin-top:12px' }, check(b, 'pauseOnSubscriptionLimit', 'Po wyczerpaniu limitu subskrypcji czekaj na odnowienie (zalecane)'))),
      el('div', { class: 'card' }, el('h2', null, 'Bieżące zużycie'), A.els.liveUsage)));
    wrap.append(el('div', { class: 'card' }, el('h2', null, 'Tokeny dziennie', el('span', { class: 'sub', text: 'ostatnie 14 dni, wszyscy dostawcy' })), usageChart(st.usage.daily || [])));
    wrap.append(saveBar(['schedule', 'budget']));
    return wrap;
  }

  /* ------------------------------------------------------------------ Repozytorium i wydania */

  function viewSettings() {
    const st = A.state;
    const c = cfgCopy();
    const r = c.repo;
    const wrap = el('div', { class: 'stack' });
    wrap.append(el('div', { class: 'card' }, el('h2', null, 'Izolowane środowisko pracy'),
      el('div', { class: 'form' },
        field('Repozytorium źródłowe APEX (folder z .git)', txt(r, 'sourceRepoPath', { placeholder: 'np. C:\\APEX\\zrodla' }), 'AI Lab pracuje na własnej kopii; Twoje repozytorium nie jest zmieniane.'),
        field('Gałąź bazowa', txt(r, 'baseBranch', { placeholder: 'bieżąca gałąź repozytorium' })),
        field('Dane do backtestu (folder z plikami *_H1.csv)', txt(r, 'backtestDataDir', { placeholder: 'np. C:\\APEX\\zrodla\\data\\bars' }), 'Bez danych propozycje dostaną status „wymaga uwagi”.'),
        field('Limit czasu kompilacji (min)', num(r, 'buildTimeoutMinutes', { min: 1, max: 120 })),
        field('Limit czasu testów (min)', num(r, 'testTimeoutMinutes', { min: 1, max: 120 })),
        field('Limit czasu backtestu (min)', num(r, 'backtestTimeoutMinutes', { min: 1, max: 240 })),
        field('Zachowywane kopie robocze', num(r, 'keepWorktrees', { min: 1, max: 20 }))),
      el('div', { class: 'stack', style: 'margin-top:12px;gap:10px' },
        check(r, 'runBacktests', 'Wykonuj backtest portfela dla każdej propozycji'),
        check(c, 'autostart', 'Uruchamiaj AI Lab razem z APEX Desktop')),
      el('p', { class: 'note', text: `Okres odłożony (2018–2020.05) był użyty do oceny zmian ${st.holdoutUses} raz(y). Każde kolejne użycie zmniejsza jego wiarygodność – lepszy backtest nie dowodzi przyszłej skuteczności.` })));
    wrap.append(saveBar(['repo', 'autostart']));

    const pol = st.config.policy;
    wrap.append(el('div', { class: 'card' }, el('h2', null, 'Zasady bezpieczeństwa AI', el('span', { class: 'sub', text: 'zmiana tylko w pliku konfiguracji' })),
      el('div', { class: 'grid cols-2' },
        el('div', null, el('div', { class: 'muted', text: 'Moduły chronione (wdrożenie wymaga frazy „' + st.phrases.protectedChanges + '”):' }),
          el('ul', { class: 'ai-paths' }, ...pol.protectedPaths.map((p) => el('li', null, el('code', { text: p }))), el('li', { text: '+ pliki strategii aktywnych w profilu' }))),
        el('div', null, el('div', { class: 'muted', text: 'Zabronione dla AI (zmiana zawsze odrzucana):' }),
          el('ul', { class: 'ai-paths' }, ...pol.forbiddenPaths.map((p) => el('li', null, el('code', { text: p })))),
          el('div', { class: 'muted', style: 'margin-top:8px', text: 'Zawsze blokowane: logika dopuszczenia konta LIVE, kryptowaluty, martingale, grid, uśrednianie, wyłączanie lub usuwanie testów, sieć i procesy w silniku, AccessRights.FullAccess.' })))));

    const rel = st.releases || { list: [] };
    const curr = rel.list.find((x) => x.version === rel.current);
    wrap.append(el('div', { class: 'card' }, el('h2', null, 'Wydania', el('span', { class: 'sub', text: curr ? 'aktywne po restarcie: ' + curr.version : 'aktywna instalacja pierwotna' })),
      rel.list.length ? el('div', { class: 'table-wrap' }, el('table', null,
        el('thead', null, el('tr', null, ...['Wersja', 'Zmiana', 'Utworzono', 'Folder'].map((h) => el('th', { text: h })))),
        el('tbody', null, ...rel.list.map((x) => el('tr', null, el('td', null, x.version === rel.current ? el('strong', { text: x.version + ' ✓' }) : x.version),
          el('td', { text: x.title }), el('td', { text: time(x.createdUtc) }), el('td', null, el('code', { text: x.path }))))))) : el('p', { class: 'empty', text: 'Brak wydań przygotowanych przez AI Lab.' }),
      el('div', { class: 'controls', style: 'margin-top:12px' },
        el('button', { class: 'btn danger', disabled: !rel.current, onclick: rollback }, icon('refresh'), 'Przywróć poprzednią wersję')),
      el('p', { class: 'note', text: 'Skrót „Uruchom APEX.cmd” uruchamia aktywne wydanie. Po zatwierdzeniu lub wycofaniu zamknij APEX Desktop i uruchom go ponownie skrótem – wcześniej nic się nie zmienia.' })));
    return wrap;
  }

  async function rollback() {
    const ok = await confirmModal('Przywrócić poprzednią wersję?', 'Aktywne stanie się poprzednie wydanie (lub instalacja pierwotna). Zmiana zacznie działać po ponownym uruchomieniu APEX.', 'Przywróć', true);
    if (!ok) return;
    const r = await lab('releases/rollback', {});
    toast(r.message); refresh();
  }

  window.ApexAI = { mount, start, stop };
})();
