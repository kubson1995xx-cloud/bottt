param([ValidateSet('red','green')][string]$Phase='red')
$ErrorActionPreference='Stop'
$repo=$env:GITHUB_WORKSPACE
$e=Join-Path $env:RUNNER_TEMP 'controller-windows-source'
$items=Join-Path $e 'local-build-inputs';New-Item -ItemType Directory -Path $items|Out-Null
Push-Location (Join-Path $e 'sdk')
try {
 $project=Join-Path $repo 'tests/Apex.Tests/Apex.Tests.csproj'
 & dotnet build $project -c Release -p:NuGetAudit=false "-p:CustomAfterMicrosoftCommonTargets=$e/compile-evidence.targets" "-p:ControllerEvidenceRoot=$items" *> (Join-Path $e 'local-build.log')
 if($LASTEXITCODE -ne 0){throw 'Local execution compilation failed; not behavioral RED'}
 $tests=Join-Path $repo 'tests/Apex.Tests/bin/Release/net8.0'
 $closure=Join-Path $e 'local-closure';New-Item -ItemType Directory -Path $closure|Out-Null
 Copy-Item $tests (Join-Path $closure 'tests') -Recurse
 Copy-Item (Join-Path $repo 'src/Apex.Desktop/bin/Release/net8.0') (Join-Path $closure 'host') -Recurse
 # A dedicated metadata-only SDK probe; NEVER invoke a default Desktop or generic tester.
 $probe=Join-Path $repo 'tools/WindowsLocalMetadata/WindowsLocalMetadata.csproj'
 & dotnet build $probe -c Release -p:NuGetAudit=false *> (Join-Path $e 'metadata-build.log')
 if($LASTEXITCODE -ne 0){throw 'Metadata probe compile failed'}
 & dotnet (Join-Path $repo 'tools/WindowsLocalMetadata/bin/Release/net8.0/WindowsLocalMetadata.dll') (Join-Path $tests 'Apex.Tests.dll') *> (Join-Path $e 'entry-metadata.json')
 if($LASTEXITCODE -ne 0){throw 'Not the admitted friend entry; no fallback'}
 $old=$ErrorActionPreference
 try {$ErrorActionPreference='Continue'; & dotnet test $project -c Release --no-build --no-restore --filter 'Gate=WindowsLocalExecution&FullyQualifiedName!~Windows_local_first_run_creates_owner' --logger 'trx;LogFileName=local.trx' --results-directory $e -- xUnit.ParallelizeTestCollections=false *> (Join-Path $e 'local-tests.log');$code=$LASTEXITCODE}finally{$ErrorActionPreference=$old}
 [ordered]@{phase=$Phase;exit=$code;syntheticOnly=$true;privateBroker=$false;actualLinuxTransfer=$false;windows11=$false}|ConvertTo-Json|Set-Content (Join-Path $e 'local-summary.json')
 if($code -ne 0){throw 'Native local execution assertions failed; exact closure and TRX retained'}
}finally{Pop-Location}
