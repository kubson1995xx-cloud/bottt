#requires -Version 5.1
param([ValidateSet('red','green','delivery')][string]$Phase='green')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$e=Join-Path $env:RUNNER_TEMP 'okx-simple-connect';New-Item -ItemType Directory -Path $e|Out-Null
$sdk=Join-Path $e 'sdk';New-Item -ItemType Directory -Path $sdk|Out-Null
[IO.File]::WriteAllText((Join-Path $sdk 'global.json'),'{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
$os=Get-CimInstance Win32_OperatingSystem
@{source=(& git -C $repo rev-parse HEAD);phase=$Phase;os=$os.Caption;version=$os.Version}|ConvertTo-Json|Set-Content (Join-Path $e 'identity.json') -Encoding UTF8
$inputs=@()
foreach($base in @('src','tests','tools','frontend/naval')) {
 foreach($f in Get-ChildItem (Join-Path $repo $base) -Recurse -File) {
  if($f.FullName -notmatch '[\\/](?:bin|obj|node_modules|dist)[\\/]') {
   $inputs+=@{path=$f.FullName.Substring($repo.Length+1);sha256=(Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
  }
 }
}
$inputs|ConvertTo-Json -Depth 4|Set-Content (Join-Path $e 'inputs.json') -Encoding UTF8
$dotnet=(Get-Command dotnet).Source
$env:APEX_WINDOWS_EVIDENCE=$e
if($Phase -eq 'delivery') {
 $prior=Join-Path $env:RUNNER_TEMP 'simple-connect-reuse'
 & python (Join-Path $repo 'tests/package/safe_extract.py') (Join-Path $env:RUNNER_TEMP 'simple-connect-prior/APEX-NAVAL-COMMAND-Windows-x64.zip') $prior (Join-Path $e 'prior-readback.json') '87deacbf67fd9de196511951ed95c50ff3c1f2595e8c769037824252d8732005'
 if($LASTEXITCODE -ne 0){throw 'Pinned prior CRC/hash/layout refused'}
 $output=Join-Path $env:RUNNER_TEMP 'APEX-NAVAL-COMMAND-Windows-x64'
 & (Join-Path $PSScriptRoot 'Zbuduj-APEX.ps1') -Target win-x64 -Wyjscie $output -UiOnlyReuseRoot $prior
 $env:APEX_PACKAGE_ROOT=$output+'-extracted'
 $env:APEX_LEGACY_EXE=Join-Path $prior 'APEX-Desktop/ApexDesktop.exe'
 $env:APEX_LEGACY_EXE_SHA256=(Get-FileHash $env:APEX_LEGACY_EXE -Algorithm SHA256).Hash.ToLowerInvariant()
}
Push-Location $sdk
try {
 if ((& $dotnet --version) -cne '8.0.427'){throw 'SDK pin failed'}
 & $dotnet build (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release *> (Join-Path $e 'build.log');if($LASTEXITCODE -ne 0){throw 'Native build failed'}
 Copy-Item (Join-Path $repo 'tests/Apex.Windows.Tests/bin/Release/net8.0') (Join-Path $e 'native-runtime') -Recurse
 & $dotnet test (Join-Path $e 'native-runtime/Apex.Windows.Tests.dll') --filter 'Gate=SimpleConnectSource' --logger 'trx;LogFileName=native.trx' --results-directory $e
 if($LASTEXITCODE -ne 0){throw 'Native behavioral gate failed; exact inputs and runtime retained'}
 if($Phase -ne 'red') {
  & $dotnet build (Join-Path $repo 'tests/Apex.Adapters.Okx.Tests/Apex.Adapters.Okx.Tests.csproj') -c Release *> (Join-Path $e 'adapter-build.log')
  if($LASTEXITCODE -ne 0){throw 'Adapter native consumer build failed'}
  Copy-Item (Join-Path $repo 'tests/Apex.Adapters.Okx.Tests/bin/Release/net8.0') (Join-Path $e 'adapter-runtime') -Recurse
  & $dotnet test (Join-Path $repo 'tests/Apex.Adapters.Okx.Tests/Apex.Adapters.Okx.Tests.csproj') -c Release --no-build --no-restore --filter 'FullyQualifiedName~OkxEnrollmentDiscoveryTests|FullyQualifiedName~OkxNativeQuotaWaitTests|FullyQualifiedName~OkxSerializedDispatchTests' --logger 'trx;LogFileName=adapter-native.trx' --results-directory $e
  if($LASTEXITCODE -ne 0){throw 'Native signed discovery/quota gate failed'}
 }
 if($Phase -eq 'delivery') {
  $failed=$false
  Copy-Item (Join-Path $repo 'tests/Apex.Tests/bin/Release/net8.0') (Join-Path $e 'host-runtime') -Recurse
  & $dotnet test (Join-Path $repo 'tests/Apex.Tests/Apex.Tests.csproj') -c Release --no-build --no-restore --filter 'FullyQualifiedName~OkxEnrollmentBoundaryTests|FullyQualifiedName~OkxDemoBalanceTests|FullyQualifiedName~OkxNativeCredentialTests|FullyQualifiedName~OkxCredentialTests|FullyQualifiedName~OkxControlTests|FullyQualifiedName~OkxCompositionTests|FullyQualifiedName~OkxActivationTests|FullyQualifiedName~OkxNativeWaitTests' --logger 'trx;LogFileName=host-affected.trx' --results-directory $e
  if($LASTEXITCODE -ne 0){$failed=$true}
  foreach($gate in @('EnrollmentSource','UiRepair','UiInventory','UiUpgrade','UiNavigation')) {
   & $dotnet test (Join-Path $e 'native-runtime/Apex.Windows.Tests.dll') --filter ('Gate='+$gate) --logger ('trx;LogFileName='+$gate+'.trx') --results-directory $e
   if($LASTEXITCODE -ne 0){$failed=$true}
  }
  & (Join-Path $PSScriptRoot 'Smoke-Windows.ps1') -Pakiet $env:APEX_PACKAGE_ROOT -Evidence (Join-Path $e 'extracted-smoke')
  & (Join-Path $env:APEX_PACKAGE_ROOT 'Sprawdz-Pakiet.ps1') -Pakiet $env:APEX_PACKAGE_ROOT
  @{status=$(if($failed){'FAIL'}else{'PASS'});source=(& git -C $repo rev-parse HEAD);zipSha256=(Get-FileHash ($output+'.zip') -Algorithm SHA256).Hash.ToLowerInvariant();exeSha256=(Get-FileHash (Join-Path $env:APEX_PACKAGE_ROOT 'APEX-Desktop/ApexDesktop.exe') -Algorithm SHA256).Hash.ToLowerInvariant();protectedPositiveVerified=$false;privateBrokerConnected=$false;scope='Native form, DPAPI helper and actual extracted UI; protected production positive requires local Windows verification then genuine user DEMO GET';engineEvidence='Retained source-bound original suites, not rerun; affected host tests run separately'}|ConvertTo-Json|Set-Content (Join-Path $e 'summary.json') -Encoding UTF8
  if($failed){throw 'Affected/extracted gate failed; candidate and exact evidence retained'}
 }
}finally{Pop-Location}
