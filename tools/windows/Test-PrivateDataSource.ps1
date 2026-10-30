# Source-only native gate. No package, credentials, broker endpoint or release operation.
param(
    [ValidateSet('red','green')][string]$Phase = 'green'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $repo
$evidence = Join-Path $env:RUNNER_TEMP ('apex-task1-source-' + $env:GITHUB_RUN_ID + '-' + $env:GITHUB_RUN_ATTEMPT)
New-Item -ItemType Directory -Path $evidence -ErrorAction Stop | Out-Null
$env:DOTNET_HOST_PATH = (Get-Command dotnet).Source
$inputs = @()
foreach ($name in (& git ls-files)) {
    $file = Join-Path $repo $name
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        $inputs += [ordered]@{ path=$name; sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=(Get-Item -LiteralPath $file).Length }
    }
}
[ordered]@{
    schema='apex-task1-source-v1'; reason=('Native private-data ' + $Phase + '; source only, no trading or package acceptance')
    sourceCommit=(& git rev-parse HEAD); sourceDirty=[bool](& git status --porcelain)
    phase=$Phase; osCaption=(Get-CimInstance Win32_OperatingSystem).Caption
    sdk=(& dotnet --version); powershell=$PSVersionTable.PSVersion.ToString(); inputs=$inputs
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'source-map.json') -Encoding UTF8
function Invoke-Gate([string]$label, [string[]]$arguments) {
    # Windows PowerShell 5.1 wraps native stderr as ErrorRecords. Retain it,
    # then gate on the real exit code rather than aborting evidence collection.
    $ErrorActionPreference = 'Continue'
    & dotnet @arguments *> (Join-Path $evidence ($label + '.log'))
    $nativeExit = $LASTEXITCODE
    return $nativeExit
}
function Assert-Build([string]$label, [string[]]$arguments) {
    $exit = Invoke-Gate $label $arguments
    if ($exit -ne 0) { throw ('Build/restore gate failed: ' + $label) }
}
$exitCode = 1
try {
    Assert-Build 'restore-windows' @('restore','tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj')
    Assert-Build 'build-windows' @('build','tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj','-c','Release','--no-restore')
    $privateExit = Invoke-Gate 'private-data' @('test','tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj','-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~WindowsPrivateDataTests','--logger',('trx;LogFileName=private-data-' + $Phase + '.trx'),'--results-directory',$evidence)
    Assert-Build 'restore-existing' @('restore','tests/Apex.Tests/Apex.Tests.csproj')
    Assert-Build 'build-existing' @('build','tests/Apex.Tests/Apex.Tests.csproj','-c','Release','--no-restore')
    $accessExit = Invoke-Gate 'access-native' @('test','tests/Apex.Tests/Apex.Tests.csproj','-c','Release','--no-build','--no-restore','--filter','FullyQualifiedName~NavalReadAccess','--logger','trx;LogFileName=access-native.trx','--results-directory',$evidence)
    $counts = @()
    foreach ($file in (Get-ChildItem -LiteralPath $evidence -Filter '*.trx')) {
        [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
        $c = $trx.TestRun.ResultSummary.Counters
        $counts += [ordered]@{ file=$file.Name; total=[int]$c.total; passed=[int]$c.passed; failed=[int]$c.failed; notExecuted=[int]$c.notExecuted }
        if ([int]$c.total -le 0 -or [int]$c.notExecuted -ne 0 -or [int]$c.passed -ne [int]$c.total) { $exitCode = 1 }
    }
    $exitCode = 1
    if ($privateExit -eq 0 -and $accessExit -eq 0 -and $counts.Count -eq 2 -and @($counts | Where-Object { $_.total -le 0 -or $_.passed -ne $_.total -or $_.notExecuted -ne 0 }).Count -eq 0) { $exitCode = 0 }
    [ordered]@{ sourceCommit=(& git rev-parse HEAD); phase=$Phase; privateExit=$privateExit; accessExit=$accessExit; counts=$counts; sourceOnly=$true; brokerTested=$false; packageTested=$false } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding UTF8
}
finally {
    # Uploaded evidence contains test names/assertions/counters, never private fixture files.
    foreach ($file in (Get-ChildItem -LiteralPath $evidence -File | Where-Object { $_.Extension -eq '.log' -or $_.Extension -eq '.trx' })) {
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($pair in @(@($repo,'[REPO]'),@($env:RUNNER_TEMP,'[RUNNER_TEMP]'),@($env:USERPROFILE,'[USERPROFILE]'))) {
            if ($pair[0]) { $text = $text.Replace($pair[0], $pair[1]) }
        }
        [IO.File]::WriteAllText($file.FullName, $text, (New-Object Text.UTF8Encoding($false)))
    }
    Write-Output ('TASK1_EVIDENCE=' + $evidence)
}
exit $exitCode
