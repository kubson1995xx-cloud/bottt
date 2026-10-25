'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { parseHTML } = require('linkedom');
const root = path.resolve(__dirname, '../..');
const www = path.join(root, 'src/Apex.Desktop/wwwroot');

function panel() {
  const { window, document } = parseHTML(fs.readFileSync(path.join(www, 'index.html'), 'utf8'));
  const calls = [];
  const storage = new Map();
  window.matchMedia = () => ({ matches: false });
  window.scrollTo = () => {};
  const context = vm.createContext({
    document, window, console, Intl, Date, URL, Blob,
    localStorage: { getItem: k => storage.get(k) || null, setItem: (k, v) => storage.set(k, v) },
    setInterval: () => 1, clearInterval: () => {}, setTimeout: () => 1, clearTimeout: () => {},
    fetch: async (url, opts) => {
      calls.push({ url, opts });
      return { ok: true, status: 200, json: async () => ({ host: { version: 'test' }, data: { accounts: [] } }) };
    },
  });
  vm.runInContext(fs.readFileSync(path.join(www, 'app.js'), 'utf8'), context);
  return { document, context, calls, run: source => vm.runInContext(source, context) };
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

test('API HTTP error is an error, not a successful command', async () => {
  const p = panel();
  p.context.fetch = async () => ({ ok: false, status: 504, json: async () => ({ message: 'Polecenie anulowane.' }) });
  await assert.rejects(p.run(`api('/api/accounts/x/command', {action:'start'})`), /504|anulowane/);
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

test('shipped interface has no AI Lab or automatic update controls', () => {
  for (const file of ['index.html', 'app.js', 'decision-center.js']) {
    const text = fs.readFileSync(path.join(www, file), 'utf8');
    assert.doesNotMatch(text, /\/api\/ai\/|AI Lab|Auto-Updates|unlockLive/);
  }
});
