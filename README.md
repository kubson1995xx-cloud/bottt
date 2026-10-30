# APEX Trading AI (repozytorium `bottt`)

Bot C#/.NET dla Windows 11 z polskim panelem. Główna ścieżka: lokalny cTrader Desktop (Pepperstone DEMO) + ApexBridge + APEX Desktop. MT5 jest dodatkiem bez odbioru MetaEditor/brokera; Native pozostaje tylko odczytem/redukcją w produkcji.
Zaimplementowano sześć rodzin strategii, ale profil dopuszcza **wyłącznie IndexDipBuy / indeksy, Experimental, BUY-only**. Inne rodziny/klasy są nieaktywne; kryptowaluty są wykluczone w ścieżce CFD/cTrader/MT5. Opcjonalny Naval to wyłącznie historyczny odczyt OFFLINE, nie bot wykonawczy OKX.

> **Wydanie techniczne DEMO w trakcie końcowej weryfikacji.** Aktualne dowody i bramki odbioru: [wyniki testów](docs/WYNIKI_TESTOW.md) i [HANDOFF](HANDOFF.md).
> **Brak potwierdzonej przewagi.** Nowe autentyczne badania: 64/64 prób zablokowanych na preflight danych, 0 ocenionych kont; n/net/koszty/PF/DD są niedostępne, nie zerowe ([Task4](reports/autonomy/task4-summary.md)). Raport 2005–2020 jest historyczny i nie waliduje bieżącej egzekucji/TP 3R.
> Ta wersja jest **wyłącznie DEMO**. Nie udostępnia odblokowania REAL. Rzeczywisty test transakcji DEMO wymaga osobnego zatwierdzenia po połączeniu z cTrader.

## Architektura

```
  cTrader Desktop ─ ApexBridge (cBot) ──WebSocket──┐
                                                    ├─► APEX Desktop ─► panel http://127.0.0.1:5077
  MetaTrader 5 ─── ApexBridge (EA) ─────TCP────────┘     │
                                                         ├─ silnik (TradingEngine) – osobny dla każdego konta
                                                         ├─ strategie + reżim rynku + skaner okazji (ranking 0–100)
                                                         ├─ RiskEngine (0,5%/transakcję, 1,5%/dzień, DD 5%, 3 pozycje, dźwignia ≤ 5)
                                                         ├─ ochrona wykonania (rejestr sygnałów, weryfikacja niepewnych odpowiedzi)
                                                         ├─ filtr wiadomości makro (kalendarz)
                                                         └─ dziennik, statystyki, Centrum decyzji
```

| Katalog | Zawartość |
|---|---|
| `src/Apex.Core` | Silnik niezależny od platformy: wskaźniki, reżim, strategie, ranking, ryzyko, zarządzanie pozycją, rejestr wykonania, protokół mostu. |
| `src/Apex.Desktop` | APEX Desktop: serwer mostów, panel WWW (PL), Centrum decyzji. |
| `src/Apex.AiLab` | Moduł automatycznych analiz i aktualizacji przez AI (APEX AI Lab) – **odłożony**, nie wchodzi w skład pakietu ani panelu (stan: [`HANDOFF.md`](HANDOFF.md)). |
| `src/Platforms/cTrader` | `ApexBridge` (główna ścieżka) i `ApexTradingAI` (Native: produkcyjnie odczyt/redukcja, bez START/TEST nowych wejść). |
| `src/Platforms/MT5` | Most MetaTrader 5 (MQL5). |
| `tools/Apex.Backtester` | Backtester na tym samym silniku: walk-forward, holdout, Monte Carlo, scenariusze kosztów. |
| `tools/Apex.PlatformSimulator` | Symulator platformy (testy bez brokera, wstrzykiwanie awarii). |
| `tools/windows/Zbuduj-APEX.ps1` | Budowa pakietu dla Windows (z testami). |
| `tests/Apex.Tests` | Testy bota: jednostkowe, silnika i integracyjne (prawdziwy proces APEX Desktop). |
| `tests/Apex.AiLab.Tests` | Testy odłożonego modułu AI Lab (nie są uruchamiane przy budowie pakietu). |
| `config`, `reports`, `docs` | Profil strategii, wyniki badań, dokumentacja. |

## Naval — bieżąca dostawa lokalna

Task7 zaakceptowany lokalnie na Linux; nie oznacza to odbioru urządzeń ani brokera. Task8A buduje świeży **linux-x64 / framework-dependent / ASP.NET Core 8** ZIP ze źródeł, osobno od historycznego Windows autonomy. Zobacz [instrukcję Linux/VPS i granice](docs/NAVAL_DELIVERY_VPS.md), [prywatny opcjonalny odczyt](docs/NAVAL_READ_PUBLICATION.md) oraz `apex-build.json` i `Weryfikacja/source-inputs.json` konkretnej paczki. Domyślnie brak NavalRead i kont. Nowego Naval Windows ZIP nie potwierdza stary run `40360216699`.

