# APEX Trading AI (repozytorium `bottt`)

Autonomiczny, wieloplatformowy bot tradingowy dla Windows 11 z panelem premium. Obsługuje cTrader (np. Pepperstone) i MetaTrader 5 przez mosty.
Handluje FX, metalami, energią, indeksami, obligacjami i towarami. **Kryptowaluty są całkowicie wykluczone.**

> **Wydanie techniczne DEMO w trakcie końcowej weryfikacji.** Aktualne dowody i bramki odbioru: [wyniki testów](docs/WYNIKI_TESTOW.md) i [HANDOFF](HANDOFF.md).
> Badania na danych 2005–2020 nie wykazały strategii z potwierdzoną przewagą ([raport](reports/RAPORT_BADAN.md)).
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
                                                         └─ dziennik, statystyki, AI Decision Center
```

| Katalog | Zawartość |
|---|---|
| `src/Apex.Core` | Silnik niezależny od platformy: wskaźniki, reżim, strategie, ranking, ryzyko, zarządzanie pozycją, rejestr wykonania, protokół mostu. |
| `src/Apex.Desktop` | APEX Desktop: serwer mostów, panel WWW (PL), AI Decision Center. |
| `src/Apex.AiLab` | Moduł automatycznych analiz i aktualizacji przez AI (APEX AI Lab) – **odłożony**, nie wchodzi w skład pakietu ani panelu (stan: [`HANDOFF.md`](HANDOFF.md)). |
| `src/Platforms/cTrader` | `ApexBridge` (most) i `ApexTradingAI` (wersja natywna „wszystko w jednym”). |
| `src/Platforms/MT5` | Most MetaTrader 5 (MQL5). |
| `tools/Apex.Backtester` | Backtester na tym samym silniku: walk-forward, holdout, Monte Carlo, scenariusze kosztów. |
| `tools/Apex.PlatformSimulator` | Symulator platformy (testy bez brokera, wstrzykiwanie awarii). |
| `tools/windows/Zbuduj-APEX.ps1` | Budowa pakietu dla Windows (z testami). |
| `tests/Apex.Tests` | Testy bota: jednostkowe, silnika i integracyjne (prawdziwy proces APEX Desktop). |
| `tests/Apex.AiLab.Tests` | Testy odłożonego modułu AI Lab (nie są uruchamiane przy budowie pakietu). |
| `config`, `reports`, `docs` | Profil strategii, wyniki badań, dokumentacja. |

## Szybki start (Windows 11, gotowy ZIP)

Rozpakuj cały `APEX-Windows-DEMO.zip` do nowego folderu i otwórz `Uruchom APEX.cmd`. **Nie potrzebujesz SDK, Node ani Visual Studio.** Zainstaluj `cTrader/ApexBridge.algo`, zaloguj Pepperstone DEMO w lokalnym cTrader Desktop i sparuj most kluczem z panelu. Zacznij od [instalacji](docs/INSTALACJA_WINDOWS.md) i [procedury odbioru DEMO](docs/PROCEDURA_DEMO_PEPPERSTONE.md).

### Budowanie ze źródeł

```powershell
winget install Microsoft.DotNet.SDK.8
winget install OpenJS.NodeJS.LTS
powershell -ExecutionPolicy Bypass -File tools\windows\Zbuduj-APEX.ps1
& ".\dist\Uruchom APEX.cmd"
```

Następnie podłącz cTrader (konto **DEMO**) i wykonaj test wykonania. Szczegóły w dokumentacji poniżej.

## Dokumentacja

1. [Instalacja na Windows 11](docs/INSTALACJA_WINDOWS.md)
2. [Podłączanie platform (cTrader, MT5, inne)](docs/PODLACZENIE_PLATFORM.md)
3. [Procedura DEMO – Pepperstone](docs/PROCEDURA_DEMO_PEPPERSTONE.md)
4. [Instrukcja panelu (w tym AI Decision Center)](docs/INSTRUKCJA_PANELU.md)
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
