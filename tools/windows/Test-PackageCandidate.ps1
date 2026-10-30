#Requires -Version 5.1
# Task5 engineering candidate only. Default full-delivery remains a separate lane.
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Native Windows required' }
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$evidence=Join-Path $env:RUNNER_TEMP ('apex-task5-package-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)
New-Item -ItemType Directory -Path $evidence | Out-Null
$names=@(& git -C $repo ls-files)+@(& git -C $repo ls-files --others --exclude-standard)
$files=@($names|Sort-Object -Unique|ForEach-Object { $p=Join-Path $repo $_;[ordered]@{path=$_;bytes=(Get-Item -LiteralPath $p).Length;sha256=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()} })
$os=Get-CimInstance Win32_OperatingSystem
[ordered]@{sourceCommit=(& git -C $repo rev-parse HEAD);sourceDirty=[bool](& git -C $repo status --porcelain);reason='Before Task5 genuine full builder and isolated legacy EXE smoke; not Task6/7 acceptance';osCaption=$os.Caption;osVersion=$os.Version;osArchitecture=$os.OSArchitecture;files=$files}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence 'source-before.json') -Encoding UTF8
$output=Join-Path $env:RUNNER_TEMP 'APEX-NAVAL-COMMAND-Windows-x64'
try {
    & (Join-Path $PSScriptRoot 'Zbuduj-APEX.ps1') -Target win-x64 -Wyjscie $output
    & (Join-Path $PSScriptRoot 'Smoke-Windows.ps1') -Pakiet ($output+'-extracted') -Evidence ($output+'-smoke-evidence')
    $smoke=Get-Content -LiteralPath (Join-Path ($output+'-smoke-evidence') 'summary.json') -Raw|ConvertFrom-Json
    $package=Get-Content -LiteralPath (Join-Path ($output+'-package-evidence') 'summary.json') -Raw|ConvertFrom-Json
    if ($smoke.status -cne 'PASS' -or $package.status -cne 'PASS') { throw 'External candidate gates not green' }
    $meta=Get-Content -LiteralPath (Join-Path $output 'apex-build.json') -Raw|ConvertFrom-Json
    if ($meta.sourceCommit -cne (& git -C $repo rev-parse HEAD) -or $meta.sourceDirty -or $meta.windowsRuntimeTested -or $meta.brokerTested) { throw 'Candidate sealed identity invalid' }
    $counts=[ordered]@{}
    foreach ($suite in @('bot','platform','backtester','windows')) { $counts[$suite]=Read-ApexTestCounts (Join-Path $output ('Weryfikacja/'+$suite+'.trx')) }
    [ordered]@{status='PASS';kind='engineering-candidate-not-end-user-accepted';sourceCommit=$meta.sourceCommit;sourceInputsSha256=$meta.sourceInputsSha256;sdk=$meta.tools.dotnet;osCaption=$os.Caption;counts=$counts;zipSha256=(Get-FileHash -LiteralPath ($output+'.zip') -Algorithm SHA256).Hash.ToLowerInvariant();firstRunModalTested=$false;defaultShellTested=$false;browserAccepted=$false;brokerTested=$false}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence 'candidate.json') -Encoding UTF8
} finally {
    # Only whitelisted public maps/results are uploaded. Private build scratch, raw
    # host output, access maps, DPAPI, configurations and authority stay unuploaded.
    Write-Host ('TASK5_EVIDENCE='+$evidence)
}
