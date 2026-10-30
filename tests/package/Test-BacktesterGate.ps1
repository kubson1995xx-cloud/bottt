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
$real=Join-Path $Scratch 'real-presentation'
Case 'presentation-build-real-export' { New-ApexPresentation -Source $Source -Scratch $real | Out-Null }
$package=[IO.Path]::GetFullPath((Join-Path $Scratch 'controlled-package'))
# Task5 controlled layout contracts: dummy EXE/TRX are NEVER product evidence.
# The presentation is the genuine producer output above; runtime metadata here is
# an explicitly controlled parser fixture, not a claim of native publication.
$windowsRuntimeText='{"runtimeOptions":{"tfm":"net8.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"8.0.0"},{"name":"Microsoft.AspNetCore.App","version":"8.0.0"}]}}'
function Set-WindowsControl {
    Copy-Item -LiteralPath (Join-Path $real 'presentation') -Destination (Join-Path $package 'APEX-Desktop/presentation') -Recurse
    Put (Join-Path $package 'PRZECZYTAJ.md') 'CONTROLLED_NOT_PRODUCT'
    Copy-Item $fixture (Join-Path $package 'Weryfikacja/windows.trx')
    $meta.navalReadDefaultEnabled=$true;$meta.firstRunProfile='windows-local-presentation-v1';$meta.existingConfigPolicy='preserve';$meta.task7Acceptance='pending'
    $meta.testCounts.windows=@{total=1;passed=1;failed=0}
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $digest=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($windowsRuntimeText)))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
    $script:wp=@{schema='apex-windows-publish-v1';rid='win-x64';sdkVersion='8.0.427';exeSha256=(Hash (Join-Path $package 'APEX-Desktop/ApexDesktop.exe'));runtimeConfigUtf8=$windowsRuntimeText;runtimeConfigSha256=$digest;assetsSha256=('3'*64);publishArgv=@('publish','src/Apex.Desktop/Apex.Desktop.csproj','-c','Release','--no-restore','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:PublishTrimmed=false','-p:PublishReadyToRun=false','-p:IsTransformWebConfigDisabled=true','-o','APEX-Desktop');runtimePacks=@(@{name='Microsoft.NETCore.App.Runtime.win-x64';version='8.0.0';sha512=[Convert]::ToBase64String([byte[]]::new(64))},@{name='Microsoft.AspNetCore.App.Runtime.win-x64';version='8.0.0';sha512=[Convert]::ToBase64String([byte[]]::new(64))});nativeLibraries=@(@{name='coreclr.dll';pack='Microsoft.NETCore.App.Runtime.win-x64';bytes=1;sha256=('4'*64)})}
}
function Windows-Control { Reset-Package }
function Seal-Windows { Put (Join-Path $package 'Weryfikacja/windows-publish.json') ($wp|ConvertTo-Json -Depth 12);Seal }

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
    # Create the parent BEFORE Copy-Item: PS5.1 DirectoryNotFoundException can
    # terminate even with SilentlyContinue. Never hide a fixture construction failure.
    New-Item -ItemType Directory -Force (Join-Path $package 'Weryfikacja') | Out-Null
    foreach ($suite in @('bot','platform','backtester')) { Copy-Item -LiteralPath $fixture -Destination (Join-Path $package ('Weryfikacja/'+$suite+'.trx')) -Force }
    $inputs=@{schema='apex-source-inputs-v1';files=@(@{path='package-lock.json';bytes=1;sha256=('1'*64)},@{path='frontend/naval/package-lock.json';bytes=1;sha256=('2'*64)})}
    Put (Join-Path $package 'Weryfikacja/source-inputs.json') ($inputs|ConvertTo-Json -Depth 5)
    $tap="TAP version 13`nok 1 - controlled`n1..1`n# tests 1`n# suites 0`n# pass 1`n# fail 0`n# cancelled 0`n# skipped 0`n# todo 0`n"
    Put (Join-Path $package 'Weryfikacja/frontend-tests.txt') $tap
    Put (Join-Path $package 'Weryfikacja/frontend-build-tests.txt') $tap
    Put (Join-Path $package 'Weryfikacja/frontend-results.json') (@{schema='apex-frontend-tests-v1';tests=@{total=1;passed=1;failed=0;log='frontend-tests.txt';logSha256=(Hash (Join-Path $package 'Weryfikacja/frontend-tests.txt'))};build=@{total=1;passed=1;failed=0;log='frontend-build-tests.txt';logSha256=(Hash (Join-Path $package 'Weryfikacja/frontend-build-tests.txt'))};typecheckExit=0;buildExit=0}|ConvertTo-Json -Depth 5)
    foreach ($name in @('INSTALACJA_WINDOWS.md','PROCEDURA_DEMO_PEPPERSTONE.md','PODLACZENIE_PLATFORM.md','INSTRUKCJA_PANELU.md','KONFIGURACJA.md','OGRANICZENIA_I_STATUS.md','WYNIKI_TESTOW.md','PRZEGLAD_BEZPIECZENSTWA.md','METODOLOGIA_TESTOW.md','BACKTESTER.md','NAVAL_READ_PUBLICATION.md','NAVAL_DELIVERY_VPS.md','NAVAL_OFFLINE.md','NAVAL_SHARED_OFFLINE.md','README.md','RAPORT_BADAN.md','NAVAL_FRONTEND.md')) { Put (Join-Path $package ('Dokumentacja/'+$name)) 'CONTROLLED_NOT_PRODUCT' }
    Put (Join-Path $package 'MT5-opcjonalny/ApexBridge.mq5') 'CONTROLLED_NOT_PRODUCT'
    Copy-Item (Join-Path $Source 'tools/windows/Smoke-Windows.ps1') (Join-Path $package 'Weryfikacja')
    foreach ($name in @('Sprawdz-Pakiet.ps1','TestEvidence.ps1')) { Copy-Item (Join-Path $Source ('tools/windows/'+$name)) $package }
    $script:meta=@{schema='apex-build-v2';sourceCommit=('a'*40);sourceDirty=$true;target='win-x64';deploymentMode='self-contained';requiredRuntime='included';demoOnly=$true;botTestsRun=$true;platformTestsRun=$true;backtesterTestsRun=$true;panelTestsRun=$true;frontendTestsRun=$true;frontendBuildVerified=$true;brokerTested=$false;windowsRuntimeTested=$false;navalReadDefaultEnabled=$false;task7Acceptance='accepted-local-linux';sourceInputsSha256=(Hash (Join-Path $package 'Weryfikacja/source-inputs.json'));rootLockSha256=('1'*64);navalLockSha256=('2'*64);navalManifestSha256=(Hash (Join-Path $naval 'build-manifest.json'));frontendResultsSha256=(Hash (Join-Path $package 'Weryfikacja/frontend-results.json'));tools=@{node='v26.8.1';npm='11.19.0';dotnet='8.0.427';powershell='7.6.9'};testCounts=@{bot=@{total=1;passed=1;failed=0};platform=@{total=1;passed=1;failed=0};backtester=@{total=1;passed=1;failed=0}}}
    Set-WindowsControl
}
function Seal {
    if ($meta.target -ceq 'win-x64') { Put (Join-Path $package 'Weryfikacja/windows-publish.json') ($wp|ConvertTo-Json -Depth 12) }
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
    Reset-Package;Put (Join-Path $package 'Dokumentacja/README.md') 'FAKE_TASK8A_SECRET_SENTINEL_NOT_A_CREDENTIAL';Seal
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
# Review I1-I3 behavioral regressions. All additions are controlled, never executable.
foreach ($bad in @('naval-local-access.json','APEX-Desktop/wwwroot/cache/session.json','APEX-Desktop/replay.exe','APEX-Desktop/OtherProduct.dll','Dokumentacja/extra.md','Weryfikacja/unapproved.txt','APEX-Desktop/wwwroot/extra.js')) {
    Reset-Package; Put (Join-Path $package $bad) '{"schema":"naval-local-access-v1","principals":[]}'; Seal
    Case ('ExplicitShippedLayout-'+$bad) { Reject { Verify } }
}
Case 'PrivateMapSchemaInAllowedDocumentNotBasename' {
    Reset-Package; Put (Join-Path $package 'Dokumentacja/README.md') '{"schema":"naval-local-access-v1","principals":[]}'; Seal
    $refusal='';try { Verify } catch { $refusal=$_.Exception.Message }
    if ($refusal -cne 'Private access-map document in package') { throw 'Schema content guard not reached' }
}
Case 'PrivateMapSchemaLargerThanSentinelWindow' {
    Reset-Package
    Put (Join-Path $package 'Dokumentacja/README.md') ('{"schema":"naval-local-access-v1","principals":[],"padding":"'+('x'*8388608)+'"}')
    Seal; Reject { Verify }
}
Case 'EmptyCacheDirectoryRefused' { Reset-Package; New-Item -ItemType Directory (Join-Path $package 'APEX-Desktop/wwwroot/cache')|Out-Null; Seal; Reject { Verify } }
Case 'MissingRequiredDocumentation' { Reset-Package; Remove-Item (Join-Path $package 'Dokumentacja/NAVAL_DELIVERY_VPS.md'); Seal; Reject { Verify } }
Case 'NestedUnlistedExactCasePair' {
    Reset-Package; Seal
    Put (Join-Path $naval 'assets/INDEX-TEST.js') 'CONTROLLED_UNLISTED_ALIAS'
    Reject { Test-ApexNavalAssets $naval }; Reject { Verify }
}
Case 'NestedManifestCaseCollision' {
    Reset-Package; Put (Join-Path $naval 'assets/INDEX-TEST.js') 'CONTROLLED_ALIAS'
    $m=Get-Content (Join-Path $naval 'build-manifest.json') -Raw|ConvertFrom-Json
    $m.artifacts+=@{path='assets/INDEX-TEST.js';bytes=16;sha256=(Hash (Join-Path $naval 'assets/INDEX-TEST.js'))}
    Put (Join-Path $naval 'build-manifest.json') ($m|ConvertTo-Json -Depth 6)
    Reject { Test-ApexNavalAssets $naval }
}
Case 'OuterUnlistedExactCasePair' { Reset-Package; Seal; Put (Join-Path $package 'Dokumentacja/readme.md') 'CONTROLLED_ALIAS'; Reject { Verify } }
Case 'OuterDeclaredSpellingMustBeExact' {
    Reset-Package; Seal; $p=Join-Path $package 'Dokumentacja/README.md'; Move-Item $p (Join-Path $package 'Dokumentacja/readme.md'); Reject { Verify }
}
foreach ($fault in @('log-bytes','binding','missing-log','missing-ref','traversal-ref','wrong-allowed-ref','absolute-ref','case-ref','tap-fail','tap-skip','tap-cancel','tap-todo','tap-duplicate-counter','tap-missing-counter','tap-missing-plan','tap-missing-results','tap-count-contradiction','declared-skip','declared-cancel','declared-todo')) {
    Reset-Package
    $p=Join-Path $package 'Weryfikacja/frontend-results.json';$f=Get-Content $p -Raw|ConvertFrom-Json
    $log=Join-Path $package 'Weryfikacja/frontend-tests.txt';$text=[IO.File]::ReadAllText($log)
    switch ($fault) {
        'log-bytes' { Put $log 'CONTROLLED_NO_TEST_RESULTS' }
        'binding' { $f.tests.logSha256='0'*64 }
        'missing-log' { Remove-Item $log }
        'missing-ref' { $f.tests.PSObject.Properties.Remove('log') }
        'traversal-ref' { $f.tests.log='../frontend-tests.txt' }
        'wrong-allowed-ref' { $f.tests.log='frontend-build-tests.txt';$f.tests.logSha256=Hash (Join-Path $package ('Weryfikacja/'+$f.tests.log)) }
        'absolute-ref' { $f.tests.log=$log }
        'case-ref' { $f.tests.log='FRONTEND-TESTS.txt' }
        'tap-fail' { $text=$text.Replace('# fail 0','# fail 1') }
        'tap-skip' { $text=$text.Replace('# skipped 0','# skipped 1') }
        'tap-cancel' { $text=$text.Replace('# cancelled 0','# cancelled 1') }
        'tap-todo' { $text=$text.Replace('# todo 0','# todo 1') }
        'tap-duplicate-counter' { $text+="# tests 1`n" }
        'tap-missing-counter' { $text=$text.Replace("# skipped 0`n",'') }
        'tap-missing-plan' { $text=$text.Replace("1..1`n",'') }
        'tap-missing-results' { $text=$text.Replace("ok 1 - controlled`n",'') }
        'tap-count-contradiction' { $text=$text.Replace('# tests 1','# tests 2').Replace('# pass 1','# pass 2') }
        'declared-skip' { $f.tests|Add-Member skipped 1 }
        'declared-cancel' { $f.tests|Add-Member cancelled 1 }
        'declared-todo' { $f.tests|Add-Member todo 1 }
    }
    if ($fault -like 'tap-*') { Put $log $text; $f.tests.logSha256=Hash $log }
    Put $p ($f|ConvertTo-Json -Depth 6);$meta.frontendResultsSha256=Hash $p;Seal
    Case ('FrontendEvidence-'+$fault) { Reject { Verify } }
}
# I3-R1: every result-shaped line must count or fail explicitly, never disappear.
# These controls use real shipped scripts and freshly bound log/metadata hashes.
$flatResults=[ordered]@{
    described=@{line='ok 1 - controlled';pass=$true}
    bare=@{line='ok 1';pass=$true}
    crlf=@{line='ok 1';pass=$true;crlf=$true}
    wrongId=@{line='ok 2';pass=$false}
    zeroId=@{line='ok 0';pass=$false}
    leadingZeroId=@{line='ok 01';pass=$false}
    negativeId=@{line='ok -1';pass=$false}
    signedId=@{line='ok +1';pass=$false}
    decimalId=@{line='ok 1.0';pass=$false}
    attachedId=@{line='ok1';pass=$false}
    underscoreId=@{line='ok_1';pass=$false}
    overflowId=@{line='ok 999999999999999999999999';pass=$false}
    missingId=@{line='ok';pass=$false}
    missingIdDescription=@{line='ok - controlled';pass=$false}
    unnumberedDescription=@{line='ok controlled';pass=$false}
    noDashDescription=@{line='ok 1 controlled';pass=$false}
    emptyDescription=@{line='ok 1 -';pass=$false}
    tabSeparator=@{line="ok`t1";pass=$false}
    doubleSpace=@{line='ok  1';pass=$false}
    indented=@{line='  ok 1';pass=$false}
    uppercase=@{line='OK 1';pass=$false}
    failed=@{line='not ok 1';pass=$false}
    malformedFailed=@{line='notok 1';pass=$false}
    skipDirective=@{line='ok 1 - controlled # SKIP';pass=$false}
    todoDirective=@{line='ok 1 - controlled # TODO';pass=$false}
}
foreach ($entry in $flatResults.GetEnumerator()) {
    $shape=$entry.Value
    foreach ($kind in @('tests','build')) {
        Reset-Package
        $p=Join-Path $package 'Weryfikacja/frontend-results.json';$f=Get-Content $p -Raw|ConvertFrom-Json
        $log=Join-Path $package ('Weryfikacja/'+$f.$kind.log)
        $text=[IO.File]::ReadAllText($log).Replace('ok 1 - controlled',$shape.line)
        if ($shape.crlf) { $text=$text.Replace("`n","`r`n") }
        Put $log $text;$f.$kind.logSha256=Hash $log
        Put $p ($f|ConvertTo-Json -Depth 6);$meta.frontendResultsSha256=Hash $p;Seal
        Case ('FlatTap-'+$kind+'-'+$entry.Key) {
            if ($shape.pass) { & (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package }
            else { Reject { & (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package } }
        }
    }
}
foreach ($line in @('ok 2','ok 2 - CONTROLLED_EXTRA_RECORD','ok 1','ok 1 - CONTROLLED_DUPLICATE','ok','ok +2','ok2','  ok 2','OK 2','notok 2')) {
    Reset-Package
    $p=Join-Path $package 'Weryfikacja/frontend-results.json';$f=Get-Content $p -Raw|ConvertFrom-Json
    $log=Join-Path $package 'Weryfikacja/frontend-tests.txt'
    Put $log ([IO.File]::ReadAllText($log).Replace('1..1',($line+"`n1..1")))
    $f.tests.logSha256=Hash $log;Put $p ($f|ConvertTo-Json -Depth 6);$meta.frontendResultsSha256=Hash $p;Seal
    Case ('FlatTap-Extra-'+$line) { Reject { & (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package } }
}
Case 'FlatTap-MixedBareDescribedSequentialPositive' {
    Reset-Package
    $p=Join-Path $package 'Weryfikacja/frontend-results.json';$f=Get-Content $p -Raw|ConvertFrom-Json
    $log=Join-Path $package 'Weryfikacja/frontend-tests.txt'
    $text=[IO.File]::ReadAllText($log).Replace('1..1',"ok 2`n1..2").Replace('# tests 1','# tests 2').Replace('# pass 1','# pass 2')
    Put $log $text;$f.tests.total=2;$f.tests.passed=2;$f.tests.logSha256=Hash $log
    Put $p ($f|ConvertTo-Json -Depth 6);$meta.frontendResultsSha256=Hash $p;Seal
    & (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package
}
Case 'LinuxExplicitProductLayoutPositive' {
    Reset-Package
    Remove-Item (Join-Path $package 'APEX-Desktop/ApexDesktop.exe');Remove-Item (Join-Path $package 'Uruchom APEX.cmd')
    foreach ($p in @('APEX-Desktop/ApexDesktop.dll','APEX-Desktop/ApexDesktop.deps.json','APEX-Desktop/Apex.Core.dll','Uruchom APEX.sh')) { Put (Join-Path $package $p) 'CONTROLLED_NOT_PRODUCT' }
    Put (Join-Path $package 'APEX-Desktop/ApexDesktop.runtimeconfig.json') '{"runtimeOptions":{"tfm":"net8.0","frameworks":[{"name":"Microsoft.AspNetCore.App","version":"8.0.0"}]}}'
    $meta.target='linux-x64';$meta.navalReadDefaultEnabled=$false;$meta.Remove('firstRunProfile');$meta.Remove('existingConfigPolicy');$meta.testCounts.Remove('windows');$meta.deploymentMode='framework-dependent';$meta.requiredRuntime='Microsoft.AspNetCore.App 8'
    foreach ($name in @('PRZECZYTAJ.md','Weryfikacja/windows.trx','Weryfikacja/windows-publish.json','APEX-Desktop/presentation')) { $p=Join-Path $package $name;if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }
    Seal;Verify
}
Case 'LinuxRequiredCoreAssembly' { Remove-Item (Join-Path $package 'APEX-Desktop/Apex.Core.dll'); Seal; Reject { Verify } }
Case 'NonemptyDestinationAndExistingZipRefused' {
    $dest=Join-Path $Scratch 'no-overwrite';New-Item -ItemType Directory -Path $dest|Out-Null;Put (Join-Path $dest 'keep.txt') 'KEEP'
    Reject { & (Join-Path $Source 'tools/windows/Zbuduj-APEX.ps1') -Wyjscie $dest }
    Remove-Item (Join-Path $dest 'keep.txt');Put ($dest+'.zip') 'KEEP'
    Reject { & (Join-Path $Source 'tools/windows/Zbuduj-APEX.ps1') -Wyjscie $dest }
    if ([IO.File]::ReadAllText($dest+'.zip') -ne 'KEEP') { throw 'Overwrote ZIP' }
}
# Task2: the only positive presentation is produced by the real committed replay CLI.
# These are helper contracts, NOT a fake full release or runtime-host simulator.

$presentation = Join-Path $Scratch 'presentation-control'
function Reset-Presentation {
    if (Test-Path -LiteralPath $presentation) { Remove-Item -LiteralPath $presentation -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $real 'presentation') -Destination $presentation -Recurse
}
function Presentation-Json([string]$name,$value) { Put (Join-Path $presentation $name) ($value | ConvertTo-Json -Depth 64) }
function Rehash-Presentation {
    $m=Get-Content -LiteralPath (Join-Path $presentation 'presentation.json') -Raw | ConvertFrom-Json
    $m.publicationSha256=Hash (Join-Path $presentation 'naval-publication.json')
    Presentation-Json 'presentation.json' $m
}
function Refuse-Presentation {
    $reason='';try { Test-ApexPresentation $presentation | Out-Null } catch { $reason=$_.Exception.Message }
    if ($reason -notlike 'Presentation *') { throw ('Presentation validator did not refuse at its contract: '+$reason) }
}
Case 'presentation-valid-real-export' {
    Reset-Presentation
    $m=Test-ApexPresentation $presentation
    if ($m.schema -cne 'apex-presentation-v1' -or $m.scope.Id -cne 'presentation') { throw 'Wrong presentation metadata' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $real 'presentation') -Force).Count -ne 2 -or -not (Test-Path (Join-Path $real 'presentation-result.json')) -or -not (Test-Path (Join-Path $real 'presentation-authority'))) { throw 'Public/private export split missing' }
}
Case 'presentation-corrupt-rehashed-export' {
    Reset-Presentation
    $p=Get-Content (Join-Path $presentation 'naval-publication.json') -Raw|ConvertFrom-Json
    $p.publication.journal.checkpoint.currentQuality='LIVE'
    Presentation-Json 'naval-publication.json' $p; Rehash-Presentation; Refuse-Presentation
}
Case 'presentation-foreign-scope' {
    Reset-Presentation
    $m=Get-Content (Join-Path $presentation 'presentation.json') -Raw|ConvertFrom-Json
    $m.scope.Accounts[0].AccountId='foreign'; Presentation-Json 'presentation.json' $m; Refuse-Presentation
}
Case 'presentation-live-label' {
    Reset-Presentation
    $p=Get-Content (Join-Path $presentation 'naval-publication.json') -Raw|ConvertFrom-Json
    $p.temporalClassification='LIVE'; Presentation-Json 'naval-publication.json' $p; Rehash-Presentation; Refuse-Presentation
}
Case 'presentation-unlisted-file' { Reset-Presentation; Put (Join-Path $presentation 'authority.json') '{}'; Refuse-Presentation }
foreach ($fault in @('scope-id-array','account-venue-array','account-environment-array','missing-file','wrong-hash','extra-metadata','missing-metadata','duplicate-metadata','case-metadata','invalid-hash','extra-scope','duplicate-account','live-account','extra-portfolio','credential','directory','case-file','malformed-export')) {
    Case ('presentation-'+$fault) {
        Reset-Presentation
        $m=Get-Content (Join-Path $presentation 'presentation.json') -Raw|ConvertFrom-Json
        switch ($fault) {
            'scope-id-array' { $m.scope.Id=@('presentation'); Presentation-Json 'presentation.json' $m }
            'account-venue-array' { $m.scope.Accounts[0].Venue=@('SYNTHETIC'); Presentation-Json 'presentation.json' $m }
            'account-environment-array' { $m.scope.Accounts[0].Environment=@('Offline'); Presentation-Json 'presentation.json' $m }
            'missing-file' { Remove-Item (Join-Path $presentation 'naval-publication.json') }
            'wrong-hash' { $m.publicationSha256='0'*64; Presentation-Json 'presentation.json' $m }
            'extra-metadata' { $m|Add-Member extra 'unapproved'; Presentation-Json 'presentation.json' $m }
            'missing-metadata' { $m.PSObject.Properties.Remove('coreSha256'); Presentation-Json 'presentation.json' $m }
            'duplicate-metadata' { $path=Join-Path $presentation 'presentation.json'; Put $path ([IO.File]::ReadAllText($path).Replace('"schema":',('"schema":"apex-presentation-v1","schema":'))) }
            'case-metadata' { $path=Join-Path $presentation 'presentation.json'; Put $path ([IO.File]::ReadAllText($path).Replace('"schema":','"Schema":')) }
            'invalid-hash' { $m.inputSha256=('A'*64); Presentation-Json 'presentation.json' $m }
            'extra-scope' { $m.scope|Add-Member credential 'NOT_A_SECRET'; Presentation-Json 'presentation.json' $m }
            'duplicate-account' { $m.scope.Accounts=@($m.scope.Accounts[0],$m.scope.Accounts[0]); Presentation-Json 'presentation.json' $m }
            'live-account' { $m.scope.Accounts[0].Environment='Live'; Presentation-Json 'presentation.json' $m }
            'extra-portfolio' { $m.scope.PortfolioId|Add-Member extra 'unapproved'; Presentation-Json 'presentation.json' $m }
            'credential' { $p=Get-Content (Join-Path $presentation 'naval-publication.json') -Raw|ConvertFrom-Json; $p|Add-Member apiKey 'NOT_A_SECRET'; Presentation-Json 'naval-publication.json' $p; Rehash-Presentation }
            'directory' { New-Item -ItemType Directory (Join-Path $presentation 'cache')|Out-Null }
            'case-file' { Move-Item (Join-Path $presentation 'presentation.json') (Join-Path $presentation 'PRESENTATION.json') }
            'malformed-export' { Put (Join-Path $presentation 'naval-publication.json') '{'; Rehash-Presentation }
        }
        Refuse-Presentation
    }
}
# F1: check raw root shape, before the PowerShell pipeline can unwrap arrays.
foreach ($shape in @('singleton-array','multiple-array','empty-array','string','number','boolean','null')) {
    Case ('presentation-metadata-root-'+$shape) {
        Reset-Presentation
        $path=Join-Path $presentation 'presentation.json'; $text=[IO.File]::ReadAllText($path)
        switch ($shape) {
            'singleton-array' { $text='['+$text+']' }
            'multiple-array' { $text='['+$text+','+$text+']' }
            'empty-array' { $text='[]' }
            'string' { $text='"metadata"' }
            'number' { $text='1' }
            'boolean' { $text='true' }
            'null' { $text='null' }
        }
        Put $path $text; Refuse-Presentation
    }
}
Case 'presentation-export-root-singleton-array' {
    Reset-Presentation
    $path=Join-Path $presentation 'naval-publication.json'
    Put $path ('['+[IO.File]::ReadAllText($path)+']'); Rehash-Presentation; Refuse-Presentation
}
# F2: matching cross-file values still must have exact SHA256 syntax.
foreach ($field in @('publicationSha256','inputSha256','coreSha256','backtesterSha256')) {
    Case ('presentation-metadata-final-LF-'+$field) {
        Reset-Presentation
        $m=Get-Content (Join-Path $presentation 'presentation.json') -Raw|ConvertFrom-Json
        $m.$field += "`n"; Presentation-Json 'presentation.json' $m; Refuse-Presentation
    }
}
foreach ($field in @('inputSha256','coreSha256','backtesterSha256')) {
    Case ('presentation-provenance-final-LF-'+$field) {
        Reset-Presentation
        $p=Get-Content (Join-Path $presentation 'naval-publication.json') -Raw|ConvertFrom-Json
        $m=Get-Content (Join-Path $presentation 'presentation.json') -Raw|ConvertFrom-Json
        $p.provenance.$field += "`n"; $m.$field=$p.provenance.$field
        Presentation-Json 'naval-publication.json' $p; Presentation-Json 'presentation.json' $m
        Rehash-Presentation; Refuse-Presentation
    }
}
foreach ($shape in @('final-LF','array','number')) {
    Case ('presentation-content-digest-'+$shape) {
        Reset-Presentation
        $p=Get-Content (Join-Path $presentation 'naval-publication.json') -Raw|ConvertFrom-Json
        switch ($shape) {
            'final-LF' { $p.contentSha256 += "`n" }
            'array' { $p.contentSha256=@($p.contentSha256) }
            'number' { $p.contentSha256=1 }
        }
        Presentation-Json 'naval-publication.json' $p; Rehash-Presentation; Refuse-Presentation
    }
}
foreach ($casing in @('lower','upper','mixed','final-LF')) {
    Case ('presentation-config-digest-'+$casing) {
        Reset-Presentation
        $p=Get-Content (Join-Path $presentation 'naval-publication.json') -Raw|ConvertFrom-Json
        $m=Get-Content (Join-Path $presentation 'presentation.json') -Raw|ConvertFrom-Json
        $digest=$m.scope.ConfigDigest.ToLowerInvariant()
        switch ($casing) {
            'upper' { $digest=$digest.ToUpperInvariant() }
            'mixed' { $digest=$digest.Substring(0,32).ToUpperInvariant()+$digest.Substring(32) }
            'final-LF' { $digest += "`n" }
        }
        $m.scope.ConfigDigest=$digest; $p.publication.journal.checkpoint.configDigest=$digest
        Presentation-Json 'naval-publication.json' $p; Presentation-Json 'presentation.json' $m; Rehash-Presentation
        # Helper lexical/casing contract only, not a re-canonicalized typed export.
        if ($casing -eq 'final-LF') { Refuse-Presentation } else { Test-ApexPresentation $presentation | Out-Null }
    }
}
Case 'WindowsTask5ShippedPositive' { Windows-Control;Seal-Windows;& (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package }
foreach ($fault in @('missing-exe','missing-presentation','missing-runtime-proof','shared-framework','wrong-rid','wrong-default','wrong-profile','wrong-preserve','missing-windows-trx','zero-windows-trx','failed-windows-trx','skip-windows-trx','loose-dll','replay-exe','private-map','session-cache')) {
    Case ('WindowsTask5-'+$fault) {
        Windows-Control
        switch ($fault) {
            'missing-exe' { Remove-Item (Join-Path $package 'APEX-Desktop/ApexDesktop.exe') }
            'missing-presentation' { Remove-Item (Join-Path $package 'APEX-Desktop/presentation/presentation.json') }
            'shared-framework' { $wp.runtimeConfigUtf8=$windowsRuntimeText.Replace('includedFrameworks','frameworks');$sha=[Security.Cryptography.SHA256]::Create();try {$wp.runtimeConfigSha256=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($wp.runtimeConfigUtf8)))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()} }
            'wrong-rid' { $wp.rid='linux-x64' }
            'wrong-default' { $meta.navalReadDefaultEnabled=$false }
            'wrong-profile' { $meta.firstRunProfile='legacy' }
            'wrong-preserve' { $meta.existingConfigPolicy='reset' }
            'missing-windows-trx' { Remove-Item (Join-Path $package 'Weryfikacja/windows.trx') }
            'zero-windows-trx' { Write-Fixture 0 0 0; Copy-Item $fixture (Join-Path $package 'Weryfikacja/windows.trx') }
            'failed-windows-trx' { Write-Fixture 1 0 1; Copy-Item $fixture (Join-Path $package 'Weryfikacja/windows.trx') }
            'skip-windows-trx' { Put (Join-Path $package 'Weryfikacja/windows.trx') ([IO.File]::ReadAllText($fixture).Replace("outcome='Passed'","outcome='NotExecuted'")) }
            'loose-dll' { Put (Join-Path $package 'APEX-Desktop/arbitrary.dll') 'CONTROLLED_NOT_PRODUCT' }
            'replay-exe' { Put (Join-Path $package 'APEX-Desktop/replay.exe') 'CONTROLLED_NOT_PRODUCT' }
            'private-map' { Put (Join-Path $package 'APEX-Desktop/presentation/local-access.json') '{"schema":"naval-local-access-v1"}' }
            'session-cache' { Put (Join-Path $package 'APEX-Desktop/wwwroot/cache/session.json') '{}' }
        }
        Seal-Windows
        if ($fault -eq 'missing-runtime-proof') { Remove-Item (Join-Path $package 'Weryfikacja/windows-publish.json') }
        Reject { & (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package }
        Write-Fixture 1 1 0
    }
}
[IO.File]::WriteAllText((Join-Path $Scratch 'results.json'),($results.ToArray()|ConvertTo-Json -Depth 6))
$fail=@($results|Where-Object {-not $_.pass}).Count
Write-Output "Controlled package gates: $($results.Count) cases, $fail failed; NOT product totals or native runtime proof"
if ($fail -gt 0) { throw "$fail controlled packaging assertions failed" }
