'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { parseHTML } = require('linkedom');
const root = path.resolve(__dirname, '../..');
const www = path.join(root, 'src/Apex.Desktop/wwwroot');

function panel(emptyToken = false) {
  const html = fs.readFileSync(path.join(www, 'index.html'), 'utf8');
  const { window, document } = parseHTML(emptyToken ? html.replace('__APEX_TOKEN__', '') : html);
  const calls = [];
  const storage = new Map();
  const timers = [];
  window.matchMedia = () => ({ matches: false });
  window.scrollTo = () => {};
  const context = vm.createContext({
    document, window, console, Intl, Date, URL, Blob, AbortController,
    localStorage: { getItem: k => storage.get(k) || null, setItem: (k, v) => storage.set(k, v) },
    setInterval: () => 1, clearInterval: () => {}, setTimeout: (fn, delay) => { timers.push({fn, delay}); return timers.length; }, clearTimeout: () => {},
    fetch: async (url, opts) => {
      calls.push({ url, opts });
      return { ok: true, status: 200, json: async () => ({ host: { version: 'test' }, data: { accounts: [] } }) };
    },
  });
  vm.runInContext(fs.readFileSync(path.join(www, 'app.js'), 'utf8'), context);
  return { document, context, calls, timers, run: source => vm.runInContext(source, context) };
}

const accountSource = `({ key: 'cTrader:Pepperstone:123', connected: true, platform: 'cTrader',
  engine: { isLive: false, guardAllowed: true, mode: 'Stopped', disabledStrategies: [], disabledClasses: [],
    openPositions: [], recentSignals: [], recentErrors: [], universeByClass: {}, equity: 10000, currency: 'USD' },
  equity: [], profile: [], executionTest: [] })`;

function selectAccount(p, overrides = '') {
  p.run(`S.data = {host: {version:'test',news:{enabled:false}}, data:{accounts:[${accountSource}]}};
    S.host = S.data.host; S.account = S.data.data.accounts[0].key; S.lastOk = Date.now();
    ${overrides}`);
}

test('empty enabled-mode meta keeps legacy JSON requests without injecting a credential (VM only)', async () => {
  const p = panel(true);
  await new Promise(setImmediate);
  await p.run(`api('/api/status')`);
  await p.run(`api('/api/accounts/x/command', {action:'emergency'})`);
  const get = p.calls.find(c => c.url === '/api/status');
  const post = p.calls.find(c => c.opts?.body && JSON.parse(c.opts.body).action === 'emergency');
  assert.ok(get); assert.ok(post);
  for (const call of [get, post]) {
    assert.equal(call.opts.headers['X-Apex-Token'], '');
    assert.equal(call.opts.headers.Authorization, undefined);
    assert.notEqual(call.opts.credentials, 'omit');
  }
  assert.equal(post.opts.headers['Content-Type'], 'application/json');
  assert.equal(post.opts.headers['X-Apex-Local'], '1'); // nonsecret anti-CSRF/preflight proof
  // Native Basic challenge/cache/network/CSP are intentionally not claimed by this VM test.
});

test('API HTTP error is an error, not a successful command', async () => {
  const p = panel();
  p.context.fetch = async () => ({ ok: false, status: 504, json: async () => ({ message: 'Polecenie anulowane.' }) });
  await assert.rejects(p.run(`api('/api/accounts/x/command', {action:'start'})`), /504|anulowane/);
});

test('hung status request aborts and releases polling for recovery', async () => {
  const p = panel();
  await new Promise(setImmediate);
  p.timers.length = 0;
  p.context.fetch = (_url, opts) => new Promise((_resolve, reject) => {
    opts.signal?.addEventListener('abort', () => reject(new Error('timeout')));
  });
  const pending = p.run('poll()');
  const timer = p.timers.find(t => t.delay === 5000);
  assert.ok(timer, 'status GET must have a five-second deadline');
  timer.fn();
  await pending;
  assert.equal(p.run('S.polling'), false);
  assert.equal(p.run('S.lastOk'), 0);
});

