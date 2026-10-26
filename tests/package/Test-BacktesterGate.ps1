param([Parameter(Mandatory=$true)][string]$Source, [Parameter(Mandatory=$true)][string]$Scratch)
$ErrorActionPreference = 'Stop'
. (Join-Path $Source 'tools/windows/TestEvidence.ps1')
New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
$fixture = Join-Path $Scratch 'controlled-trx.xml'
function Write-Fixture([int]$total, [int]$passed, [int]$failed) {
    [IO.File]::WriteAllText($fixture, "<TestRun><Results><UnitTestResult executionId='one' outcome='Passed'/></Results><ResultSummary><Counters total='$total' executed='$total' passed='$passed' failed='$failed' error='0' timeout='0' aborted='0' inconclusive='0' notExecuted='0'/></ResultSummary></TestRun>")
}
Write-Fixture 1 1 0
$c = Read-ApexTestCounts $fixture
if ($c.total -ne 1 -or $c.passed -ne 1) { throw 'Wrong fixture counts' }
foreach ($values in @(@(0,0,0), @(1,0,1), @(2,2,0))) {
    Write-Fixture $values[0] $values[1] $values[2]
    $rejected = $false
    try { $null = Read-ApexTestCounts $fixture } catch { $rejected = $true }
    if (-not $rejected) { throw 'Accepted empty/failed/inconsistent TRX fixture' }
}
# Controlled package fixture tests integrity/metadata enforcement only; dummy EXE is never executed.
$package = Join-Path $Scratch 'controlled-package'
New-Item -ItemType Directory -Force -Path $package | Out-Null
foreach ($required in @('APEX-Desktop/ApexDesktop.exe','APEX-Desktop/wwwroot/index.html','APEX-Desktop/wwwroot/app.js','APEX-Desktop/wwwroot/decision-center.js','cTrader/ApexBridge.algo','Uruchom APEX.cmd')) {
    $file = Join-Path $package $required
    New-Item -ItemType Directory -Force -Path (Split-Path $file) | Out-Null
    [IO.File]::WriteAllText($file, 'controlled fixture, not product executable')
}
$evidence = Join-Path $package 'Weryfikacja'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
Write-Fixture 1 1 0
foreach ($suite in @('bot','platform','backtester')) { Copy-Item $fixture (Join-Path $evidence ($suite + '.trx')) -Force }
$meta = @{ demoOnly=$true; botTestsRun=$true; platformTestsRun=$true; backtesterTestsRun=$true; panelTestsRun=$true;
    testCounts=@{bot=@{total=1;passed=1;failed=0};platform=@{total=1;passed=1;failed=0};backtester=@{total=1;passed=1;failed=0}} }
function Write-PackageManifest {
    [IO.File]::WriteAllText((Join-Path $package 'apex-build.json'), ($meta | ConvertTo-Json -Depth 5))
    $prefix = $package.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $hashes = @(Get-ChildItem $package -Recurse -File | Where-Object { $_.Name -ne 'manifest.sha256' } | ForEach-Object {
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.FullName.Substring($prefix.Length).Replace('\','/')
    })
    [IO.File]::WriteAllLines((Join-Path $package 'manifest.sha256'),$hashes)
}
Write-PackageManifest
& (Join-Path $Source 'tools/windows/Sprawdz-Pakiet.ps1') -Pakiet $package
# The caller may use forward/mixed separators on Windows; verify the same physical package.
$alternatePath = $package.Replace([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::AltDirectorySeparatorChar
& (Join-Path $Source 'tools/windows/Sprawdz-Pakiet.ps1') -Pakiet $alternatePath
# Canonicalizing the root must not weaken the manifest containment boundary.
[IO.File]::WriteAllText((Join-Path $package 'manifest.sha256'), (('0' * 64) + '  ../outside.txt'))
$rejected = $false
try { & (Join-Path $Source 'tools/windows/Sprawdz-Pakiet.ps1') -Pakiet $package } catch {
    if ($_.Exception.Message -notmatch 'Niedozwolona sciezka manifestu') { throw }
    $rejected = $true
}
if (-not $rejected) { throw 'Verifier accepted parent traversal' }
Write-PackageManifest
foreach ($fault in @('missing-flag','wrong-count','missing-trx')) {
    $meta.backtesterTestsRun = $fault -ne 'missing-flag'
    $meta.testCounts.backtester.total = 1
    if ($fault -eq 'wrong-count') { $meta.testCounts.backtester.total = 2 }
    if ($fault -eq 'missing-trx') { Remove-Item (Join-Path $evidence 'backtester.trx') }
    Write-PackageManifest
    $rejected = $false
    try { & (Join-Path $Source 'tools/windows/Sprawdz-Pakiet.ps1') -Pakiet $package } catch { $rejected=$true }
    if (-not $rejected) { throw "Verifier accepted $fault" }
}
Write-Output 'PASS package gates: 10 controlled fixture cases (4 TRX, 6 package); these are not product test totals or Windows runtime proof'
