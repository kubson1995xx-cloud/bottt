param([string]$Phase='red')
$ErrorActionPreference='Stop'
$repo=$env:GITHUB_WORKSPACE
$e=Join-Path $env:RUNNER_TEMP 'controller-windows-source'
. (Join-Path $repo 'tools/windows/TestEvidence.ps1')
$items=Join-Path $e 'composition-build-inputs';New-Item -ItemType Directory -Path $items|Out-Null
Push-Location (Join-Path $e 'sdk')
try {
 if($Phase.StartsWith('candidate')) {
  Push-Location (Join-Path $repo 'frontend/naval')
  $nativePreference=$ErrorActionPreference
  try {
   $ErrorActionPreference='Continue'
   & npm.cmd ci --ignore-scripts --no-audit --no-fund *> (Join-Path $e 'frontend-install.log');if($LASTEXITCODE -ne 0){throw 'Pinned frontend install failed'}
   & npm.cmd test *> (Join-Path $e 'frontend-tests.log');if($LASTEXITCODE -ne 0){throw 'Frontend tests failed'}
   & npm.cmd run build *> (Join-Path $e 'frontend-build.log');if($LASTEXITCODE -ne 0){throw 'Typed frontend build/staging failed'}
   & npm.cmd run test:build *> (Join-Path $e 'frontend-build-tests.log');if($LASTEXITCODE -ne 0){throw 'Staged frontend tests failed'}
  }finally{$ErrorActionPreference=$nativePreference;Pop-Location}
 }
 & dotnet build (Join-Path $repo 'tools/Apex.Backtester/Apex.Backtester.csproj') -c Release -p:NuGetAudit=false *> (Join-Path $e 'presentation-build.log')
 if($LASTEXITCODE -ne 0){throw 'Presentation producer compilation failed'}
 $presentation=Join-Path $env:RUNNER_TEMP ('composition-presentation-'+$env:GITHUB_RUN_ID)
 New-ApexPresentation $repo $presentation | ConvertTo-Json | Set-Content (Join-Path $e 'presentation-proof.json')
 $env:APEX_PRESENTATION_TEST_DIRECTORY=Join-Path $presentation 'presentation'
 Copy-Item $env:APEX_PRESENTATION_TEST_DIRECTORY (Join-Path $e 'public-presentation') -Recurse
 $project=Join-Path $repo 'tests/Apex.ControllerComposition.Tests/Apex.ControllerComposition.Tests.csproj'
 & dotnet build $project -c Release -p:NuGetAudit=false "-p:CustomAfterMicrosoftCommonTargets=$e/compile-evidence.targets" "-p:ControllerEvidenceRoot=$items" *> (Join-Path $e 'composition-build.log')
 if($LASTEXITCODE -ne 0){throw 'Composition compilation failed, not behavioral RED'}
 $testRoot=Join-Path $repo 'tests/Apex.ControllerComposition.Tests/bin/Release/net8.0'
 Copy-Item (Join-Path $repo 'src/Apex.Desktop/wwwroot') (Join-Path $testRoot 'wwwroot') -Recurse -Force
 Copy-Item $env:APEX_PRESENTATION_TEST_DIRECTORY (Join-Path $testRoot 'presentation') -Recurse
 $closure=Join-Path $e 'composition-closure';New-Item -ItemType Directory -Path $closure|Out-Null
 Copy-Item $testRoot (Join-Path $closure 'tests') -Recurse
 Copy-Item (Join-Path $repo 'src/Apex.Desktop/bin/Release/net8.0') (Join-Path $closure 'host') -Recurse
 $filter='FullyQualifiedName~Apex.ControllerComposition.Tests.CompositionTests'
 $old=$ErrorActionPreference
 try {$ErrorActionPreference='Continue'; & dotnet test $project -c Release --no-build --no-restore --filter $filter --logger 'trx;LogFileName=composition.trx' --results-directory $e *> (Join-Path $e 'composition-tests.log');$code=$LASTEXITCODE}finally{$ErrorActionPreference=$old}
 [ordered]@{phase=$Phase;exit=$code;sourceOnly=$true;privateBrokerTested=$false;vpsTested=$false}|ConvertTo-Json|Set-Content (Join-Path $e 'composition-summary.json')
 if($code -ne 0){throw 'Composition tests failed; exact payload and TRX retained'}
 if($Phase.StartsWith('candidate')) {
  $old=$ErrorActionPreference
  try {$ErrorActionPreference='Continue'; & dotnet test $project -c Release --no-build --no-restore --filter '(FullyQualifiedName!~Apex.ControllerComposition.Tests)&(FullyQualifiedName!~NativeEnvironmentTests)&(FullyQualifiedName!~CausalNativeTests)' --logger 'trx;LogFileName=affected-native.trx' --results-directory $e *> (Join-Path $e 'affected-native-tests.log');$nativeCode=$LASTEXITCODE}finally{$ErrorActionPreference=$old}
  if($nativeCode -ne 0){throw 'Affected native suite failed'}
  $package=Join-Path $env:RUNNER_TEMP 'APEX-Controller-Windows-x64-candidate'
  New-Item -ItemType Directory -Path $package|Out-Null
  $publish=Join-Path $package 'APEX-Desktop'
  $publishInputs=Join-Path $e 'publish-build-inputs';New-Item -ItemType Directory -Path $publishInputs|Out-Null
  & dotnet publish (Join-Path $repo 'src/Apex.Desktop/Apex.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false "-p:CustomAfterMicrosoftCommonTargets=$e/compile-evidence.targets" "-p:ControllerEvidenceRoot=$publishInputs" -o $publish *> (Join-Path $e 'publish.log')
  if($LASTEXITCODE -ne 0){throw 'Self-contained current EXE publish failed'}
  Copy-Item $env:APEX_PRESENTATION_TEST_DIRECTORY (Join-Path $publish 'presentation') -Recurse
  Copy-Item (Join-Path $repo 'tools/windows/CONTROLLER-START.pl.md') (Join-Path $package 'PRZECZYTAJ.pl.md')
  [IO.File]::WriteAllText((Join-Path $package 'Paruj-kontroler.cmd'),"@`"%~dp0APEX-Desktop\ApexDesktop.exe`" --pair-controller %*`r`n",[Text.Encoding]::ASCII)
  [ordered]@{schema='apex-controller-engineering-candidate-v1';sourceCommit=(& git -C $repo rev-parse HEAD);target='win-x64';sdk='8.0.427';selfContained=$true;syntheticAcceptanceOnly=$true;vpsPaired=$false;brokerTested=$false;windows11Accepted=$false;privateIdentityBundled=$false}|ConvertTo-Json|Set-Content (Join-Path $package 'apex-build.json') -Encoding UTF8
  Copy-Item (Join-Path $e 'source-inputs-before-build.json') (Join-Path $package 'source-inputs.json')
  $inventory=@(Get-ChildItem $package -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{path=$_.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length}})
  if(@($inventory|Where-Object{$_.path -match 'controller-ssh|controller-connection|authorized_keys|\.pub$|\.dpapi$|apex\.config|accounts/'}).Count -ne 0){throw 'Private file in candidate layout'}
  [IO.File]::WriteAllLines((Join-Path $package 'manifest.sha256'),@($inventory|ForEach-Object{$_.sha256+'  '+$_.path}),[Text.UTF8Encoding]::new($false))
  $candidate=Join-Path $e 'candidate';New-Item -ItemType Directory -Path $candidate|Out-Null
  $zip=Join-Path $candidate 'APEX-Controller-Windows-x64-candidate.zip'
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::CreateFromDirectory($package,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
  $zipHash=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  $extracted=$package+'-independently-extracted'
  & python (Join-Path $repo 'tests/package/safe_extract.py') $zip $extracted (Join-Path $e 'candidate-extraction.json') $zipHash
  if($LASTEXITCODE -ne 0){throw 'Candidate CRC/inventory/safe extraction failed'}
  $env:APEX_COMPOSITION_PACKAGE_EXE=Join-Path $extracted 'APEX-Desktop/ApexDesktop.exe'
  $env:APEX_COMPOSITION_EVIDENCE=$e
  $old=$ErrorActionPreference
  try {$ErrorActionPreference='Continue'; & dotnet test $project -c Release --no-build --no-restore --filter 'FullyQualifiedName~Apex.ControllerComposition.Tests.PackagedTests|FullyQualifiedName~Apex.ControllerComposition.Tests.NativePairingTests' --logger 'trx;LogFileName=extracted-package.trx' --results-directory $e *> (Join-Path $e 'extracted-package-tests.log');$packageCode=$LASTEXITCODE}finally{$ErrorActionPreference=$old}
  foreach($row in $inventory){if((Get-FileHash (Join-Path $extracted $row.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $row.sha256){throw 'Extracted package changed during native execution'}}
  if((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $zipHash){throw 'ZIP changed during native execution'}
  [ordered]@{zipSha256=$zipHash;exeSha256=(Get-FileHash $env:APEX_COMPOSITION_PACKAGE_EXE -Algorithm SHA256).Hash.ToLowerInvariant();packageExit=$packageCode;actualExtractedExe=$true;sourceTests=(Read-ApexTestCounts (Join-Path $e 'composition.trx'));affectedNative=(Read-ApexTestCounts (Join-Path $e 'affected-native.trx'));brokerTested=$false;vpsTested=$false;windows11Tested=$false;zipUnchanged=$true;extractedInventoryUnchanged=$true}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $e 'candidate-summary.json') -Encoding UTF8
  if($packageCode -ne 0){throw 'Actual extracted package native/UI test failed; candidate retained'}
 }
}finally{Pop-Location}
