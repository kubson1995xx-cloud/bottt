'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');

test('Windows package is fail-closed, manifest verified and excludes archive AI module', () => {
  const build = fs.readFileSync(path.join(root, 'tools/windows/Zbuduj-APEX.ps1'), 'utf8');
  assert.match(build, /Test-Path.*Wyjscie/);
  assert.match(build, /manifest\.sha256/);
  assert.match(build, /Sprawdz-Pakiet\.ps1/);
  assert.match(build, /APEX\.Trading\.Demo\.sln/);
  assert.doesNotMatch(build, /Copy-Item[^\n]*docs\\\*\.md/);
  assert.ok([...Buffer.from(build)].every(b => b < 128), 'PowerShell 5.1 script must stay ASCII');
});

test('Windows CI builds, runs tests and smoke-starts actual published application', () => {
  const workflow = fs.readFileSync(path.join(root, '.github/workflows/windows-demo.yml'), 'utf8');
  assert.match(workflow, /windows-2022/);
  assert.match(workflow, /Zbuduj-APEX\.ps1/);
  assert.match(workflow, /Smoke-Windows\.ps1/);
  assert.match(workflow, /upload-artifact/);
  assert.doesNotMatch(workflow, /PominTesty/);
});
