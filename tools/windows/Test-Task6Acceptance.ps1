#Requires -Version 5.1
# Nonshipped Task6 new/missing acceptance only on the exact accepted Task5 ZIP.
param([Parameter(Mandatory=$true)][string]$Download,[Parameter(Mandatory=$true)][string]$PriorEvidence)
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Actual Windows required' }
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$e=Join-Path $env:RUNNER_TEMP ('apex-task6-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)
New-Item -ItemType Directory -Path $e|Out-Null
$package=Join-Path $env:RUNNER_TEMP 'task6-extracted'
$zip=Join-Path $Download 'APEX-NAVAL-COMMAND-Windows-x64.zip'
$files=@(@(& git -C $repo ls-files)+@(& git -C $repo ls-files --others --exclude-standard)|Sort-Object -Unique|ForEach-Object {$p=Join-Path $repo $_;[ordered]@{path=$_;bytes=(Get-Item -LiteralPath $p).Length;sha256=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}})
[ordered]@{testCommit=(& git -C $repo rev-parse HEAD);testDirty=[bool](& git -C $repo status --porcelain);productCommit='eed2fdf0bb4625260025e52a06e257c00fa38299';productRun=41167079728;productArtifact=13030195574;reason='Only new/missing Task6 acceptance; immutable approved Task5 payload. No unchanged full suite or runtime rebuild.';files=$files;osCaption=(Get-CimInstance Win32_OperatingSystem).Caption;ps=$PSVersionTable.PSVersion.ToString()}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $e 'source-before.json') -Encoding UTF8
$oldRoot=$env:APEX_PACKAGE_ROOT;$oldEvidence=$env:APEX_WINDOWS_EVIDENCE
$before=@();$gates=[ordered]@{};$passed=$false;$failure=$null
function Inventory { $root=(Get-Item -LiteralPath $package).FullName;@(Get-ChildItem -LiteralPath $root -Force -Recurse -File|Sort-Object FullName|ForEach-Object {$_.FullName.Substring($root.Length+1).Replace('\','/')+' '+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}) }
try {
    & python (Join-Path $repo 'tests/package/extract_task6.py') $zip $package (Join-Path $e 'archive-readback.json')
    if ($LASTEXITCODE -ne 0) { throw 'Exact artifact extraction failed' }
    & (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package
    $before=Inventory;[IO.File]::WriteAllLines((Join-Path $e 'package-before.sha256'),$before)
    # Current test source vs packaged test evidence are distinct. Confirm all actual
    # product/asset inputs equal the sealed source map before compiling the tests.
    $inputMap=Get-Content (Join-Path $package 'Weryfikacja/source-inputs.json') -Raw|ConvertFrom-Json
    $mapFiles=$inputMap.files
    if (-not $mapFiles) { throw 'Missing sealed input map' }
    $matched=@()
    foreach ($f in $mapFiles) {
        if ($f.path -match '^(src/|frontend/naval/|tools/Apex.Backtester/)' -and $f.path -notmatch '/bin/|/obj/|node_modules/') {
            $p=Join-Path $repo $f.path
            if (-not (Test-Path -LiteralPath $p) -or (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() -cne $f.sha256) { throw ('Product input changed; cannot reuse artifact: '+$f.path) }
            $matched+=$f
        }
    }
    if ($matched.Count -lt 20) { throw 'Suspiciously narrow product input parity' }
    [ordered]@{productCommit='eed2fdf0bb4625260025e52a06e257c00fa38299';testCommit=(& git -C $repo rev-parse HEAD);unchangedProductInputs=$matched}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $e 'product-test-map.json') -Encoding UTF8
    # Reuse the ACTUAL same-ZIP Task5 extracted-host result; no fake rerun/counts.
    $priorSummary=@(Get-ChildItem -LiteralPath $PriorEvidence -Recurse -Filter summary.json|Where-Object {$_.DirectoryName -like '*package-evidence*'})
    $priorTrx=@(Get-ChildItem -LiteralPath $PriorEvidence -Recurse -Filter packaged.trx)
    if ($priorSummary.Count -ne 1 -or $priorTrx.Count -ne 1) { throw 'Exact prior packaged evidence missing/ambiguous' }
    $prior=Get-Content $priorSummary[0].FullName -Raw|ConvertFrom-Json
    if ($prior.status -cne 'PASS' -or $prior.zipSha256 -cne '2ebcfa68834e8750831dc65877edbd07c5b1e0728e8250ba5e11868c5fc12aec') { throw 'Prior package evidence not same ZIP' }
    Copy-Item -LiteralPath $priorTrx[0].FullName -Destination (Join-Path $e 'packaged.trx')
    $reused=Read-ApexTestCounts (Join-Path $e 'packaged.trx')
    # SDK8 scoped to a throwaway SDK selection directory. No product publish.
    $selection=Join-Path $e 'sdk';New-Item -ItemType Directory -Path $selection|Out-Null
    $sdk=@(& dotnet --list-sdks|ForEach-Object {($_ -split ' ')[0]}|Where-Object {$_ -match '^8\.0\.\d+$'}|Sort-Object {[version]$_}|Select-Object -Last 1)
    if ($sdk.Count -ne 1) { throw 'SDK8 absent' }
    [IO.File]::WriteAllText((Join-Path $selection 'global.json'),(@{sdk=@{version=$sdk[0];rollForward='disable'}}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Push-Location $selection
    try {
        $actualSdk=& dotnet --version
        if ($actualSdk -notmatch '^8\.') { throw 'Not actual SDK8' }
        & dotnet restore (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj')
        if ($LASTEXITCODE -ne 0) { throw 'Test restore failed' }
        & dotnet build (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
        $env:APEX_PACKAGE_ROOT=$package;$env:APEX_WINDOWS_EVIDENCE=$e;$env:DOTNET_HOST_PATH=(Get-Command dotnet).Source
        foreach ($gate in @('Package','Browser')) {
            $trx='windows-'+$gate.ToLowerInvariant()+'.trx'
            & dotnet test (Join-Path $repo 'tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj') -c Release --no-build --no-restore --filter ('Gate='+$gate) --logger ('trx;LogFileName='+$trx) --results-directory $e
            $code=$LASTEXITCODE;$gates[$gate]=@{exitCode=$code;trx=$trx}
            # Retain partial progress even when a native UI gate fails; Browser still runs.
            $gates|ConvertTo-Json -Depth 6|Set-Content (Join-Path $e 'gate-checkpoint.json') -Encoding UTF8
            if ($code -eq 0) { $gates[$gate].counts=Read-ApexTestCounts (Join-Path $e $trx) }
        }
    } finally { Pop-Location }
    $passed=(@($gates.Values|Where-Object {$_.exitCode -ne 0}).Count -eq 0)
} catch { $failure=$_.Exception.Message;Write-Host ('TASK6_FAILED: '+$failure) }
finally {
    if (Test-Path -LiteralPath $package) {
        $after=Inventory;[IO.File]::WriteAllLines((Join-Path $e 'package-after.sha256'),$after)
        $unchanged=($before.Count -gt 0 -and ($before -join "`n") -ceq ($after -join "`n"))
        if (-not $unchanged) { $passed=$false }
        & (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package
    }
    $zipAfter=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($zipAfter -cne '2ebcfa68834e8750831dc65877edbd07c5b1e0728e8250ba5e11868c5fc12aec') { $passed=$false }
    $owned=@(Get-ChildItem -LiteralPath $e -Filter 'owned-*.json'|ForEach-Object {Get-Content $_.FullName -Raw|ConvertFrom-Json})
    $exits=@(Get-ChildItem -LiteralPath $e -Filter 'exit-*.json'|ForEach-Object {Get-Content $_.FullName -Raw|ConvertFrom-Json})
    [ordered]@{owned=$owned;exits=$exits}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $e 'windows-process.json') -Encoding UTF8
    foreach ($p in $owned) {
        $live=Get-CimInstance Win32_Process -Filter ('ProcessId='+$p.pid) -ErrorAction SilentlyContinue
        if ($live -and $live.ExecutablePath -ieq $p.exe) { Stop-Process -Id $p.pid -Force;$passed=$false }
    }
    [ordered]@{status=$(if($passed){'PASS'}else{'FAIL'});failure=$failure;testCommit=(& git -C $repo rev-parse HEAD);productCommit='eed2fdf0bb4625260025e52a06e257c00fa38299';sdk=$actualSdk;gates=$gates;reusedPackagedCounts=$reused;reusedFromRun=41167079728;productArtifact=13030195574;packageUnchanged=$unchanged;zipSha256=$zipAfter;files=@('windows-package.trx','windows-browser.trx','packaged.trx','archive-readback.json','package-before.sha256','package-after.sha256','windows-browser.json','windows-process.json');brokerTested=$false;physicalWindows11Tested=$false;hardwareGpuTested=$false}|ConvertTo-Json -Depth 9|Set-Content (Join-Path $e 'summary.json') -Encoding UTF8
    $env:APEX_PACKAGE_ROOT=$oldRoot;$env:APEX_WINDOWS_EVIDENCE=$oldEvidence
}
if (-not $passed) { throw 'Task6 acceptance failed; inspect retained individual gates' }
