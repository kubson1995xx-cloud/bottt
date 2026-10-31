#Requires -Version 5.1
# Full fresh engineering candidate. Parent independent review remains mandatory.
param([Parameter(Mandatory=$true)][string]$OriginalDownload)
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion.Major -ne 5) { throw 'Native Windows PowerShell5.1 required' }
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$output=Join-Path $env:RUNNER_TEMP 'APEX-NAVAL-COMMAND-Windows-x64'
$package=$output+'-extracted'
$e=Join-Path $env:RUNNER_TEMP ('apex-final-'+$env:GITHUB_RUN_ID+'-'+$env:GITHUB_RUN_ATTEMPT)
$public=Join-Path $env:RUNNER_TEMP 'final-public-evidence'
New-Item -ItemType Directory -Path $e,$public|Out-Null
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string]$p,$o) { [IO.File]::WriteAllText($p,($o|ConvertTo-Json -Depth 15),[Text.UTF8Encoding]::new($false)) }
$commit=(& git -C $repo rev-parse HEAD).Trim()
$os=Get-CimInstance Win32_OperatingSystem
$names=@(& git -C $repo ls-files)+@(& git -C $repo ls-files --others --exclude-standard)
$files=@($names|Sort-Object -Unique|ForEach-Object {$p=Join-Path $repo $_;[ordered]@{path=$_;bytes=(Get-Item -LiteralPath $p).Length;sha256=(Hash $p)}})
Json (Join-Path $e 'source-before.json') ([ordered]@{sourceCommit=$commit;sourceDirty=[bool](& git -C $repo status --porcelain);reason='Before fresh mandatory full builder, extracted native Package/Browser and isolated smoke; no reused runtime';osCaption=$os.Caption;osVersion=$os.Version;osArchitecture=$os.OSArchitecture;powershell=$PSVersionTable.PSVersion.ToString();node=(& node --version);npm=(& npm --version);files=$files})
$selection=Join-Path $e 'sdk';New-Item -ItemType Directory -Path $selection|Out-Null
$dotnet=(Get-Command dotnet).Source
$sdk=@(& $dotnet --list-sdks|ForEach-Object {($_ -split ' ')[0]}|Where-Object {$_ -match '^8\.0\.\d+$'}|Sort-Object {[version]$_}|Select-Object -Last 1)
if ($sdk.Count -ne 1) { throw 'Installed SDK8 absent' }
Json (Join-Path $selection 'global.json') @{sdk=@{version=$sdk[0];rollForward='disable'}}
$passed=$false;$failure=$null
Push-Location $selection
try {
    $actualSdk=& $dotnet --version
    if ($actualSdk -cne $sdk[0]) { throw 'Actual SDK8 selection failed' }
    & (Join-Path $repo 'tests/package/Test-PublishScalarRegression.ps1') -OriginalZip (Join-Path $OriginalDownload 'APEX-NAVAL-COMMAND-Windows-x64.zip') -Evidence (Join-Path $e 'scalar-replay')
    & (Join-Path $repo 'tests/package/Test-WindowsRuntimeOrigins.ps1') -Source $repo -EvidencePath (Join-Path $e 'runtime-origin-controls.json')
    # The mandatory builder freshly builds all source/SDK suites and then invokes
    # Test-NavalPackage.ps1: explicit APEX_PACKAGE_ROOT=$package, Gate=Package and
    # Gate=Browser (6+2), no task6 pinned extractor and no prior TRX reuse.
    & (Join-Path $PSScriptRoot 'Zbuduj-APEX.ps1') -Target win-x64 -Wyjscie $output
    & (Join-Path $PSScriptRoot 'Smoke-Windows.ps1') -Pakiet $package -Evidence ($output+'-smoke-evidence')
    $p=Get-Content (Join-Path ($output+'-package-evidence') 'summary.json') -Raw|ConvertFrom-Json
    $s=Get-Content (Join-Path ($output+'-smoke-evidence') 'summary.json') -Raw|ConvertFrom-Json
    $m=Get-Content (Join-Path $package 'apex-build.json') -Raw|ConvertFrom-Json
    $zipHash=Hash ($output+'.zip')
    if ($p.status -cne 'PASS' -or $s.status -cne 'PASS' -or -not $p.packageUnchanged -or $p.zipSha256 -cne $zipHash -or $m.sourceCommit -cne $commit -or $m.sourceDirty -or $m.tools.dotnet -cne $actualSdk -or $m.target -cne 'win-x64' -or $m.deploymentMode -cne 'self-contained') { throw 'Fresh candidate identity/summary invalid' }
    $counts=[ordered]@{}
    foreach ($suite in @('bot','platform','backtester','windows')) { $counts[$suite]=Read-ApexTestCounts (Join-Path $package ('Weryfikacja/'+$suite+'.trx')) }
    $counts.packaged=Read-ApexTestCounts (Join-Path ($output+'-package-evidence') 'packaged.trx')
    foreach ($gate in @('Package','Browser')) {
        $c=Read-ApexTestCounts (Join-Path ($output+'-package-evidence') ('windows-'+$gate.ToLowerInvariant()+'.trx'))
        $expected=6;if($gate -eq 'Browser'){$expected=2}
        if ($c.total -ne $expected -or $p.nativeGates.$gate.exitCode -ne 0) { throw 'Native extracted gate incomplete' }
        $counts[$gate]=$c
    }
    & (Join-Path $package 'Sprawdz-Pakiet.ps1') -Pakiet $package
    if ((Hash ($output+'.zip')) -cne $zipHash) { throw 'ZIP changed after native acceptance' }
    Json (Join-Path $e 'candidate.json') ([ordered]@{status='PASS';kind='fresh-full-windows-candidate-pending-independent-review';sourceCommit=$commit;sourceInputsSha256=$m.sourceInputsSha256;runId=$env:GITHUB_RUN_ID;sdk=$actualSdk;osCaption=$os.Caption;osVersion=$os.Version;powershell=$PSVersionTable.PSVersion.ToString();counts=$counts;zipBytes=(Get-Item ($output+'.zip')).Length;zipSha256=$zipHash;nativeFirstRunAndBrowserFresh=$true;brokerTested=$false;physicalWindows11Tested=$false;hardwareGpuTested=$false})
    $passed=$true
} catch {
    $failure=[ordered]@{type=$_.Exception.GetType().FullName;line=$_.InvocationInfo.ScriptLineNumber;script=[IO.Path]::GetFileName($_.InvocationInfo.ScriptName)}
    throw
} finally {
    Pop-Location
    Json (Join-Path $e 'driver-result.json') @{status=$(if($passed){'PASS'}else{'FAIL'});failure=$failure;sourceCommit=$commit;sdk=$actualSdk}
    # Export ONLY public producer-owned records, maps and sanitized TRX/TAP. Never
    # recurse through private fixtures, raw host output, access maps or DPAPI files.
    $groups=@(
        @{root=$e;name='driver';patterns=@('source-before.json','runtime-origin-controls.json','candidate.json','driver-result.json')},
        @{root=(Join-Path $e 'scalar-replay');name='scalar';patterns=@('scalar-results.json','original-archive.json')},
        @{root=($output+'-build-evidence');name='build';patterns=@('*.json')},
        @{root=(Join-Path $output 'Weryfikacja');name='sealed';patterns=@('*.json','*.trx','frontend-*.txt')},
        @{root=($output+'-package-evidence');name='package';patterns=@('*.json','*.trx','*.sha256')},
        @{root=(Join-Path ($output+'-package-evidence') 'host');name='host';patterns=@('process-*.json')},
        @{root=($output+'-smoke-evidence');name='smoke';patterns=@('summary.json')}
    )
    foreach ($g in $groups) {
        if (-not (Test-Path -LiteralPath $g.root)) { continue }
        $dest=Join-Path $public $g.name;New-Item -ItemType Directory -Path $dest|Out-Null
        foreach ($pattern in $g.patterns) { foreach ($file in Get-ChildItem -LiteralPath $g.root -File -Filter $pattern) {
            $text=[IO.File]::ReadAllText($file.FullName)
            if ($text -match '(?i)"(?:OperatorSecret|ReaderSecret|Password|TokenDigest|SecretDigest)"\s*:' -or $text -match '(?i)Authorization:\s*(?:Basic|Bearer)\s+\S+') { throw 'Public evidence rejected: private field/header' }
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $dest $file.Name)
        } }
    }
    $index=@(Get-ChildItem -LiteralPath $public -Recurse -File|Sort-Object FullName|ForEach-Object {[ordered]@{path=$_.FullName.Substring($public.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Hash $_.FullName)}})
    Json (Join-Path $public 'evidence-index.json') @{sourceCommit=$commit;files=$index;scope='Whitelisted sanitized public evidence; native known-value prepublication checks remain in C# privacy records'}
}