test('risk panel offers no path to unlock REAL and explains DEMO-only', () => {
  const p = panel();
  selectAccount(p, `S.data.data.accounts[0].engine.isLive = true; S.data.data.accounts[0].liveFeature = true;`);
  p.document.body.appendChild(p.run('viewRisk()'));
  assert.doesNotMatch(p.document.body.textContent, /Odblokuj konto LIVE|Odblokować handel LIVE|LiveTradingFeature/);
  assert.match(p.document.body.textContent, /wyłącznie.*DEMO|tylko.*DEMO/i);
});

test('disconnected account cannot start automation or execution test', () => {
  const p = panel();
  selectAccount(p, `S.data.data.accounts[0].connected = false;`);
  const dashboard = p.run('viewDashboard()');
  const start = [...dashboard.querySelectorAll('button')].find(b => b.textContent === 'START');
  assert.ok(start.hasAttribute('disabled'));
  const diagnostics = p.run('viewDiagnostics()');
  const execution = [...diagnostics.querySelectorAll('button')].find(b => b.textContent.includes('Test wykonania'));
  assert.ok(execution.hasAttribute('disabled'));
});

test('unknown account is not labelled as a verified DEMO account', () => {
  const p = panel();
  assert.doesNotMatch(p.run('envBadge(null)').textContent, /^DEMO$/);
});

test('command refuses new trading actions on stale panel or REAL account', async () => {
  const p = panel();
  selectAccount(p, 'S.lastOk = 0;');
  const before = p.calls.length;
  await p.run(`command('start')`);
  assert.equal(p.calls.length, before);
  selectAccount(p, 'S.data.data.accounts[0].engine.isLive = true;');
  await p.run(`command('executionTest')`);
  assert.equal(p.calls.length, before);
});

test('START requires explicit DEMO confirmation', () => {
  const p = panel();
  selectAccount(p);
  const start = [...p.run('viewDashboard()').querySelectorAll('button')].find(b => b.textContent === 'START');
  start.click();
  const dialog = p.document.querySelector('[role="dialog"]');
  assert.ok(dialog, 'START cannot immediately send a trading command');
  assert.match(dialog.textContent, /DEMO/);
});

test('positions panel exposes broker IDs, SL and TP and distinguishes foreign positions', () => {
  const p = panel();
  selectAccount(p, `S.host.label='APEX'; S.data.data.accounts[0].positions=[
    {id:456,symbol:'US500',side:'buy',units:0.1,entry:5000,price:5001,sl:4900,tp:5300,label:'APEX',net:0.1},
    {id:789,symbol:'EURUSD',side:'sell',units:1000,entry:1.1,price:1.09,sl:1.12,tp:1.05,label:'InnyBot',net:10}];`);
  const view = p.run('viewPositions()');
  assert.match(view.textContent, /456/);
  assert.match(view.textContent.replace(/\s/g, ''), /4900/);
  assert.match(view.textContent.replace(/\s/g, ''), /5300/);
  assert.match(view.textContent, /789/);
  assert.match(view.textContent, /pozycje obce|InnyBot/i);
});

test('scope panel separates app readiness from broker coverage and copies exact generated label', () => {
  const p = panel();
  selectAccount(p, `S.host.pairingKey='key'; S.host.wsUrl='ws://127.0.0.1:5077/bridge'; S.host.tcpPort=5078;
    S.data.data.accounts[0].scope={label:'A0123456789ABCDEF01',baselineId:'scope-id',status:'ScopedReady',eligibleTechnicalTest:true,strategyStartBlocked:true,brokerCoverageUnknown:true,pendingDeficits:['#42 C+O deficit'],nextAction:'Osobno zatwierdź test DEMO',policy:'Dedykowane DEMO hedged',brokerEpoch:'startup:3'};`);
  const view = p.run('viewConnections()');
  assert.match(view.textContent, /A0123456789ABCDEF01/);
  assert.match(view.textContent, /CoverageUnknown/);
  assert.match(view.textContent, /ScopedReady/);
  assert.match(view.textContent, /scope-id/);
  assert.match(view.textContent, /PASS/);
});