## Szybki start (Windows 11, historyczny pakiet autonomy)

Rozpakuj cały `APEX Windows DEMO.zip` do nowego folderu i otwórz `Uruchom APEX.cmd`. **Nie potrzebujesz SDK, Node ani Visual Studio.** Zainstaluj zgodny `cTrader/ApexBridge.algo`, zaloguj dedykowane Pepperstone DEMO hedging w lokalnym cTrader Desktop i sparuj most kluczem z panelu. Read-only setup rezerwuje zakres; skopiuj dokładną wygenerowaną etykietę do mostu i sparuj ponownie. Świeże ScopedReady nie oznacza pełnej historii brokera. Test wymaga osobnej zgody na rzeczywistą transakcję, a START ważnego trwałego PASS i kolejnego jawnego zatwierdzenia. Zacznij od [instalacji](docs/INSTALACJA_WINDOWS.md) i [procedury odbioru DEMO](docs/PROCEDURA_DEMO_PEPPERSTONE.md).

### Budowanie ze źródeł

```powershell
winget install Microsoft.DotNet.SDK.8
# Wymagany przetestowany Node26 / npm11; zwykłe 22+ nie wystarcza dla Vite8
powershell -ExecutionPolicy Bypass -File tools\windows\Zbuduj-APEX.ps1
& ".\dist\Uruchom APEX.cmd"
```

Następnie podłącz cTrader (konto **DEMO**) tylko do odczytu. Test wykonania dopiero po osobnym zatwierdzeniu użytkownika; budowa i dostarczenie pakietu nie są zgodą na handel.

## Dokumentacja

1. [Instalacja na Windows 11](docs/INSTALACJA_WINDOWS.md)
2. [Podłączanie platform (cTrader, MT5, inne)](docs/PODLACZENIE_PLATFORM.md)
3. [Procedura DEMO – Pepperstone](docs/PROCEDURA_DEMO_PEPPERSTONE.md)
4. [Instrukcja panelu (w tym Centrum decyzji)](docs/INSTRUKCJA_PANELU.md)
5. [Konfiguracja: instrumenty, prowizje, profil, blokada REAL](docs/KONFIGURACJA.md)
6. [Stan projektu i przekazanie pracy (HANDOFF)](HANDOFF.md)
7. [Protokół mostu APEX Bridge v1](docs/PROTOKOL_MOSTU.md)
8. [Metodologia testów strategii](docs/METODOLOGIA_TESTOW.md) i [raport badań](reports/RAPORT_BADAN.md)
9. [Wyniki testów](docs/WYNIKI_TESTOW.md)
10. [Przegląd bezpieczeństwa](docs/PRZEGLAD_BEZPIECZENSTWA.md)
11. [Ograniczenia i status gotowości](docs/OGRANICZENIA_I_STATUS.md)

## Zasady, których bot nie łamie

- Każde nowe zlecenie wymaga SL i TP. Nie ma martingale, gridu, uśredniania ani zwiększania pozycji po stracie.
- Wielkość pozycji wynika wyłącznie ze stałego procentu kapitału i jest zaokrąglana w dół.
- Nie dotyka pozycji ręcznych ani innych botów.
- Po niepewnej odpowiedzi serwera najpierw weryfikuje stan, nigdy nie ponawia „w ciemno”.
- Strategie eksperymentalne działają wyłącznie na DEMO.
- Ocena okazji 0–100 to ranking jakości, **nie prawdopodobieństwo zysku**.
- Brak modułów uczenia i automatycznej aktualizacji. Aktualizacja aplikacji jest ręczna. Po restarcie nowe wejścia wymagają świadomego START; nie usuwaj zapisu stanu w celu ominięcia limitów.

## Dla deweloperów

```bash
dotnet build APEX.Trading.Demo.sln -c Release                 # cały produkt, oba cBoty
dotnet test tests/Apex.Tests -c Release                      # testy bota
dotnet test tests/Apex.AiLab.Tests -c Release                 # archiwum, poza produktem
npm ci --ignore-scripts && npm test && npm run check         # wyłącznie testy panelu
dotnet run --project tools/Apex.PlatformSimulator -- --key K   # symulator platformy
dotnet run --project tools/Apex.Backtester -- smoke            # backtester (dane: tools/data/prepare_data.py)
```
