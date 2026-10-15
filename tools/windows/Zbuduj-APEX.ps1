#Requires -Version 5.1
<#
.SYNOPSIS
    Buduje kompletny pakiet APEX Trading AI dla Windows 11.

.DESCRIPTION
    1. Sprawdza .NET 8 SDK.
    2. Uruchamia testy automatyczne (jednostkowe + integracyjne). Błąd testów = brak pakietu.
    3. Publikuje APEX Desktop jako samodzielny plik ApexDesktop.exe (win-x64, nie wymaga instalacji .NET).
    4. Buduje cBoty cTrader (ApexBridge.algo – most, ApexTradingAI.algo – wersja natywna "wszystko w jednym").
    5. Kopiuje most MetaTrader 5 (ApexBridge.mq5 – do kompilacji w MetaEditor).
    6. Kopiuje dokumentację i tworzy skrót "Uruchom APEX.cmd".

    Skrypt NIE usuwa żadnych plików – istniejące pliki w katalogu wyjściowym są nadpisywane.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\windows\Zbuduj-APEX.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\windows\Zbuduj-APEX.ps1 -Wyjscie C:\APEX -PominTesty
#>
param(
    [string]$Wyjscie = "",
    [switch]$PominTesty
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($Wyjscie)) { $Wyjscie = Join-Path $repo "dist" }

function Krok([string]$tekst) { Write-Host ""; Write-Host "=== $tekst ===" -ForegroundColor Cyan }
function Sprawdz([string]$co) { if ($LASTEXITCODE -ne 0) { throw "$co – błąd (kod $LASTEXITCODE). Pakiet NIE został zbudowany." } }

Krok "Sprawdzanie .NET 8 SDK"
$sdki = & dotnet --list-sdks 2>$null
if (-not ($sdki | Where-Object { $_ -match '^8\.' })) {
    throw "Brak .NET 8 SDK. Zainstaluj: winget install Microsoft.DotNet.SDK.8  (lub https://dotnet.microsoft.com/download/dotnet/8.0)"
}
Write-Host ($sdki | Where-Object { $_ -match '^8\.' } | Select-Object -First 1)

if (-not $PominTesty) {
    Krok "Testy automatyczne (ok. 1–2 min)"
    & dotnet test (Join-Path $repo "tests\Apex.Tests\Apex.Tests.csproj") -c Release
    Sprawdz "Testy"
}

Krok "APEX Desktop (win-x64, samodzielny ApexDesktop.exe)"
$desktop = Join-Path $Wyjscie "APEX-Desktop"
& dotnet publish (Join-Path $repo "src\Apex.Desktop\Apex.Desktop.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IsTransformWebConfigDisabled=true -o $desktop
Sprawdz "Publikacja APEX Desktop"

Krok "cBoty cTrader"
$ctrader = Join-Path $Wyjscie "cTrader"
New-Item -ItemType Directory -Force -Path $ctrader | Out-Null
foreach ($sln in @("src\Platforms\cTrader\ApexBridge\ApexBridge.sln", "src\Platforms\cTrader\ApexTradingAI\ApexTradingAI.sln")) {
    & dotnet build (Join-Path $repo $sln) -c Release
    Sprawdz "Budowa $sln"
}
Copy-Item (Join-Path $repo "src\Platforms\cTrader\ApexBridge\ApexBridge\bin\Release\net6.0\ApexBridge.algo") $ctrader -Force
Copy-Item (Join-Path $repo "src\Platforms\cTrader\ApexTradingAI\ApexTradingAI\bin\Release\net6.0\ApexTradingAI.algo") $ctrader -Force

Krok "MetaTrader 5"
$mt5 = Join-Path $Wyjscie "MT5"
New-Item -ItemType Directory -Force -Path $mt5 | Out-Null
Copy-Item (Join-Path $repo "src\Platforms\MT5\ApexBridge.mq5") $mt5 -Force

Krok "Dokumentacja"
$docs = Join-Path $Wyjscie "Dokumentacja"
New-Item -ItemType Directory -Force -Path $docs | Out-Null
Copy-Item (Join-Path $repo "docs\*.md") $docs -Force
Copy-Item (Join-Path $repo "README.md") $docs -Force
Copy-Item (Join-Path $repo "reports\RAPORT_BADAN.md") $docs -Force

$cmd = @"
@echo off
rem Uruchamia APEX Desktop (panel otworzy się w przeglądarce Edge w trybie aplikacji).
start "" "%~dp0APEX-Desktop\ApexDesktop.exe"
"@
Set-Content -Path (Join-Path $Wyjscie "Uruchom APEX.cmd") -Value $cmd -Encoding ASCII

Krok "Gotowe"
Write-Host "Pakiet: $Wyjscie" -ForegroundColor Green
Write-Host "  Uruchom APEX.cmd              – start APEX Desktop"
Write-Host "  cTrader\ApexBridge.algo       – most cTrader (dwuklik = instalacja w cTrader)"
Write-Host "  cTrader\ApexTradingAI.algo    – wersja natywna (bez APEX Desktop)"
Write-Host "  MT5\ApexBridge.mq5            – most MetaTrader 5 (kompilacja w MetaEditor)"
Write-Host "  Dokumentacja\                 – instrukcje (zacznij od INSTALACJA_WINDOWS.md)"
