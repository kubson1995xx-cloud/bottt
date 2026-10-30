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
foreach ($flag in @('brokerTested','windowsRuntimeTested')) { if ($meta.$flag -isnot [bool] -or $meta.$flag -ne $false) { throw "Invalid sealed default flag: $flag" } }
if ($meta.navalReadDefaultEnabled -isnot [bool] -or $meta.navalReadDefaultEnabled -ne ($meta.target -ceq 'win-x64')) { throw 'Invalid target-specific Naval default' }
if ($meta.target -ceq 'win-x64' -and ($meta.firstRunProfile -cne 'windows-local-presentation-v1' -or $meta.existingConfigPolicy -cne 'preserve' -or $meta.tools.dotnet -notmatch '^8\.0\.')) { throw 'Invalid Windows first-run or SDK identity' }
if ($meta.task7Acceptance -cnotin @('pending','accepted-local-linux')) { throw 'Missing Task7 scope' }
foreach ($tool in @('node','npm','dotnet','powershell')) { if ([string]::IsNullOrWhiteSpace($meta.tools.$tool)) { throw 'Missing tool identity' } }
if ($meta.tools.node -notmatch '^v26\.') { throw 'Untested Node line' }
$required=@('APEX-Desktop/wwwroot/index.html','APEX-Desktop/wwwroot/app.js','APEX-Desktop/wwwroot/decision-center.js','APEX-Desktop/wwwroot/app.css','cTrader/ApexBridge.algo','cTrader/ApexTradingAI.algo','Dokumentacja/THIRD_PARTY_NAVAL.txt','apex-build.json','manifest.sha256','Sprawdz-Pakiet.ps1','TestEvidence.ps1','MT5-opcjonalny/ApexBridge.mq5','Weryfikacja/source-inputs.json','Weryfikacja/frontend-results.json','Weryfikacja/frontend-tests.txt','Weryfikacja/frontend-build-tests.txt','Weryfikacja/bot.trx','Weryfikacja/platform.trx','Weryfikacja/backtester.trx','Weryfikacja/Smoke-Windows.ps1')
foreach ($name in @('INSTALACJA_WINDOWS.md','PROCEDURA_DEMO_PEPPERSTONE.md','PODLACZENIE_PLATFORM.md','INSTRUKCJA_PANELU.md','KONFIGURACJA.md','OGRANICZENIA_I_STATUS.md','WYNIKI_TESTOW.md','PRZEGLAD_BEZPIECZENSTWA.md','METODOLOGIA_TESTOW.md','BACKTESTER.md','NAVAL_READ_PUBLICATION.md','NAVAL_DELIVERY_VPS.md','NAVAL_OFFLINE.md','NAVAL_SHARED_OFFLINE.md','README.md','RAPORT_BADAN.md','NAVAL_FRONTEND.md')) { $required+=('Dokumentacja/'+$name) }
if ($meta.target -ceq 'win-x64') {
    if ($meta.deploymentMode -cne 'self-contained' -or $meta.requiredRuntime -cne 'included') { throw 'Windows deployment metadata mismatch' }
    $required+=@('APEX-Desktop/ApexDesktop.exe','Uruchom APEX.cmd','PRZECZYTAJ.md','APEX-Desktop/presentation/naval-publication.json','APEX-Desktop/presentation/presentation.json','Weryfikacja/windows-publish.json','Weryfikacja/windows.trx')
    Test-ApexWindowsPublish (Join-Path $base 'APEX-Desktop') (Join-Path $base 'Weryfikacja/windows-publish.json') | Out-Null
    Test-ApexPresentation (Join-Path $base 'APEX-Desktop/presentation') | Out-Null
} else {
    if ($meta.deploymentMode -cne 'framework-dependent' -or $meta.requiredRuntime -cne 'Microsoft.AspNetCore.App 8') { throw 'Linux deployment metadata mismatch' }
    $required+=@('APEX-Desktop/ApexDesktop.dll','APEX-Desktop/Apex.Core.dll','APEX-Desktop/ApexDesktop.deps.json','APEX-Desktop/ApexDesktop.runtimeconfig.json','Uruchom APEX.sh')
    if (Test-Path -LiteralPath (Join-Path $base 'APEX-Desktop/ApexDesktop.exe')) { throw 'Fake Windows executable on Linux target' }
    $runtimePath=Join-Path $base 'APEX-Desktop/ApexDesktop.runtimeconfig.json'
    if (Test-Path $runtimePath) { $runtime=Get-Content $runtimePath -Raw|ConvertFrom-Json; if ($runtime.runtimeOptions.tfm -cne 'net8.0' -or @($runtime.runtimeOptions.frameworks|Where-Object {$_.name -ceq 'Microsoft.AspNetCore.App' -and $_.version -match '^8\.'}).Count -ne 1 -or $null -ne $runtime.runtimeOptions.includedFrameworks) { throw 'Not framework-dependent ASP.NET8' } }
}
foreach ($r in $required) { if (-not (Test-Path -LiteralPath (Join-Path $base $r) -PathType Leaf)) { throw "Brak pliku: $r" } }
$suites=@('bot','platform','backtester');if ($meta.target -ceq 'win-x64') { $suites+=@('windows') }
foreach ($suite in $suites) {
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
foreach ($kind in @('tests','build')) {
    $c=$front.$kind
    $allowedLog='frontend-tests.txt';if ($kind -ceq 'build') { $allowedLog='frontend-build-tests.txt' }
    if ($null -eq $c -or $c.log -cne $allowedLog -or $c.logSha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid frontend log reference/binding' }
    $log=Join-Path $base ('Weryfikacja/'+$allowedLog)
    if ((Hash $log) -cne $c.logSha256) { throw 'Frontend log SHA mismatch' }
    $actual=Read-ApexFrontendCounts $log
    foreach ($name in @('total','passed','failed')) { if ($null -eq $c.$name -or "$($c.$name)" -cnotmatch '^[0-9]+$' -or $c.$name -ne $actual[$name]) { throw 'Frontend declared/TAP counts differ' } }
    # v1 producer omits these fields; when declared, they must agree with actual zero counters.
    foreach ($name in @('skipped','cancelled','todo')) { if ($null -ne $c.$name -and ("$($c.$name)" -cnotmatch '^[0-9]+$' -or $c.$name -ne $actual[$name])) { throw 'Frontend declared/TAP nonpassing counts differ' } }
}
$naval=Test-ApexNavalAssets (Join-Path $base 'APEX-Desktop/wwwroot/naval')
# Allow only the builder's explicit product layout, not anything blessed by a rehash.
# The Naval subtree is separately bounded by Test-ApexNavalAssets above.
$allowed=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($p in $required) { [void]$allowed.Add($p) }
foreach ($p in @('APEX-Desktop/ApexDesktop.pdb','APEX-Desktop/Apex.Core.pdb')) { [void]$allowed.Add($p) }
if ($meta.target -ceq 'linux-x64') { [void]$allowed.Add('APEX-Desktop/Apex.Core.dll') }
$directories=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($p in $allowed) { $parts=$p.Split('/');for ($i=1;$i -lt $parts.Length;$i++) { [void]$directories.Add(($parts[0..($i-1)] -join '/')) } }
foreach ($p in @('APEX-Desktop/wwwroot/naval','APEX-Desktop/wwwroot/naval/assets')) { [void]$directories.Add($p) }
$actual=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$actualFolded=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($n in $all | Where-Object { $_.FullName -cne $canonical }) {
    $p=$n.FullName.Substring($base.Length).Replace('\','/');Relative $p
    if (-not $actualFolded.Add($p)) { throw 'Case-colliding package path' }
    if ($n.PSIsContainer) { if (-not $directories.Contains($p)) { throw "Unexpected package directory: $p" };continue }
    [void]$actual.Add($p)
    if (-not $allowed.Contains($p) -and -not $p.StartsWith('APEX-Desktop/wwwroot/naval/',[StringComparison]::Ordinal)) { throw "File outside shipped product layout: $p" }
}
$listed=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$listedFolded=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in [IO.File]::ReadAllLines((Join-Path $base 'manifest.sha256'))) {
    if ($line -cnotmatch '^([0-9a-f]{64})  (.+)$') { throw 'Nieprawidlowa linia manifestu.' }
    $expected=$Matches[1];$relative=$Matches[2];Relative $relative
    $file=[IO.Path]::GetFullPath((Join-Path $base $relative))
    if (-not $file.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'Sciezka poza pakietem.' }
    if ($relative -ceq 'manifest.sha256' -or -not $listedFolded.Add($relative)) { throw 'Powtorzony wpis manifestu.' }
    if (-not $actual.Contains($relative)) { throw 'Manifest spelling differs from actual package path' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Hash $file) -cne $expected) { throw "Brak pliku lub niezgodna suma: $relative" }
    [void]$listed.Add($relative)
}
foreach ($n in $all | Where-Object { -not $_.PSIsContainer }) {
    $relative=$n.FullName.Substring($base.Length).Replace('\','/')
    if ($relative -cne 'manifest.sha256' -and -not $listed.Contains($relative)) { throw "Plik spoza manifestu: $relative" }
    if ($relative -match 'ApexAiLab|Apex\.AiLab|AI_AUTO_UPDATES|AI_LAB_ZALEGLOSCI|apex\.config\.json|(^|/)state\.json|desktop.control|authority|journal\.(json|csv)|(^|/)export\.json|\.lock$|(^|/)access\.json|digest.map|(^|/)\.env|token|credential|\.key$|\.pem$|\.pfx$|\.log$|trace|\.csv$|(^|/)(datasets?|\.git|\.npm[^/]*|node_modules)(/|$)|Apex\..*Tests|Apex\.Backtester|Apex\.Naval\.Replay|\.naval-(stage|backup)|test-backup|\.map$') { throw "Niedozwolona pozostalosc lub dane uzytkownika: $relative" }
    # Document-schema check is independent of basename and sentinel size window.
    # This does NOT claim generic secret absence or detect arbitrary embedded data.
    $text=[IO.File]::ReadAllText($n.FullName)
    if ($n.Length -lt 8388608 -and $text.Contains(('FAKE_TASK8A_SECRET_'+'SENTINEL_NOT_A_CREDENTIAL'))) { throw 'Known test secret sentinel in package' }
    if ($text.TrimStart().StartsWith('{',[StringComparison]::Ordinal)) {
        $document=$null;try { $document=$text|ConvertFrom-Json } catch { }
        if ($null -ne $document -and $document.schema -eq 'naval-local-access-v1') { throw 'Private access-map document in package' }
    }
}
Write-Host "PAKIET OK: $($listed.Count) files; Naval $($naval.artifacts) artifacts; v2 source/target/TRX/SHA/exclusions valid. Integrity is not publisher signature."
