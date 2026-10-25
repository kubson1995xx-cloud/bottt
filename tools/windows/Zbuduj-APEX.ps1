#Requires -Version 5.1
<#
.SYNOPSIS
  Buduje samodzielny pakiet APEX DEMO dla Windows 11 x64 i ZIP.
.DESCRIPTION
  Wymaga .NET 8 SDK, Node.js >= 22 i dostepu do NuGet/npm przy budowaniu.
  Odbiorca gotowego ZIP nie potrzebuje SDK ani Node. Skrypt nigdy nie pomija testow.
  Katalog docelowy musi byc pusty lub nie istniec: bez nadpisywania starego pakietu.
  ASCII dla zgodnosci Windows PowerShell 5.1. MT5 jest dodatkiem, bez kompilacji.
#>
param([string]$Wyjscie = "")
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
if ([string]::IsNullOrWhiteSpace($Wyjscie)) { $Wyjscie = Join-Path $repo "dist" }
$Wyjscie = [System.IO.Path]::GetFullPath($Wyjscie)
if ((Test-Path -LiteralPath $Wyjscie) -and @(Get-ChildItem -LiteralPath $Wyjscie -Force).Count -gt 0) {
    throw "Katalog Wyjscie nie jest pusty. Wybierz NOWY katalog parametrem -Wyjscie; stare pliki nie beda nadpisane."
}
if (Test-Path -LiteralPath ($Wyjscie + ".zip")) { throw "ZIP juz istnieje. Wybierz nowy katalog -Wyjscie." }
function Krok([string]$tekst) { Write-Host "=== $tekst ===" -ForegroundColor Cyan }
function Sprawdz([string]$co) { if ($LASTEXITCODE -ne 0) { throw "$co - blad (kod $LASTEXITCODE). Brak zweryfikowanego pakietu." } }
$sdki = & dotnet --list-sdks
Sprawdz "Sprawdzanie SDK"
if (-not ($sdki | Where-Object { $_ -match '^8\.' })) { throw "Zainstaluj .NET 8 SDK: winget install Microsoft.DotNet.SDK.8" }
& node --version
Sprawdz "Node.js (wymagany tylko do testow panelu)"
$commit = & git -C $repo rev-parse HEAD
Sprawdz "Identyfikator zrodel"
$dirty = [bool](& git -C $repo status --porcelain)
$evidence = Join-Path $Wyjscie "Weryfikacja"
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
Push-Location $repo
try {
    Krok "Kompilacja kompletnego rozwiazania DEMO (bez AI Lab)"
    & dotnet build APEX.Trading.Demo.sln -c Release
    Sprawdz "Kompilacja rozwiazania"
    Krok "Testy jednostkowe i integracyjne bota"
    & dotnet test tests/Apex.Tests/Apex.Tests.csproj -c Release --no-build --logger "trx;LogFileName=bot.trx" --results-directory $evidence
    Sprawdz "Testy bota"
    Krok "Wykonywalne testy adaptera cTrader (bez brokera)"
    & dotnet test src/Platforms/cTrader/Tests/Apex.Platform.Tests.csproj -c Release --no-build --logger "trx;LogFileName=platform.trx" --results-directory $evidence
    Sprawdz "Testy adaptera cTrader"
    Krok "Testy panelu i pakietu"
    & npm ci --ignore-scripts --no-audit --no-fund
    Sprawdz "Zaleznosci testowe panelu"
    & npm test
    Sprawdz "Testy panelu"
    & npm run check
    Sprawdz "Skladnia JavaScript"
    Krok "Samodzielny ApexDesktop.exe (win-x64)"
    $desktop = Join-Path $Wyjscie "APEX-Desktop"
    & dotnet publish src/Apex.Desktop/Apex.Desktop.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IsTransformWebConfigDisabled=true -o $desktop
    Sprawdz "Publikacja Windows"
    $ctrader = Join-Path $Wyjscie "cTrader"
    New-Item -ItemType Directory -Force -Path $ctrader | Out-Null
    Copy-Item "src/Platforms/cTrader/ApexBridge/ApexBridge/bin/Release/net6.0/ApexBridge.algo" $ctrader
    # Wersja natywna jest alternatywa, nie wolno uruchamiac jej razem z mostem na tym samym koncie.
    Copy-Item "src/Platforms/cTrader/ApexTradingAI/ApexTradingAI/bin/Release/net6.0/ApexTradingAI.algo" $ctrader
    $mt5 = Join-Path $Wyjscie "MT5-opcjonalny"
    New-Item -ItemType Directory -Force -Path $mt5 | Out-Null
    if (Test-Path "src/Platforms/MT5/ApexBridge.mq5") { Copy-Item "src/Platforms/MT5/ApexBridge.mq5" $mt5 }
    $docs = Join-Path $Wyjscie "Dokumentacja"
    New-Item -ItemType Directory -Force -Path $docs | Out-Null
    # Jawna lista: nie pakujemy dokumentow odlozonego modulu AI Lab.
    foreach ($name in @("INSTALACJA_WINDOWS.md", "PROCEDURA_DEMO_PEPPERSTONE.md", "PODLACZENIE_PLATFORM.md", "INSTRUKCJA_PANELU.md", "KONFIGURACJA.md", "OGRANICZENIA_I_STATUS.md", "WYNIKI_TESTOW.md", "PRZEGLAD_BEZPIECZENSTWA.md", "METODOLOGIA_TESTOW.md")) {
        Copy-Item (Join-Path "docs" $name) $docs
    }
    Copy-Item "README.md" $docs
    Copy-Item "reports/RAPORT_BADAN.md" $docs
    Copy-Item (Join-Path $PSScriptRoot "Sprawdz-Pakiet.ps1") $Wyjscie
    Copy-Item (Join-Path $PSScriptRoot "Smoke-Windows.ps1") $evidence
    $cmd = "@echo off`r`nrem APEX DEMO - cTrader Desktop musi dzialac na tym samym komputerze.`r`nstart `"`" `"%~dp0APEX-Desktop\ApexDesktop.exe`"`r`n"
    [IO.File]::WriteAllText((Join-Path $Wyjscie "Uruchom APEX.cmd"), $cmd, [Text.Encoding]::ASCII)
    $info = [ordered]@{ product = "APEX Trading AI DEMO"; sourceCommit = "$commit".Trim(); sourceDirty = $dirty; builtUtc = [DateTime]::UtcNow.ToString("o"); target = "win-x64"; demoOnly = $true; botTestsRun = $true; panelTestsRun = $true; brokerTested = $false; windowsRuntimeTested = $false }
    [IO.File]::WriteAllText((Join-Path $Wyjscie "apex-build.json"), ($info | ConvertTo-Json), [Text.Encoding]::UTF8)
    $base = $Wyjscie.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $hashes = @(Get-ChildItem -LiteralPath $Wyjscie -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($base.Length).Replace('\', '/')
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + $relative
    })
    [IO.File]::WriteAllLines((Join-Path $Wyjscie "manifest.sha256"), $hashes, [Text.Encoding]::UTF8)
    & (Join-Path $PSScriptRoot "Sprawdz-Pakiet.ps1") -Pakiet $Wyjscie
    Krok "ZIP"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($Wyjscie, ($Wyjscie + ".zip"), [IO.Compression.CompressionLevel]::Optimal, $false)
    $zipHash = (Get-FileHash -LiteralPath ($Wyjscie + ".zip") -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($Wyjscie + ".zip.sha256"), ($zipHash + "  " + [IO.Path]::GetFileName($Wyjscie) + ".zip`n"), [Text.Encoding]::ASCII)
    Write-Host "Pakiet: $Wyjscie.zip" -ForegroundColor Green
    Write-Host "SHA256: $zipHash"
    Write-Host "Kompilacja i testy OK. To NIE jest dowod uruchomienia na Windows ani polaczenia z brokerem."
    Write-Host "Rozpakuj ZIP. Zacznij od Dokumentacja/INSTALACJA_WINDOWS.md. Tylko DEMO."
} finally { Pop-Location }
