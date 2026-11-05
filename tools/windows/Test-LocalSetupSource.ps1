# Task3 source-only native ACL/DPAPI/setup + existing production auth process gate.
param([ValidateSet('red','green')][string]$Phase='green')
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Native Windows required' }
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$evidence=Join-Path $env:RUNNER_TEMP ('apex-task3-source-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)
New-Item -ItemType Directory -Path $evidence | Out-Null
# Select SDK8 locally, not merely setup-dotnet installing it alongside SDK10.
$sdk=(& dotnet --list-sdks | Where-Object { $_ -match '^8\.0\.\d+ ' } | Select-Object -Last 1).Split(' ')[0]
if ($sdk -notmatch '^8\.0\.\d+$') { throw 'SDK8 required' }
$selection=Join-Path $evidence 'sdk-selection'; New-Item -ItemType Directory -Path $selection | Out-Null
[IO.File]::WriteAllText((Join-Path $selection 'global.json'),('{"sdk":{"version":"'+$sdk+'","rollForward":"disable"}}'))
Push-Location $selection
$env:DOTNET_HOST_PATH=(Get-Command dotnet).Source
$oldPresentation=$env:APEX_PRESENTATION_TEST_DIRECTORY
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Save-Map([string]$Name,[string]$Reason,[string[]]$Paths) {
    $files=@($Paths|Sort-Object -Unique|ForEach-Object { $p=Join-Path $repo $_; if (Test-Path -LiteralPath $p -PathType Leaf) { [ordered]@{path=$_;bytes=(Get-Item -LiteralPath $p).Length;sha256=(Hash $p)} } })
    [ordered]@{schema='apex-task3-source-v1';sourceCommit=(& git -C $repo rev-parse HEAD);sourceDirty=[bool](& git -C $repo status --porcelain);reason=$Reason;phase=$Phase;sourceOnly=$true;packageTested=$false;brokerTested=$false;osCaption=(Get-CimInstance Win32_OperatingSystem).Caption;powershell=$PSVersionTable.PSVersion.ToString();sdk=(& dotnet --version);sdkSelectionSha256=(Hash (Join-Path $selection 'global.json'));files=$files}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence $Name) -Encoding UTF8
}
function Invoke-Dotnet([string]$Label,[string[]]$Arguments) {
    $old=$ErrorActionPreference
    try { $ErrorActionPreference='Continue'; & dotnet @Arguments *> (Join-Path $evidence ($Label+'.log')); $code=$LASTEXITCODE } finally { $ErrorActionPreference=$old }
    return $code
}
try {
    if ((& dotnet --version) -notmatch '^8\.0\.') { throw 'SDK8 selection failed' }
    Save-Map 'source-map.json' 'Before Task3 native build; SDK8 selected in external working directory; no package/UI/browser acceptance' @((& git -C $repo ls-files)+(& git -C $repo ls-files --others --exclude-standard))
    Push-Location (Join-Path $repo 'frontend/naval')
    try {
        $old=$ErrorActionPreference; $ErrorActionPreference='Continue'
        & npm ci --ignore-scripts --no-audit --no-fund *> (Join-Path $evidence 'frontend-ci.log'); $install=$LASTEXITCODE
        & npm run build *> (Join-Path $evidence 'frontend-build.log'); $build=$LASTEXITCODE
        $ErrorActionPreference=$old
        if ($install -ne 0 -or $build -ne 0) { throw 'Existing pinned frontend prerequisite failed' }
    } finally { Pop-Location }
    foreach ($pair in @(@('producer','tools/Apex.Backtester/Apex.Backtester.csproj'),@('windows','tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj'),@('existing','tests/Apex.Tests/Apex.Tests.csproj'))) {
        if ((Invoke-Dotnet ('build-'+$pair[0]) @('build',(Join-Path $repo $pair[1]),'-c','Release','-p:NuGetAudit=false')) -ne 0) { throw ('Build failed: '+$pair[0]) }
    }
    Save-Map 'behavior-inputs.json' 'Before actual setup/DPAPI and production process tests; native modal adapter injected privately, actual UI deferred Task6' @('tests/Apex.Windows.Tests/bin/Release/net8.0/Apex.Windows.Tests.dll','tests/Apex.Windows.Tests/bin/Release/net8.0/ApexDesktop.dll','tests/Apex.Tests/bin/Release/net8.0/Apex.Tests.dll','src/Apex.Desktop/bin/Release/net8.0/ApexDesktop.dll','tools/Apex.Backtester/bin/Release/net8.0/Apex.Backtester.dll','tools/Apex.Backtester/bin/Release/net8.0/Apex.Core.dll','tools/windows/TestEvidence.ps1','tools/windows/Test-LocalSetupSource.ps1','tests/Apex.Backtester.Tests/fixtures/synthetic/naval-shared-replay-v1.json')
    $private=Join-Path $env:RUNNER_TEMP ('apex-task3-private-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)
    $null=New-ApexPresentation $repo $private
    $env:APEX_PRESENTATION_TEST_DIRECTORY=Join-Path $private 'presentation'
    Copy-Item -LiteralPath $env:APEX_PRESENTATION_TEST_DIRECTORY -Destination (Join-Path $evidence 'public-presentation') -Recurse
    Copy-Item -LiteralPath $env:APEX_PRESENTATION_TEST_DIRECTORY -Destination (Join-Path $repo 'tests/Apex.Windows.Tests/bin/Release/net8.0/presentation') -Recurse
    Save-Map 'native-inputs.json' 'Before setup/DPAPI/process execution, including staged genuine presentation and frontend bytes' @((& git -C $repo ls-files)+@('tests/Apex.Windows.Tests/bin/Release/net8.0/presentation/presentation.json','tests/Apex.Windows.Tests/bin/Release/net8.0/presentation/naval-publication.json')+@(Get-ChildItem -LiteralPath (Join-Path $repo 'src/Apex.Desktop/bin/Release/net8.0/wwwroot') -File -Recurse | ForEach-Object { $_.FullName.Substring($repo.Length+1) }))
    $setup=Invoke-Dotnet 'setup' @('test',(Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj'),'-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~WindowsLocalSetupTests','--logger',('trx;LogFileName=setup-'+$Phase+'.trx'),'--results-directory',$evidence)
    $access=Invoke-Dotnet 'access' @('test',(Join-Path $repo 'tests/Apex.Tests/Apex.Tests.csproj'),'-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~NavalReadAccess|FullyQualifiedName~NavalFrontendHost','--logger',('trx;LogFileName=access-'+$Phase+'.trx'),'--results-directory',$evidence)
    [ordered]@{sourceCommit=(& git -C $repo rev-parse HEAD);phase=$Phase;setupExit=$setup;accessExit=$access;sourceOnly=$true;packageTested=$false;nativeUiTested=$false;brokerTested=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding UTF8
    if ($setup -ne 0 -or $access -ne 0) { throw 'Native source tests failed; RED is failure, not waived' }
    $null=Read-ApexTestCounts (Join-Path $evidence ('setup-'+$Phase+'.trx'))
    $null=Read-ApexTestCounts (Join-Path $evidence ('access-'+$Phase+'.trx'))
} finally {
    $env:APEX_PRESENTATION_TEST_DIRECTORY=$oldPresentation
    Pop-Location
    Write-Output ('TASK3_EVIDENCE='+$evidence)
    # No private config/map/blob, authority, raw credential/modal logs or screenshots uploaded.
}
