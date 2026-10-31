# OKX Task1 native source prerequisite only. No package, secure UI or broker proof.
param([ValidateSet('red','green')][string]$Phase='green', [ValidateSet('all','windows-setup','credentials')][string]$Gate='all')
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Native Windows required' }
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$evidence=Join-Path $env:RUNNER_TEMP ('apex-okx-task1-source-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)
New-Item -ItemType Directory -Path $evidence | Out-Null
$selection=Join-Path $evidence 'sdk-selection'; New-Item -ItemType Directory -Path $selection | Out-Null
[IO.File]::WriteAllText((Join-Path $selection 'global.json'),'{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
$dotnet=(Get-Command dotnet -CommandType Application).Source
$env:DOTNET_HOST_PATH=$dotnet
$oldPresentation=$env:APEX_PRESENTATION_TEST_DIRECTORY
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Save-Map([string]$Name,[string]$Reason,[string[]]$Paths) {
    $files=@($Paths|Sort-Object -Unique|ForEach-Object { $p=Join-Path $repo $_; if (Test-Path -LiteralPath $p -PathType Leaf) { [ordered]@{path=$_;bytes=(Get-Item -LiteralPath $p).Length;sha256=(Hash $p)} } })
    if ($files.Count -eq 0) { throw 'Empty input map' }
    [ordered]@{schema='apex-okx-task1-source-v1';sourceCommit=(& git -C $repo rev-parse HEAD);reason=$Reason;phase=$Phase;sourceOnly=$true;packageTested=$false;secureUiTested=$false;brokerTested=$false;osCaption=(Get-CimInstance Win32_OperatingSystem).Caption;powershell=$PSVersionTable.PSVersion.ToString();sdk=(& $dotnet --version);files=$files}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence $Name) -Encoding UTF8
}
function Invoke-ApexSourceDotnet([string]$Label,[string[]]$Arguments) {
    $old=$ErrorActionPreference
    try { $ErrorActionPreference='Continue'; & $dotnet @Arguments *> (Join-Path $evidence ($Label+'.log')); $code=$LASTEXITCODE } finally { $ErrorActionPreference=$old }
    return $code
}
Push-Location $selection
try {
    if ((& $dotnet --version) -cne '8.0.427') { throw 'Exact SDK8 selection failed' }
    Save-Map 'source-map.json' 'Before build: hashes of actual Windows checkout bytes including CRLF conversion, not Git LF blobs' @((& git -C $repo ls-files)+(& git -C $repo ls-files --others --exclude-standard))
    Push-Location (Join-Path $repo 'frontend/naval')
    try {
        $old=$ErrorActionPreference; $ErrorActionPreference='Continue'
        & npm ci --ignore-scripts --no-audit --no-fund *> (Join-Path $evidence 'frontend-ci.log'); $install=$LASTEXITCODE
        & npm run build *> (Join-Path $evidence 'frontend-build.log'); $build=$LASTEXITCODE
        $ErrorActionPreference=$old
        if ($install -ne 0 -or $build -ne 0) { throw 'Existing pinned frontend staging prerequisite failed' }
    } finally { Pop-Location }
    $project=Join-Path $repo 'tests/Apex.Tests/Apex.Tests.csproj'
    if ($Gate -ne 'windows-setup' -and (Invoke-ApexSourceDotnet 'build' @('build',$project,'-c','Release','-p:NuGetAudit=false')) -ne 0) { throw 'Source setup/compile failure, not behavioral RED' }
    if ($Phase -eq 'green' -and $Gate -ne 'credentials') {
        foreach ($pair in @(@('producer','tools/Apex.Backtester/Apex.Backtester.csproj'),@('windows','tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj'))) {
            if ((Invoke-ApexSourceDotnet ('build-'+$pair[0]) @('build',(Join-Path $repo $pair[1]),'-c','Release','-p:NuGetAudit=false')) -ne 0) { throw ('Affected source prerequisite failed: '+$pair[0]) }
        }
        $private=Join-Path $env:RUNNER_TEMP ('apex-okx-task1-private-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)
        $null=New-ApexPresentation $repo $private
        $env:APEX_PRESENTATION_TEST_DIRECTORY=Join-Path $private 'presentation'
        Copy-Item -LiteralPath $env:APEX_PRESENTATION_TEST_DIRECTORY -Destination (Join-Path $repo 'tests/Apex.Windows.Tests/bin/Release/net8.0/presentation') -Recurse
        Save-Map 'presentation-inputs.json' 'Before source setup: genuine existing producer presentation fixture, no private state uploaded' @('tests/Apex.Windows.Tests/bin/Release/net8.0/presentation/presentation.json','tests/Apex.Windows.Tests/bin/Release/net8.0/presentation/naval-publication.json')
    }
    Save-Map 'behavior-inputs.json' 'Before tests: actual executed binary and staged asset bytes; private fixtures never uploaded' @(Get-ChildItem -LiteralPath (Join-Path $repo 'src/Apex.Desktop/bin/Release/net8.0') -File -Recurse | ForEach-Object { $_.FullName.Substring($repo.Length+1) })
    $filter='FullyQualifiedName~OkxConfigTests|FullyQualifiedName~OkxCompositionTests|FullyQualifiedName~OkxActivationTests|FullyQualifiedName~OkxCredentialTests|FullyQualifiedName~OkxNativeCredentialTests'
    if ($Gate -eq 'credentials') { $filter='FullyQualifiedName~OkxCredentialTests|FullyQualifiedName~OkxNativeCredentialTests' }
    $code=0
    if ($Gate -ne 'windows-setup') {
        Save-Map 'test-inputs.json' 'Before tests: compiled test bytes' @('tests/Apex.Tests/bin/Release/net8.0/Apex.Tests.dll','tests/Apex.Tests/bin/Release/net8.0/ApexDesktop.dll')
        $code=Invoke-ApexSourceDotnet 'okx' @('test',$project,'-c','Release','--no-build','--no-restore','--filter',$filter,'--logger',('trx;LogFileName=okx-'+$Phase+'.trx'),'--results-directory',$evidence)
    }
    $windowsExit=0; $affectedExit=0
    if ($Phase -eq 'green' -and $Gate -ne 'credentials') {
        Save-Map 'affected-inputs.json' 'Before affected native setup/startup and portable access/config/durability regressions' @('tests/Apex.Windows.Tests/bin/Release/net8.0/Apex.Windows.Tests.dll','tests/Apex.Windows.Tests/bin/Release/net8.0/ApexDesktop.dll','tools/Apex.Backtester/bin/Release/net8.0/Apex.Backtester.dll')
        $windowsExit=Invoke-ApexSourceDotnet 'windows-affected' @('test',(Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj'),'-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~WindowsLocalSetupTests|FullyQualifiedName~WindowsStartupTests','--logger','trx;LogFileName=windows-affected.trx','--results-directory',$evidence)
        if ($Gate -eq 'all') { $affectedExit=Invoke-ApexSourceDotnet 'portable-affected' @('test',$project,'-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~NavalReadAccessTests|FullyQualifiedName~NavalCashDurabilityTests|FullyQualifiedName~DesktopSafetyTests|FullyQualifiedName~FreshInstallationCanRestartBeforeReadOnlySetupWithoutManualFileEdits','--logger','trx;LogFileName=portable-affected.trx','--results-directory',$evidence) }
    }
    [ordered]@{sourceCommit=(& git -C $repo rev-parse HEAD);phase=$Phase;gate=$Gate;okxExecuted=($Gate -ne 'windows-setup');credentialsOnly=($Gate -eq 'credentials');affectedExecuted=($Gate -eq 'all' -and $Phase -eq 'green');windowsExecuted=($Phase -eq 'green' -and $Gate -ne 'credentials');exit=$code;windowsExit=$windowsExit;affectedExit=$affectedExit;filter=$filter;sourceOnly=$true;packageTested=$false;secureUiTested=$false;brokerTested=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding UTF8
    if ($code -ne 0) { throw 'Native source tests failed; RED is retained as failure' }
    if ($Gate -ne 'windows-setup') { $null=Read-ApexTestCounts (Join-Path $evidence ('okx-'+$Phase+'.trx')) }
    if ($Phase -eq 'green' -and $Gate -ne 'credentials') {
        if ($windowsExit -ne 0 -or $affectedExit -ne 0) { throw 'Affected source regressions failed' }
        $null=Read-ApexTestCounts (Join-Path $evidence 'windows-affected.trx')
        if ($Gate -eq 'all') { $null=Read-ApexTestCounts (Join-Path $evidence 'portable-affected.trx') }
    }
} finally { $env:APEX_PRESENTATION_TEST_DIRECTORY=$oldPresentation; Pop-Location; Write-Output ('OKX_TASK1_EVIDENCE='+$evidence) }
