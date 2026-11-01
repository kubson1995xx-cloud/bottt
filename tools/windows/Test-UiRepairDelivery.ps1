#Requires -Version 5.1
# Fresh UI/host/runtime; only identical engine/platform/backtester source evidence reused.
param([Parameter(Mandatory=$true)][string]$Download)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$e=Join-Path $env:RUNNER_TEMP 'ui-delivery-evidence';New-Item -ItemType Directory -Path $e|Out-Null
$zip=Join-Path $Download 'APEX-NAVAL-COMMAND-Windows-x64.zip'
$prior=Join-Path $env:RUNNER_TEMP 'ui-reuse-prior'
& python (Join-Path $repo 'tests/package/safe_extract.py') $zip $prior (Join-Path $e 'prior-readback.json') '87deacbf67fd9de196511951ed95c50ff3c1f2595e8c769037824252d8732005'
if ($LASTEXITCODE -ne 0) {throw 'Pinned prior extraction failed'}
& (Join-Path $prior 'Sprawdz-Pakiet.ps1') -Pakiet $prior
$os=Get-CimInstance Win32_OperatingSystem
@{sourceCommit=(& git -C $repo rev-parse HEAD);sourceDirty=[bool](& git -C $repo status --porcelain);os=$os.Caption;osVersion=$os.Version;mode='ui-only-fresh-runtime-unchanged-engine-reuse'}|ConvertTo-Json|Set-Content (Join-Path $e 'identity.json') -Encoding UTF8
$output=Join-Path $env:RUNNER_TEMP 'APEX-NAVAL-COMMAND-Windows-x64'
& (Join-Path $PSScriptRoot 'Zbuduj-APEX.ps1') -Target win-x64 -Wyjscie $output -UiOnlyReuseRoot $prior
$dotnet=(Get-Command dotnet).Source
$env:APEX_PACKAGE_ROOT=$output+'-extracted';$env:APEX_WINDOWS_EVIDENCE=$e;$env:DOTNET_HOST_PATH=$dotnet
$env:APEX_LEGACY_EXE=Join-Path $prior 'APEX-Desktop/ApexDesktop.exe';$env:APEX_LEGACY_EXE_SHA256=(Get-FileHash $env:APEX_LEGACY_EXE -Algorithm SHA256).Hash.ToLowerInvariant()
$sdk=Join-Path $env:RUNNER_TEMP 'ui-final-sdk';New-Item -ItemType Directory -Path $sdk|Out-Null
[IO.File]::WriteAllText((Join-Path $sdk 'global.json'),'{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
$runtime=Join-Path $e 'test-runtime';New-Item -ItemType Directory -Path $runtime|Out-Null
Copy-Item (Join-Path $repo 'tests/Apex.Windows.Tests/bin/Release/net8.0/*') $runtime -Recurse
$failed=$false
Push-Location $sdk
try {
 foreach ($gate in @('UiRepair','UiInventory','UiUpgrade','UiNavigation')) {
  & $dotnet test (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release --no-build --no-restore --filter ('Gate='+$gate) --logger ('trx;LogFileName='+$gate+'.trx') --results-directory $e
  if ($LASTEXITCODE -ne 0) {$failed=$true}
 }
}finally {Pop-Location}
& (Join-Path $PSScriptRoot 'Smoke-Windows.ps1') -Pakiet ($output+'-extracted') -Evidence ($output+'-smoke-evidence')
& (Join-Path ($output+'-extracted') 'Sprawdz-Pakiet.ps1') -Pakiet ($output+'-extracted')
@{status=$(if($failed){'FAIL'}else{'PASS'});sourceCommit=(& git -C $repo rev-parse HEAD);zipSha256=(Get-FileHash ($output+'.zip') -Algorithm SHA256).Hash.ToLowerInvariant();exeSha256=(Get-FileHash (Join-Path ($output+'-extracted') 'APEX-Desktop/ApexDesktop.exe') -Algorithm SHA256).Hash.ToLowerInvariant();uiRepair=$(if(-not $failed){Read-ApexTestCounts (Join-Path $e 'UiRepair.trx')}else{$null});uiInventory=$(if(-not $failed){Read-ApexTestCounts (Join-Path $e 'UiInventory.trx')}else{$null});securePositiveVerified=$false;privateBrokerTested=$false;engineSuites='REUSED: identical source inputs, original TRX retained, not rerun'}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $e 'summary.json') -Encoding UTF8
if ($failed) {throw 'Actual extracted pointer/native UI gate failed; retain candidate and exact evidence'}
