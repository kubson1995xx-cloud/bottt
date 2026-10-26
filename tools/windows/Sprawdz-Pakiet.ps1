#Requires -Version 5.1
param([string]$Pakiet = $PSScriptRoot)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "TestEvidence.ps1")
$base = (Resolve-Path -LiteralPath $Pakiet).Path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($required in @("APEX-Desktop/ApexDesktop.exe", "APEX-Desktop/wwwroot/index.html", "APEX-Desktop/wwwroot/app.js", "APEX-Desktop/wwwroot/decision-center.js", "cTrader/ApexBridge.algo", "Uruchom APEX.cmd", "apex-build.json", "manifest.sha256")) {
    if (-not (Test-Path -LiteralPath (Join-Path $base $required) -PathType Leaf)) { throw "Brak pliku: $required" }
}
$meta = Get-Content -LiteralPath (Join-Path $base "apex-build.json") -Raw | ConvertFrom-Json
if (-not $meta.demoOnly -or -not $meta.botTestsRun -or -not $meta.platformTestsRun -or -not $meta.backtesterTestsRun -or -not $meta.panelTestsRun) { throw "Pakiet bez potwierdzonych testow DEMO." }
foreach ($suite in @('bot', 'platform', 'backtester')) {
    $actual = Read-ApexTestCounts (Join-Path $base ("Weryfikacja/" + $suite + ".trx"))
    $declared = $meta.testCounts.$suite
    if ($null -eq $declared -or $declared.total -ne $actual.total -or $declared.passed -ne $actual.passed -or $declared.failed -ne $actual.failed) {
        throw "Test metadata does not match TRX: $suite"
    }
}
$listed = @{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $base "manifest.sha256"))) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Nieprawidlowa linia manifestu." }
    $expected = $Matches[1]
    $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[/\\])\.\.([/\\]|$)') { throw "Niedozwolona sciezka manifestu." }
    $file = [IO.Path]::GetFullPath((Join-Path $base $relative))
    if (-not $file.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw "Sciezka poza pakietem." }
    if ($listed.ContainsKey($relative)) { throw "Powtorzony wpis manifestu: $relative" }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Brak pliku z manifestu: $relative" }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Niezgodna suma: $relative" }
    $listed[$relative] = $true
}
foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) {
    $relative = $file.FullName.Substring($base.Length).Replace('\', '/')
    if ($relative -ne 'manifest.sha256' -and -not $listed.ContainsKey($relative)) { throw "Plik spoza manifestu: $relative" }
    if ($relative -match 'ApexAiLab|Apex\.AiLab|AI_AUTO_UPDATES|AI_LAB_ZALEGLOSCI|apex\.config\.json|state\.json') { throw "Niedozwolona pozostalosc lub dane uzytkownika: $relative" }
}
Write-Host "PAKIET OK: $($listed.Count) plikow, SHA-256 zgodne. Integralnosc nie jest podpisem zaufanego wydawcy."
