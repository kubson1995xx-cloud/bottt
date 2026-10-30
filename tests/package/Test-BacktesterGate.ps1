param([Parameter(Mandatory=$true)][string]$Source, [Parameter(Mandatory=$true)][string]$Scratch)
$ErrorActionPreference = 'Stop'
. (Join-Path $Source 'tools/windows/TestEvidence.ps1')
if (Test-Path -LiteralPath $Scratch) { throw 'Fixture directory must be new' }
New-Item -ItemType Directory -Path $Scratch | Out-Null
$fixture = Join-Path $Scratch 'controlled-trx.xml'
$results = New-Object Collections.Generic.List[object]
function Case([string]$name, [scriptblock]$body) {
    try { & $body; $results.Add(@{name=$name;pass=$true}) } catch { $results.Add(@{name=$name;pass=$false;reason=$_.Exception.Message}); Write-Host "FAIL $name : $($_.Exception.Message)" }
}
function Write-Fixture([int]$total, [int]$passed, [int]$failed) {
    [IO.File]::WriteAllText($fixture, "<TestRun><Results><UnitTestResult executionId='one' outcome='Passed'/></Results><ResultSummary><Counters total='$total' executed='$total' passed='$passed' failed='$failed' error='0' timeout='0' aborted='0' inconclusive='0' notExecuted='0'/></ResultSummary></TestRun>")
}
function Reject([scriptblock]$body) { $bad=$false; try { & $body } catch { $bad=$true }; if (-not $bad) { throw 'Accepted deliberately invalid controlled fixture' } }
function Put([string]$p,[string]$s) { New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p,$s) }
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
Case 'TrxValid' { Write-Fixture 1 1 0; $c=Read-ApexTestCounts $fixture; if ($c.total -ne 1 -or $c.passed -ne 1) { throw 'Wrong counts' } }
foreach ($v in @(@(0,0,0),@(1,0,1),@(2,2,0))) { $vv=$v; Case ('TrxRejected-'+($v -join '-')) { Write-Fixture $vv[0] $vv[1] $vv[2]; Reject { Read-ApexTestCounts $fixture } } }
# Controlled files are never executed. This valid control must pass before negative mutations.
$package=[IO.Path]::GetFullPath((Join-Path $Scratch 'controlled-package'))
function Reset-Package {
    if (Test-Path $package) { Remove-Item -LiteralPath $package -Recurse -Force }
    foreach ($p in @('APEX-Desktop/ApexDesktop.exe','APEX-Desktop/wwwroot/index.html','APEX-Desktop/wwwroot/app.js','APEX-Desktop/wwwroot/decision-center.js','APEX-Desktop/wwwroot/app.css','cTrader/ApexBridge.algo','cTrader/ApexTradingAI.algo','Uruchom APEX.cmd','Dokumentacja/THIRD_PARTY_NAVAL.txt')) { Put (Join-Path $package $p) 'CONTROLLED_NOT_PRODUCT' }
    $script:naval=Join-Path $package 'APEX-Desktop/wwwroot/naval'
    Put (Join-Path $naval 'index.html') '<script type="module" src="/naval/assets/index-test.js"></script><link href="/naval/assets/index-test.css">'
    Put (Join-Path $naval 'assets/index-test.js') 'import("./chunk-test.js");'
    Put (Join-Path $naval 'assets/chunk-test.js') 'export const controlled=true;'
    Put (Join-Path $naval 'assets/index-test.css') 'body{}'
    $a=@('index.html','assets/index-test.js','assets/chunk-test.js','assets/index-test.css') | ForEach-Object { @{path=$_;bytes=(Get-Item (Join-Path $naval $_)).Length;sha256=(Hash (Join-Path $naval $_))} }
    Put (Join-Path $naval 'build-manifest.json') (@{schema='naval-build-v1';artifacts=@($a)} | ConvertTo-Json -Depth 6)
    Write-Fixture 1 1 0
    foreach ($suite in @('bot','platform','backtester')) { Copy-Item $fixture (Join-Path $package ('Weryfikacja/'+$suite+'.trx')) -Force -ErrorAction SilentlyContinue; if (-not (Test-Path (Join-Path $package ('Weryfikacja/'+$suite+'.trx')))) { New-Item -ItemType Directory -Force (Join-Path $package 'Weryfikacja') | Out-Null; Copy-Item $fixture (Join-Path $package ('Weryfikacja/'+$suite+'.trx')) } }
    $inputs=@{schema='apex-source-inputs-v1';files=@(@{path='package-lock.json';bytes=1;sha256=('1'*64)},@{path='frontend/naval/package-lock.json';bytes=1;sha256=('2'*64)})}
    Put (Join-Path $package 'Weryfikacja/source-inputs.json') ($inputs|ConvertTo-Json -Depth 5)
    Put (Join-Path $package 'Weryfikacja/frontend-results.json') (@{schema='apex-frontend-tests-v1';tests=@{total=1;passed=1;failed=0};build=@{total=1;passed=1;failed=0};typecheckExit=0;buildExit=0}|ConvertTo-Json -Depth 5)
    $script:meta=@{schema='apex-build-v2';sourceCommit=('a'*40);sourceDirty=$true;target='win-x64';deploymentMode='self-contained';requiredRuntime='included';demoOnly=$true;botTestsRun=$true;platformTestsRun=$true;backtesterTestsRun=$true;panelTestsRun=$true;frontendTestsRun=$true;frontendBuildVerified=$true;brokerTested=$false;windowsRuntimeTested=$false;navalReadDefaultEnabled=$false;task7Acceptance='accepted-local-linux';sourceInputsSha256=(Hash (Join-Path $package 'Weryfikacja/source-inputs.json'));rootLockSha256=('1'*64);navalLockSha256=('2'*64);navalManifestSha256=(Hash (Join-Path $naval 'build-manifest.json'));frontendResultsSha256=(Hash (Join-Path $package 'Weryfikacja/frontend-results.json'));tools=@{node='v26.8.1';npm='11.19.0';dotnet='8.0.427';powershell='7.6.9'};testCounts=@{bot=@{total=1;passed=1;failed=0};platform=@{total=1;passed=1;failed=0};backtester=@{total=1;passed=1;failed=0}}}
}
function Seal {
    Put (Join-Path $package 'apex-build.json') ($meta|ConvertTo-Json -Depth 8)
    $prefix=$package.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $lines=@(Get-ChildItem -LiteralPath $package -Force -Recurse -File | Where-Object {$_.Name -ne 'manifest.sha256'} | ForEach-Object { (Hash $_.FullName)+'  '+$_.FullName.Substring($prefix.Length).Replace('\','/') })
    [IO.File]::WriteAllLines((Join-Path $package 'manifest.sha256'),$lines)
}
function Verify { & (Join-Path $Source 'tools/windows/Sprawdz-Pakiet.ps1') -Pakiet $package }
Reset-Package; Seal
Case 'ValidPackage' { Verify }
Case 'AlternateTrailingCanonicalRoot' { & (Join-Path $Source 'tools/windows/Sprawdz-Pakiet.ps1') -Pakiet ($package.Replace([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)+[IO.Path]::AltDirectorySeparatorChar) }
Case 'RealShippedVerifierDoesNotSelfReject' {
    Copy-Item (Join-Path $Source 'tools/windows/Sprawdz-Pakiet.ps1') $package
    Copy-Item (Join-Path $Source 'tools/windows/TestEvidence.ps1') $package
    Seal; Verify
}
Case 'KnownFakeSentinelInInnocuousManifestedFileRefused' {
    Reset-Package;Put (Join-Path $package 'innocuous.txt') 'FAKE_TASK8A_SECRET_SENTINEL_NOT_A_CREDENTIAL';Seal
    $refusal='';try {Verify} catch {$refusal=$_.Exception.Message}
    if ($refusal -cne 'Known test secret sentinel in package') {throw 'Sentinel content guard not reached'}
}
Reset-Package;Seal
Case 'ParentTraversal' { Put (Join-Path $package 'manifest.sha256') (('0'*64)+'  ../outside.txt'); Reject { Verify } }
foreach ($fault in @('missing-flag','wrong-count','missing-trx')) {
    Reset-Package
    if ($fault -eq 'missing-flag') { $meta.backtesterTestsRun=$false }; if ($fault -eq 'wrong-count') { $meta.testCounts.backtester.total=2 }; if ($fault -eq 'missing-trx') { Remove-Item (Join-Path $package 'Weryfikacja/backtester.trx') }
    Seal; Case $fault { Reject { Verify } }
}
$mutations=[ordered]@{
 NavalRequiredNotSilentlyAbsent={Remove-Item -LiteralPath $naval -Recurse -Force}
 NavalInventoryMissingChunk={Remove-Item (Join-Path $naval 'assets/chunk-test.js')}
 NavalInventoryRehashedButStaleAgainstBuild={Put (Join-Path $naval 'assets/chunk-test.js') 'STALE';$m=Get-Content (Join-Path $naval 'build-manifest.json') -Raw|ConvertFrom-Json;$e=$m.artifacts|Where-Object path -eq 'assets/chunk-test.js';$e.bytes=5;$e.sha256=Hash (Join-Path $naval $e.path);Put (Join-Path $naval 'build-manifest.json') ($m|ConvertTo-Json -Depth 6)}
 NavalUnlistedExtraAsset={Put (Join-Path $naval 'assets/extra.js') 'CONTROLLED'}
 NavalSymlinkOrReparse={New-Item -ItemType SymbolicLink -Path (Join-Path $naval 'assets/link.js') -Target (Join-Path $naval 'assets/chunk-test.js') | Out-Null}
 WrongTargetOrMissingRuntimeFile={$meta.target='linux-x64';$meta.deploymentMode='framework-dependent';$meta.requiredRuntime='Microsoft.AspNetCore.App 8'}
 UnknownTarget={$meta.target='unknown'}
 SourceMetadataOrTrxMismatch={$meta.sourceInputsSha256='0'*64}
 MissingSourceSchema={$meta.Remove('schema')}
}
foreach ($entry in $mutations.GetEnumerator()) { Reset-Package; & $entry.Value; Seal; Case $entry.Key { Reject { Verify } } }
foreach ($bad in @('../escape.js','/absolute.js','C:/drive.js','assets/index-test.js')) {
    Reset-Package;$m=Get-Content (Join-Path $naval 'build-manifest.json') -Raw|ConvertFrom-Json;$m.artifacts[2].path=$bad;Put (Join-Path $naval 'build-manifest.json') ($m|ConvertTo-Json -Depth 6);$meta.navalManifestSha256=Hash (Join-Path $naval 'build-manifest.json');Seal
    Case ('NavalDuplicateTraversalOrAbsolutePath-'+$bad) { Reject { Verify } }
}
# Every sentinel is fake and unmistakable; each is re-manifested before verification.
foreach ($bad in @('apex.config.json','state.json','desktop_control.json','authority.json','journal.json','owner.lock','export.json','access.json','digest-map.json','.env.test','tokens.txt','api.key','credentials.json','host.log','trace.txt','raw.csv','datasets/data.txt','.git/config','.npm/cache','node_modules/fake.js','Apex.Tests.dll','Apex.Backtester.dll','Apex.Naval.Replay.dll','Apex.AiLab.dll','Dokumentacja/AI_LAB_ZALEGLOSCI.md','.naval-stage-test/residue.js')) {
    Reset-Package;Put (Join-Path $package $bad) 'CONTROLLED_EXCLUDED_DATA_NOT_REAL';Seal
    Case ('SecretOrUserDataOrArchiveOrCacheEvenWhenManifested-'+$bad) { Reject { Verify } }
}
Case 'NonemptyDestinationAndExistingZipRefused' {
    $dest=Join-Path $Scratch 'no-overwrite';New-Item -ItemType Directory -Path $dest|Out-Null;Put (Join-Path $dest 'keep.txt') 'KEEP'
    Reject { & (Join-Path $Source 'tools/windows/Zbuduj-APEX.ps1') -Wyjscie $dest }
    Remove-Item (Join-Path $dest 'keep.txt');Put ($dest+'.zip') 'KEEP'
    Reject { & (Join-Path $Source 'tools/windows/Zbuduj-APEX.ps1') -Wyjscie $dest }
    if ([IO.File]::ReadAllText($dest+'.zip') -ne 'KEEP') { throw 'Overwrote ZIP' }
}
[IO.File]::WriteAllText((Join-Path $Scratch 'results.json'),($results.ToArray()|ConvertTo-Json -Depth 6))
$fail=@($results|Where-Object {-not $_.pass}).Count
Write-Output "Controlled package gates: $($results.Count) cases, $fail failed; NOT product totals or native runtime proof"
if ($fail -gt 0) { throw "$fail controlled packaging assertions failed" }