test('scope readiness and generic history cannot enable strategy START before Task2 PASS', async () => {
  const p = panel(); await new Promise(setImmediate);
  selectAccount(p, `S.data.data.accounts[0].engine.historyReady=true; S.data.data.accounts[0].scope={eligibleTechnicalTest:true,strategyStartBlocked:true};`);
  const start = [...p.run('viewDashboard()').querySelectorAll('button')].find(b => b.textContent === 'START');
  assert.ok(start.hasAttribute('disabled'));
  const before = p.calls.length; await p.run(`command('start')`); assert.equal(p.calls.length, before);
});

test('technical test confirmation sends explicit DEMO approval but scope setup never starts it', async () => {
  const p = panel(); await new Promise(setImmediate);
  selectAccount(p, `S.data.data.accounts[0].scope={eligibleTechnicalTest:true,strategyStartBlocked:true}; confirmModal=async()=>true;`);
  const execution = [...p.run('viewDiagnostics()').querySelectorAll('button')].find(b => b.textContent.includes('Test wykonania'));
  execution.click(); await new Promise(setImmediate);
  const command = p.calls.find(c => c.opts?.body && JSON.parse(c.opts.body).action === 'executionTest');
  assert.ok(command); assert.equal(JSON.parse(command.opts.body).args.confirmDemoTrade, true);
});

test('certificate panel shows bound account path version and enables only explicit ready START', () => {
  const p = panel();
  selectAccount(p, `S.host.pairingKey='key'; S.host.wsUrl='ws://127.0.0.1:5077/bridge'; S.host.tcpPort=5078;
    S.data.data.accounts[0].scope={eligibleTechnicalTest:true,strategyStartBlocked:false,
    certificate:{valid:true,canStart:true,status:'PASS',message:'Możesz osobno potwierdzić START',baselineId:'cert-scope',protocolVersion:1,testVersion:'demo-execution-v1',
    identity:{platform:'cTrader',broker:'Pepperstone',accountNumber:123,currency:'USD',label:'ExactLabel',executionPath:'DesktopBridge',safetyVersion:'scoped-demo-v1'}}};`);
  const view = p.run('viewConnections()');
  assert.match(view.textContent, /PASS/); assert.match(view.textContent, /123/); assert.match(view.textContent, /DesktopBridge/);
  assert.match(view.textContent, /demo-execution-v1/); assert.match(view.textContent, /ExactLabel/);
  const start = [...p.run('viewDashboard()').querySelectorAll('button')].find(b => b.textContent === 'START');
  assert.ok(!start.hasAttribute('disabled')); start.click(); assert.ok(p.document.querySelector('[role="dialog"]'));
  p.run(`S.data.data.accounts[0].scope.certificate.canStart=false;`);
  const blocked = [...p.run('viewDashboard()').querySelectorAll('button')].find(b => b.textContent === 'START');
  assert.ok(blocked.hasAttribute('disabled'));
});

test('interrupted certificate and required sync remain actionable and cannot enable START', () => {
  const p = panel();
  selectAccount(p, `S.data.data.accounts[0].scope={eligibleTechnicalTest:false,strategyStartBlocked:true,
    certificate:{valid:false,canStart:false,status:'Interrupted',message:'Test przerwany. Wymagana świeża synchronizacja i osobno potwierdzony test DEMO.'}};`);
  assert.match(p.run('viewConnections()').textContent, /Test przerwany.*synchronizacja/);
  const start = [...p.run('viewDashboard()').querySelectorAll('button')].find(b => b.textContent === 'START');
  assert.ok(start.hasAttribute('disabled'));
});

test('shipped interface has no AI Lab or automatic update controls', () => {
  for (const file of ['index.html', 'app.js', 'decision-center.js']) {
    const text = fs.readFileSync(path.join(www, file), 'utf8');
    assert.doesNotMatch(text, /\/api\/ai\/|AI Lab|Auto-Updates|unlockLive/);
  }
});
