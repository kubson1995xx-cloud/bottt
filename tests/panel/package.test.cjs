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

test('Naval frontend is built before .NET and both locks are cached without offline fallback', () => {
  const build = fs.readFileSync(path.join(root, 'tools/windows/Zbuduj-APEX.ps1'), 'utf8');
  const workflow = fs.readFileSync(path.join(root, '.github/workflows/windows-demo.yml'), 'utf8');
  assert.ok(build.indexOf('npm --prefix frontend/naval ci') >= 0);
  assert.ok(build.indexOf('npm --prefix frontend/naval run build') < build.indexOf('Invoke-ApexBuildDotnet build APEX.Trading.Demo.sln'));
  assert.match(build, /--offline/);
  assert.match(build, /--source \$emptyFeed/);
  assert.doesNotMatch(build, /ignore-failed-sources|legacy-peer-deps|SkipTests|PominTesty/);
  assert.match(workflow, /frontend\/naval\/package-lock\.json/);
  assert.match(workflow, /Test-NavalPackage\.ps1/);
  assert.match(workflow, /node-version: '26'/);
});

test('Windows CI builds, runs tests and smoke-starts actual published application', () => {
  const workflow = fs.readFileSync(path.join(root, '.github/workflows/windows-demo.yml'), 'utf8');
  assert.match(workflow, /windows-2022/);
  assert.match(workflow, /Zbuduj-APEX\.ps1/);
  assert.match(workflow, /Smoke-Windows\.ps1/);
  assert.match(workflow, /upload-artifact/);
  assert.doesNotMatch(workflow, /PominTesty/);
});

test('Task5 seals Windows defaults, native Source evidence and exact self-contained publish', () => {
  const build = fs.readFileSync(path.join(root, 'tools/windows/Zbuduj-APEX.ps1'), 'utf8');
  const verify = fs.readFileSync(path.join(root, 'tools/windows/Sprawdz-Pakiet.ps1'), 'utf8');
  assert.match(build, /-p:PublishTrimmed=false','-p:PublishReadyToRun=false/);
  assert.match(build, /--filter ['"]Gate=Source['"]/);
  assert.match(build, /trx;LogFileName=windows.trx/);
  assert.match(build, /Test-ApexWindowsPublish/);
  assert.match(build, /PRZECZYTAJ-WINDOWS.md/);
  assert.match(verify, /windows-local-presentation-v1/);
  assert.match(verify, /existingConfigPolicy/);
  assert.match(verify, /Test-ApexWindowsPublish/);
  assert.match(verify, /Weryfikacja\/windows.trx/);
});

test('Task5 candidate lane selects actual SDK8 without changing default delivery', () => {
  const workflow = fs.readFileSync(path.join(root, '.github/workflows/windows-demo.yml'), 'utf8');
  const lane = workflow.slice(workflow.indexOf('  nativePackage:'));
  assert.match(lane, /inputs.mode == 'task5-package'/);
  assert.match(lane, /runs-on: windows-latest/);
  assert.match(lane, /APEX-NAVAL-COMMAND-Windows-x64/);
  assert.match(lane, /Test-PackageCandidate.ps1/);
  assert.match(workflow, /default: full-delivery/);
});

test('Windows runtime smoke isolates only the EXE child and checks module origins', () => {
  const smoke = fs.readFileSync(path.join(root, 'tools/windows/Smoke-Windows.ps1'), 'utf8');
  assert.match(smoke, /\[string\]\$Evidence/);
  assert.match(smoke, /EnvironmentVariables\['DOTNET_ROOT'\]/);
  assert.match(smoke, /EnvironmentVariables\['PATH'\]/);
  assert.match(smoke, /DOTNET_BUNDLE_EXTRACT_BASE_DIR/);
  assert.match(smoke, /coreclr/);
  assert.match(smoke, /hostpolicy/);
  assert.match(smoke, /ucrtbase/);
  assert.match(smoke, /Get-Acl/);
  assert.doesNotMatch(smoke, /\$env:(?:PATH|DOTNET_ROOT)\s*=/i);
  for (const file of ['Smoke-Windows.ps1', 'TestEvidence.ps1', 'Sprawdz-Pakiet.ps1'])
    assert.ok([...fs.readFileSync(path.join(root, 'tools/windows', file))].every(b => b < 128), file + ' must remain PS5.1 ASCII');
});
