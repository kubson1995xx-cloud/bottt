param([string]$Phase='red')
$ErrorActionPreference='Stop'
$repo=$env:GITHUB_WORKSPACE
$e=Join-Path $env:RUNNER_TEMP 'controller-windows-source'
. (Join-Path $repo 'tools/windows/TestEvidence.ps1')
$items=Join-Path $e 'composition-build-inputs';New-Item -ItemType Directory -Path $items|Out-Null
function Save-ControllerImports([string]$Project,[string]$Label,[bool]$Rid) {
 $dir=Join-Path $e ($Label+'-imports');New-Item -ItemType Directory -Path $dir|Out-Null
 $projects=@($Project,(Join-Path $repo 'src/Apex.Desktop/Apex.Desktop.csproj'),(Join-Path $repo 'src/Apex.Core/Apex.Core.csproj'),(Join-Path $repo 'src/Apex.Adapters.Okx/Apex.Adapters.Okx.csproj'))|Select-Object -Unique
 $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 foreach($p in $projects){
  $out=Join-Path $dir ((Split-Path $p -Leaf)+'.xml')
  $inputRoot=if($Rid){Join-Path $e 'publish-build-inputs'}else{$items}
  $args=@('msbuild',$p,'-p:Configuration=Release',('-preprocess:'+$out),"-p:CustomAfterMicrosoftCommonTargets=$e/compile-evidence.targets","-p:ControllerEvidenceRoot=$inputRoot");if($Rid){$args+=@('-p:RuntimeIdentifier=win-x64','-p:SelfContained=true')}
  & dotnet @args *> (Join-Path $dir ((Split-Path $p -Leaf)+'.log'));if($LASTEXITCODE -ne 0){throw 'MSBuild import expansion failed'}
  foreach($m in [regex]::Matches([IO.File]::ReadAllText($out),'(?m)^\s*([A-Za-z]:\\[^\r\n]+\.(?:props|targets))\s*$')){[void]$paths.Add($m.Groups[1].Value.Trim())}
 }
 if($paths.Count -eq 0){throw 'Empty actual MSBuild import closure'}
 $bytes=Join-Path $dir 'bytes';New-Item -ItemType Directory -Path $bytes|Out-Null
 @($paths|Sort-Object|ForEach-Object{$h=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant();Copy-Item -LiteralPath $_ -Destination (Join-Path $bytes ($h+'.bin')) -Force;[ordered]@{path=$_;bytes=(Get-Item -LiteralPath $_).Length;sha256=$h}})|ConvertTo-Json -Depth 4|Set-Content (Join-Path $dir 'imports.json')
}
Push-Location (Join-Path $e 'sdk')
try {
 if($Phase.StartsWith('candidate')) {
  Push-Location $repo
  $nativePreference=$ErrorActionPreference
  try {
   $ErrorActionPreference='Continue'
   & npm.cmd ci --ignore-scripts --no-audit --no-fund *> (Join-Path $e 'panel-install.log');if($LASTEXITCODE -ne 0){throw 'Pinned root panel dependency install failed'}
   & npm.cmd test *> (Join-Path $e 'panel-tests.log');if($LASTEXITCODE -ne 0){throw 'Existing Polish panel regression failed'}
  }finally{$ErrorActionPreference=$nativePreference;Pop-Location}
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
 if($Phase.StartsWith('candidate')) {
  $naval=Join-Path $repo 'src/Apex.Desktop/wwwroot/naval'
  Test-ApexNavalAssets $naval | ConvertTo-Json | Set-Content (Join-Path $e 'generated-assets-validation.json')
  @(Get-ChildItem $naval -File -Recurse | ForEach-Object{[ordered]@{path=('src/Apex.Desktop/wwwroot/naval/'+$_.FullName.Substring($naval.Length+1).Replace('\','/'));bytes=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}}) | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $e 'generated-assets-before-compile.json')
 }
 & dotnet build (Join-Path $repo 'tools/Apex.Backtester/Apex.Backtester.csproj') -c Release -p:NuGetAudit=false *> (Join-Path $e 'presentation-build.log')
 if($LASTEXITCODE -ne 0){throw 'Presentation producer compilation failed'}
 $presentation=Join-Path $env:RUNNER_TEMP ('composition-presentation-'+$env:GITHUB_RUN_ID)
 New-ApexPresentation $repo $presentation | ConvertTo-Json | Set-Content (Join-Path $e 'presentation-proof.json')
 $env:APEX_PRESENTATION_TEST_DIRECTORY=Join-Path $presentation 'presentation'
 Copy-Item $env:APEX_PRESENTATION_TEST_DIRECTORY (Join-Path $e 'public-presentation') -Recurse
 $project=Join-Path $repo 'tests/Apex.ControllerComposition.Tests/Apex.ControllerComposition.Tests.csproj'
 & dotnet restore $project -p:NuGetAudit=false *> (Join-Path $e 'composition-restore.log');if($LASTEXITCODE -ne 0){throw 'Composition restore failed'}
 Save-ControllerImports $project 'generic' $false
 & dotnet build $project -c Release --no-restore -p:NuGetAudit=false "-p:CustomAfterMicrosoftCommonTargets=$e/compile-evidence.targets" "-p:ControllerEvidenceRoot=$items" *> (Join-Path $e 'composition-build.log')
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
  # The unchanged transport/admission source was already exercised in 42120289954:
  # 105 pass / 1 obsolete empty-environment diagnostic fails against corrected product.
  # Do not replay unchanged passing cases or relabel that historical run all-green.
  $package=Join-Path $env:RUNNER_TEMP 'APEX-Controller-Windows-x64-candidate'
  New-Item -ItemType Directory -Path $package|Out-Null
  $publish=Join-Path $package 'APEX-Desktop'
  $publishInputs=Join-Path $e 'publish-build-inputs';New-Item -ItemType Directory -Path $publishInputs|Out-Null
  & dotnet restore (Join-Path $repo 'src/Apex.Desktop/Apex.Desktop.csproj') -r win-x64 -p:SelfContained=true -p:NuGetAudit=false *> (Join-Path $e 'publish-restore.log');if($LASTEXITCODE -ne 0){throw 'Publish restore failed'}
  Save-ControllerImports (Join-Path $repo 'src/Apex.Desktop/Apex.Desktop.csproj') 'publish' $true
  & dotnet publish (Join-Path $repo 'src/Apex.Desktop/Apex.Desktop.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false "-p:CustomAfterMicrosoftCommonTargets=$e/compile-evidence.targets" "-p:ControllerEvidenceRoot=$publishInputs" -o $publish *> (Join-Path $e 'publish.log')
  if($LASTEXITCODE -ne 0){throw 'Self-contained current EXE publish failed'}
  Copy-Item $env:APEX_PRESENTATION_TEST_DIRECTORY (Join-Path $publish 'presentation') -Recurse
  Copy-Item (Join-Path $repo 'tools/windows/CONTROLLER-START.pl.md') (Join-Path $package 'PRZECZYTAJ.pl.md')
  [IO.File]::WriteAllText((Join-Path $package 'Paruj-kontroler.cmd'),"@`"%~dp0APEX-Desktop\ApexDesktop.exe`" --pair-controller %*`r`n",[Text.Encoding]::ASCII)
  [ordered]@{schema='apex-controller-engineering-candidate-v1';sourceCommit=(& git -C $repo rev-parse HEAD);target='win-x64';sdk='8.0.427';selfContained=$true;syntheticAcceptanceOnly=$true;vpsPaired=$false;brokerTested=$false;windows11Accepted=$false;privateIdentityBundled=$false}|ConvertTo-Json|Set-Content (Join-Path $package 'apex-build.json') -Encoding UTF8
  Copy-Item (Join-Path $e 'source-inputs-before-build.json') (Join-Path $package 'source-inputs.json')
  Copy-Item (Join-Path $e 'generated-assets-before-compile.json') (Join-Path $package 'generated-assets-before-compile.json')
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
  [ordered]@{zipSha256=$zipHash;exeSha256=(Get-FileHash $env:APEX_COMPOSITION_PACKAGE_EXE -Algorithm SHA256).Hash.ToLowerInvariant();packageExit=$packageCode;actualExtractedExe=$true;sourceTests=(Read-ApexTestCounts (Join-Path $e 'composition.trx'));affectedNativePriorRun='42120289954: 105 pass / 1 obsolete diagnostic failed; not rerun, not called all-green';brokerTested=$false;vpsTested=$false;windows11Tested=$false;zipUnchanged=$true;extractedInventoryUnchanged=$true}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $e 'candidate-summary.json') -Encoding UTF8
  if($packageCode -ne 0){throw 'Actual extracted package native/UI test failed; candidate retained'}
 }
}finally{Pop-Location}
