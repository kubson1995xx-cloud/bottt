param([string]$Phase='red')
$ErrorActionPreference='Stop'
$repo=$env:GITHUB_WORKSPACE
$e=Join-Path $env:RUNNER_TEMP 'controller-windows-source'
. (Join-Path $repo 'tools/windows/TestEvidence.ps1')
$items=Join-Path $e 'composition-build-inputs';New-Item -ItemType Directory -Path $items|Out-Null
Push-Location (Join-Path $e 'sdk')
try {
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
 $filter='FullyQualifiedName~Apex.ControllerComposition.Tests'
 $old=$ErrorActionPreference
 try {$ErrorActionPreference='Continue'; & dotnet test $project -c Release --no-build --no-restore --filter $filter --logger 'trx;LogFileName=composition.trx' --results-directory $e *> (Join-Path $e 'composition-tests.log');$code=$LASTEXITCODE}finally{$ErrorActionPreference=$old}
 [ordered]@{phase=$Phase;exit=$code;sourceOnly=$true;privateBrokerTested=$false;vpsTested=$false}|ConvertTo-Json|Set-Content (Join-Path $e 'composition-summary.json')
 if($code -ne 0){throw 'Composition tests failed; exact payload and TRX retained'}
}finally{Pop-Location}
