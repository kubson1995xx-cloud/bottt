#Requires -Version 5.1
param([string]$Pakiet = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestEvidence.ps1')
$resolved = (Resolve-Path -LiteralPath $Pakiet).Path
# Preserve .NET canonicalization on BOTH sides, including Windows 8.3 aliases.
$canonical = [IO.Path]::GetFullPath($resolved)
$base = $canonical.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Relative([string]$p) { if ($p -cnotmatch '^[A-Za-z0-9_ .-]+(?:/[A-Za-z0-9_ .-]+)*$' -or $p -match '(^|/)(\.|\.\.)(/|$)|(^|/)[ ]|[ .](/|$)|:|\\') { throw 'Niedozwolona sciezka manifestu.' } }
$all = @((Get-Item -LiteralPath $canonical -Force)) + @(Get-ChildItem -LiteralPath $canonical -Force -Recurse)
foreach ($n in $all) { if (($n.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Package links/reparse points forbidden' } }
$meta = Get-Content -LiteralPath (Join-Path $base 'apex-build.json') -Raw | ConvertFrom-Json
if ($meta.schema -cne 'apex-build-v2' -or $meta.target -cnotin @('win-x64','linux-x64') -or $meta.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $meta.sourceDirty -isnot [bool]) { throw 'Missing/unknown current source or target metadata' }
foreach ($flag in @('demoOnly','botTestsRun','platformTestsRun','backtesterTestsRun','panelTestsRun','frontendTestsRun','frontendBuildVerified')) { if ($meta.$flag -isnot [bool] -or $meta.$flag -ne $true) { throw "Pakiet bez potwierdzonych testow DEMO: $flag" } }
foreach ($flag in @('brokerTested','windowsRuntimeTested','navalReadDefaultEnabled')) { if ($meta.$flag -isnot [bool] -or $meta.$flag -ne $false) { throw "Invalid sealed default flag: $flag" } }
if ($meta.task7Acceptance -cnotin @('pending','accepted-local-linux')) { throw 'Missing Task7 scope' }
foreach ($tool in @('node','npm','dotnet','powershell')) { if ([string]::IsNullOrWhiteSpace($meta.tools.$tool)) { throw 'Missing tool identity' } }
if ($meta.tools.node -notmatch '^v26\.') { throw 'Untested Node line' }
$required=@('APEX-Desktop/wwwroot/index.html','APEX-Desktop/wwwroot/app.js','APEX-Desktop/wwwroot/decision-center.js','APEX-Desktop/wwwroot/app.css','cTrader/ApexBridge.algo','cTrader/ApexTradingAI.algo','Dokumentacja/THIRD_PARTY_NAVAL.txt','apex-build.json','manifest.sha256','Weryfikacja/source-inputs.json','Weryfikacja/frontend-results.json')
if ($meta.target -ceq 'win-x64') {
    if ($meta.deploymentMode -cne 'self-contained' -or $meta.requiredRuntime -cne 'included') { throw 'Windows deployment metadata mismatch' }
    $required+=@('APEX-Desktop/ApexDesktop.exe','Uruchom APEX.cmd')
} else {
    if ($meta.deploymentMode -cne 'framework-dependent' -or $meta.requiredRuntime -cne 'Microsoft.AspNetCore.App 8') { throw 'Linux deployment metadata mismatch' }
    $required+=@('APEX-Desktop/ApexDesktop.dll','APEX-Desktop/ApexDesktop.deps.json','APEX-Desktop/ApexDesktop.runtimeconfig.json','Uruchom APEX.sh')
    if (Test-Path -LiteralPath (Join-Path $base 'APEX-Desktop/ApexDesktop.exe')) { throw 'Fake Windows executable on Linux target' }
    $runtimePath=Join-Path $base 'APEX-Desktop/ApexDesktop.runtimeconfig.json'
    if (Test-Path $runtimePath) { $runtime=Get-Content $runtimePath -Raw|ConvertFrom-Json; if ($runtime.runtimeOptions.tfm -cne 'net8.0' -or @($runtime.runtimeOptions.frameworks|Where-Object {$_.name -ceq 'Microsoft.AspNetCore.App' -and $_.version -match '^8\.'}).Count -ne 1 -or $null -ne $runtime.runtimeOptions.includedFrameworks) { throw 'Not framework-dependent ASP.NET8' } }
}
foreach ($r in $required) { if (-not (Test-Path -LiteralPath (Join-Path $base $r) -PathType Leaf)) { throw "Brak pliku: $r" } }
foreach ($suite in @('bot','platform','backtester')) {
    $actual=Read-ApexTestCounts (Join-Path $base ('Weryfikacja/'+$suite+'.trx')); $declared=$meta.testCounts.$suite
    if ($null -eq $declared -or $declared.total -ne $actual.total -or $declared.passed -ne $actual.passed -or $declared.failed -ne $actual.failed) { throw "Test metadata does not match TRX: $suite" }
}
foreach ($binding in @(@('sourceInputsSha256','Weryfikacja/source-inputs.json'),@('frontendResultsSha256','Weryfikacja/frontend-results.json'),@('navalManifestSha256','APEX-Desktop/wwwroot/naval/build-manifest.json'))) {
    if ($meta.($binding[0]) -cnotmatch '^[a-f0-9]{64}$' -or $meta.($binding[0]) -cne (Hash (Join-Path $base $binding[1]))) { throw "Source/build identity mismatch: $($binding[0])" }
}
$inputs=Get-Content (Join-Path $base 'Weryfikacja/source-inputs.json') -Raw|ConvertFrom-Json
if ($inputs.schema -cne 'apex-source-inputs-v1' -or @($inputs.files).Count -lt 2) { throw 'Invalid source input map' }
$sourcePaths=@{}
foreach ($f in $inputs.files) { Relative $f.path; if ($sourcePaths.ContainsKey($f.path) -or $f.sha256 -cnotmatch '^[a-f0-9]{64}$' -or "$($f.bytes)" -notmatch '^\d+$') { throw 'Invalid source input entry' }; $sourcePaths[$f.path]=$f.sha256 }
if ($sourcePaths['package-lock.json'] -cne $meta.rootLockSha256 -or $sourcePaths['frontend/naval/package-lock.json'] -cne $meta.navalLockSha256) { throw 'Lock/source identity mismatch' }
$front=Get-Content (Join-Path $base 'Weryfikacja/frontend-results.json') -Raw|ConvertFrom-Json
if ($front.schema -cne 'apex-frontend-tests-v1' -or $front.typecheckExit -ne 0 -or $front.buildExit -ne 0) { throw 'Frontend gate mismatch' }
foreach ($kind in @('tests','build')) { $c=$front.$kind; if ($null -eq $c -or $c.total -le 0 -or $c.passed -ne $c.total -or $c.failed -ne 0) { throw 'Invalid frontend result counts' } }
$naval=Test-ApexNavalAssets (Join-Path $base 'APEX-Desktop/wwwroot/naval')
$listed=@{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $base 'manifest.sha256'))) {
    if ($line -cnotmatch '^([0-9a-f]{64})  (.+)$') { throw 'Nieprawidlowa linia manifestu.' }
    $expected=$Matches[1];$relative=$Matches[2];Relative $relative
    $file=[IO.Path]::GetFullPath((Join-Path $base $relative))
    if (-not $file.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'Sciezka poza pakietem.' }
    if ($relative -ceq 'manifest.sha256' -or $listed.ContainsKey($relative)) { throw 'Powtorzony wpis manifestu.' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Hash $file) -cne $expected) { throw "Brak pliku lub niezgodna suma: $relative" }
    $listed[$relative]=$true
}
foreach ($n in $all | Where-Object { -not $_.PSIsContainer }) {
    $relative=$n.FullName.Substring($base.Length).Replace('\','/')
    if ($relative -cne 'manifest.sha256' -and -not $listed.ContainsKey($relative)) { throw "Plik spoza manifestu: $relative" }
    if ($relative -match 'ApexAiLab|Apex\.AiLab|AI_AUTO_UPDATES|AI_LAB_ZALEGLOSCI|apex\.config\.json|(^|/)state\.json|desktop.control|authority|journal\.(json|csv)|(^|/)export\.json|\.lock$|(^|/)access\.json|digest.map|(^|/)\.env|token|credential|\.key$|\.pem$|\.pfx$|\.log$|trace|\.csv$|(^|/)(datasets?|\.git|\.npm[^/]*|node_modules)(/|$)|Apex\..*Tests|Apex\.Backtester|Apex\.Naval\.Replay|\.naval-(stage|backup)|test-backup|\.map$') { throw "Niedozwolona pozostalosc lub dane uzytkownika: $relative" }
    # Supplemental known fake sentinel check, never a claim of generic secret detection.
    if ($n.Length -lt 8388608 -and [IO.File]::ReadAllText($n.FullName).Contains(('FAKE_TASK8A_SECRET_'+'SENTINEL_NOT_A_CREDENTIAL'))) { throw 'Known test secret sentinel in package' }
}
Write-Host "PAKIET OK: $($listed.Count) files; Naval $($naval.artifacts) artifacts; v2 source/target/TRX/SHA/exclusions valid. Integrity is not publisher signature."
