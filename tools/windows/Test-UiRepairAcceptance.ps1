#Requires -Version 5.1
# Test-only continuation over an immutable candidate. Never rebuild/reseal runtime.
param([Parameter(Mandatory=$true)][string]$Download,[Parameter(Mandatory=$true)][string]$Original,[Parameter(Mandatory=$true)][string]$ZipSha256)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$e=Join-Path $env:RUNNER_TEMP 'ui-acceptance-evidence';New-Item -ItemType Directory -Path $e|Out-Null
$package=Join-Path $env:RUNNER_TEMP 'ui-candidate-extracted';$prior=Join-Path $env:RUNNER_TEMP 'ui-original-extracted'
& python (Join-Path $repo 'tests/package/safe_extract.py') (Join-Path $Download 'APEX-NAVAL-COMMAND-Windows-x64.zip') $package (Join-Path $e 'candidate-readback.json') $ZipSha256
if ($LASTEXITCODE -ne 0) {throw 'Candidate pin/extraction failed'}
& python (Join-Path $repo 'tests/package/safe_extract.py') (Join-Path $Original 'APEX-NAVAL-COMMAND-Windows-x64.zip') $prior (Join-Path $e 'original-readback.json') '87deacbf67fd9de196511951ed95c50ff3c1f2595e8c769037824252d8732005'
if ($LASTEXITCODE -ne 0) {throw 'Original pin/extraction failed'}
& (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package
$inputs=Get-Content (Join-Path $package 'Weryfikacja/source-inputs.json') -Raw|ConvertFrom-Json
$runtime=@($inputs.files|Where-Object {$_.path -match '^(src/|frontend/)'});$bindings=@()
foreach ($f in $runtime) {
 $file=Join-Path $repo $f.path
 if (-not(Test-Path $file -PathType Leaf) -or (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $f.sha256) {throw ('Candidate runtime/source input differs: '+$f.path)}
 $bindings+=@{path=$f.path;sha256=$f.sha256}
}
$runtimeNames=@{};foreach ($f in $runtime) {$runtimeNames[$f.path]=$true}
foreach ($name in @(& git -C $repo ls-files src frontend)) {if(-not $runtimeNames.ContainsKey($name)){throw ('New runtime input not covered by candidate: '+$name)}}
$meta=Get-Content (Join-Path $package 'apex-build.json') -Raw|ConvertFrom-Json
@{testCommit=(& git -C $repo rev-parse HEAD);productCommit=$meta.sourceCommit;zipSha256=$ZipSha256;kind='test-only-extracted-pointer-native-profile-acceptance';runtimeInputs=$bindings}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $e 'source-binding.json') -Encoding UTF8
# Build test consumers using exact shipped assets; no new npm frontend build and no runtime publish.
$stage=Join-Path $repo 'src/Apex.Desktop/wwwroot/naval';New-Item -ItemType Directory -Force -Path $stage|Out-Null
Copy-Item (Join-Path $package 'APEX-Desktop/wwwroot/naval/*') $stage -Recurse -Force
$sdk=Join-Path $env:RUNNER_TEMP 'ui-acceptance-sdk';New-Item -ItemType Directory -Path $sdk|Out-Null
[IO.File]::WriteAllText((Join-Path $sdk 'global.json'),'{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
$dotnet=(Get-Command dotnet).Source;$env:DOTNET_HOST_PATH=$dotnet;$env:APEX_PACKAGE_ROOT=$package;$env:APEX_WINDOWS_EVIDENCE=$e
$env:APEX_LEGACY_EXE=Join-Path $prior 'APEX-Desktop/ApexDesktop.exe';$env:APEX_LEGACY_EXE_SHA256=(Get-FileHash $env:APEX_LEGACY_EXE -Algorithm SHA256).Hash.ToLowerInvariant()
$failed=$false
Push-Location $sdk
try {
 & $dotnet build (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release
 if ($LASTEXITCODE -ne 0) {throw 'Test-only build failed'}
 $closure=Join-Path $e 'test-runtime';New-Item -ItemType Directory -Path $closure|Out-Null
 Copy-Item (Join-Path $repo 'tests/Apex.Windows.Tests/bin/Release/net8.0/*') $closure -Recurse
 foreach ($gate in @('UiRepair','UiInventory','UiUpgrade')) {
  & $dotnet test (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release --no-build --no-restore --filter ('Gate='+$gate) --logger ('trx;LogFileName='+$gate+'.trx') --results-directory $e
  if ($LASTEXITCODE -ne 0) {$failed=$true}
 }
}finally {Pop-Location}
& (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package
@{status=$(if($failed){'FAIL'}else{'PASS'});productCommit=$meta.sourceCommit;testCommit=(& git -C $repo rev-parse HEAD);zipSha256=$ZipSha256;securePositiveVerified=$false;privateBrokerTested=$false;runtimeRebuilt=$false}|ConvertTo-Json|Set-Content (Join-Path $e 'summary.json') -Encoding UTF8
if ($failed) {throw 'Actual candidate acceptance failed; see individual TRX'}
