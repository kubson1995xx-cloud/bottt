# Task2 bounded source/generator gate; never publishes a Windows delivery or starts a host.
param([ValidateSet('red','green')][string]$Phase='green')
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Native Windows required' }
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $repo
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$evidence=Join-Path $env:RUNNER_TEMP ('apex-task2-source-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)
New-Item -ItemType Directory -Path $evidence | Out-Null
$env:DOTNET_HOST_PATH=(Get-Command dotnet).Source
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Save-Map([string]$Name,[string]$Reason,[string[]]$Paths) {
    $files=@($Paths|Sort-Object -Unique|ForEach-Object { if (Test-Path -LiteralPath $_ -PathType Leaf) { [ordered]@{path=$_;bytes=(Get-Item -LiteralPath $_).Length;sha256=(Hash $_)} } })
    [ordered]@{schema='apex-task2-source-v1';sourceCommit=(& git rev-parse HEAD);sourceDirty=[bool](& git status --porcelain);reason=$Reason;phase=$Phase;sourceOnly=$true;packageTested=$false;brokerTested=$false;osCaption=(Get-CimInstance Win32_OperatingSystem).Caption;powershell=$PSVersionTable.PSVersion.ToString();sdk=(& dotnet --version);files=$files}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence $Name) -Encoding UTF8
}
Save-Map 'source-map.json' 'Before native Task2 build and controlled presentation gates; no host or package acceptance' @((& git ls-files)+(& git ls-files --others --exclude-standard))
function Dotnet([string]$Label,[string[]]$Arguments) {
    $ErrorActionPreference='Continue'
    & dotnet @Arguments *> (Join-Path $evidence ($Label+'.log'))
    $code=$LASTEXITCODE
    if ($code -ne 0) { throw ('Native dotnet gate failed: '+$Label+' exit '+$code) }
}
$oldPresentation=$env:APEX_PRESENTATION_TEST_DIRECTORY
try {
    Dotnet 'build-producer' @('build','tools/Apex.Backtester/Apex.Backtester.csproj','-c','Release','-p:NuGetAudit=false')
    Dotnet 'build-source-tests' @('build','tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj','-c','Release','-p:NuGetAudit=false')
    Save-Map 'behavior-inputs.json' 'Before real CLI/control/typed parser execution on native PS5.1' @('tools/Apex.Backtester/bin/Release/net8.0/Apex.Backtester.dll','tools/Apex.Backtester/bin/Release/net8.0/Apex.Core.dll','tests/Apex.Windows.Tests/bin/Release/net8.0/Apex.Windows.Tests.dll','tests/Apex.Windows.Tests/bin/Release/net8.0/ApexDesktop.dll','tools/windows/TestEvidence.ps1','tools/windows/Zbuduj-APEX.ps1','tests/package/Test-BacktesterGate.ps1','tests/Apex.Windows.Tests/WindowsLocalSetupTests.cs','tests/Apex.Backtester.Tests/fixtures/synthetic/naval-shared-replay-v1.json')
    $controls=Join-Path $evidence 'controlled'
    & (Join-Path $repo 'tests/package/Test-BacktesterGate.ps1') -Source $repo -Scratch $controls *> (Join-Path $evidence 'controlled.log')
    Copy-Item -LiteralPath (Join-Path $controls 'results.json') -Destination (Join-Path $evidence 'controlled-results.json')
    $results=Get-Content (Join-Path $evidence 'controlled-results.json') -Raw|ConvertFrom-Json
    if (@($results).Count -le 0 -or @($results|Where-Object {-not $_.pass}).Count -gt 0) { throw 'Controlled presentation failures' }
    $env:APEX_PRESENTATION_TEST_DIRECTORY=Join-Path $controls 'real-presentation/presentation'
    Dotnet 'typed-presentation' @('test','tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj','-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~WindowsLocalSetupTests','--logger',('trx;LogFileName=presentation-'+$Phase+'.trx'),'--results-directory',$evidence)
    $counts=Read-ApexTestCounts (Join-Path $evidence ('presentation-'+$Phase+'.trx'))
    # Only the public two-file output is retained with downloadable source evidence.
    Copy-Item -LiteralPath $env:APEX_PRESENTATION_TEST_DIRECTORY -Destination (Join-Path $evidence 'public-presentation') -Recurse
    [ordered]@{sourceCommit=(& git rev-parse HEAD);phase=$Phase;controlledTotal=@($results).Count;controlledPassed=@($results|Where-Object {$_.pass}).Count;typed=$counts;sourceOnly=$true;packageTested=$false;brokerTested=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding UTF8
} finally {
    $env:APEX_PRESENTATION_TEST_DIRECTORY=$oldPresentation
    # Failure controls remain on the runner; do not upload private authority/result/log directories.
    if (Test-Path -LiteralPath (Join-Path $evidence 'controlled/results.json')) { Copy-Item -LiteralPath (Join-Path $evidence 'controlled/results.json') -Destination (Join-Path $evidence 'controlled-results.json') -Force }
    Write-Output ('TASK2_EVIDENCE='+$evidence)
}
