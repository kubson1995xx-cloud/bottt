# HANDOFF – APEX Trading AI (repozytorium `bottt`)

## Aktywne przejęcie — 2026-10-26

- Zlecenie: ukończyć technicznie wersję Windows/cTrader Pepperstone **DEMO**, bez AI Lab i bez handlu REAL. Prawdziwa transakcja DEMO dopiero po osobnym zatwierdzeniu użytkownika.
- Punkt startowy: `a0385e40cdd9ceddd8d68d27dd0d17b8ee6e8fea`, gałąź `claude/beautiful-mccarthy-9cibo1`. Osobny świeży klon: `/home/ubuntu/projects/apex-trading-ai`; nie naruszono istniejących katalogów użytkownika.
- Specyfikacja: `docs/superpowers/specs/2026-10-26-apex-demo-design.md`; plan: `docs/superpowers/plans/2026-10-26-apex-demo.md`.
- Bazowe wyniki z tej sesji (Linux, SDK 8.0.427): bot **120/120**, odłożony AI Lab **181/181**, brak pominiętych i niezaliczonych testów. Oba cBoty i backtester budują się: 0 błędów, 0 ostrzeżeń. Historyczne liczby poniżej nie opisują bieżącego podziału projektów testowych.
- Bazowe TRX: `/home/ubuntu/.hermes/cache/scratch/apex-evidence/baseline-core.trx` i `baseline-ailab.trx` (artefakty lokalne poza Git).
- W toku: przegląd i regresje silnika/persistencji oraz mostów. Brak jeszcze dowodu rzeczywistego połączenia z Pepperstone i uruchomienia aplikacji na Windows.
- Etap panel/pakiet: 9/9 testów Node (7 regresji panelu obejrzanych RED→GREEN, 2 kontrakty pakietu RED→GREEN), sprawdzona składnia JS i 3 skryptów PowerShell. Dodano `APEX.Trading.Demo.sln`; pełny build 0 błędów/ostrzeżeń, ponownie 120/120 testów bota.
- Wykonano rzeczywistą publikację samodzielnego `win-x64` i ZIP przez `Zbuduj-APEX.ps1` w PowerShell 7.6.9 na Linux. Manifest 26 plików poprawny; kontrolowane uszkodzenie pliku i dodanie pliku spoza manifestu poprawnie odrzucone, po przywróceniu ponownie PASS. To pakiet walidacyjny sprzed integracji poprawek bezpieczeństwa, nie wersja końcowa do handlu.
- Panel nie ma odblokowania REAL ani AI Lab, START wymaga potwierdzenia DEMO; nowe działania blokowane przy nieaktualnym/rozłączonym koncie. Centrum decyzji pozostaje pasywną wizualizacją reguł, bez modelu AI.
- Dodano workflow Windows (PowerShell 5.1, test prawdziwego EXE bez brokera, ścieżki ze spacjami) i instrukcję gotowego ZIP bez SDK. Workflow jeszcze nie ma wyniku — nie jest to dowód wykonania na Windows.
- Rozstrzygnięcie: aktualne zlecenie jest specyfikacją i zgodą na autonomiczną implementację, nie wymaga kolejnego zatwierdzania planu. Zachowujemy C#/.NET i architekturę, nie dodajemy zdalnego OAuth zamiast lokalnego mostu. Testy archiwalnego AI Lab wykonujemy oddzielnie, bez pakowania go z botem.

---

## Archiwalny opis poprzedniego wykonawcy (może być nieaktualny)

Plik przekazania pracy dla kolejnego wykonawcy (np. Codex). Opisuje stan projektu, wykonane zmiany, zadania niedokończone i zasady pracy.
Gałąź robocza: `claude/beautiful-mccarthy-9cibo1`.

## 1. Czym jest projekt

Autonomiczny bot tradingowy dla Windows 11 (C#/.NET), z panelem WWW po polsku, łączący się z cTrader (Pepperstone) i MetaTrader 5 przez mosty.
Szczegóły: [`README.md`](README.md), dokumentacja w `docs/`.

| Katalog | Zawartość |
|---|---|
| `src/Apex.Core` (net6.0) | Silnik niezależny od platformy: wskaźniki, reżim rynku, strategie, ranking okazji 0–100, RiskEngine, zarządzanie pozycją, rejestr wykonania, protokół mostu. |
| `src/Apex.Desktop` (net8.0) | APEX Desktop: Kestrel na 127.0.0.1, serwer mostów (WebSocket :5077/bridge dla cTrader, TCP :5078 dla MT5), panel `wwwroot/`, AI Decision Center. |
| `src/Platforms/cTrader` | cBoty: `ApexBridge` (most do APEX Desktop) i `ApexTradingAI` (wersja natywna „wszystko w jednym”). |
| `src/Platforms/MT5` | `ApexBridge.mq5` – most MetaTrader 5 (**nieskompilowany** – brak MetaEditora w środowisku). |
| `src/Apex.AiLab` | APEX AI Lab – moduł automatycznych analiz i aktualizacji przez AI. **Odłożony na później** (patrz punkt 4). |
| `tools/Apex.Backtester` | Backtester na tym samym silniku (walk-forward, holdout, Monte Carlo). |
| `tools/Apex.PlatformSimulator` | Symulator platformy do testów bez brokera. |
| `tools/windows/Zbuduj-APEX.ps1` | Budowa pakietu Windows (testy → publish → cBoty → dokumentacja → `Uruchom APEX.cmd`). |
| `tests/Apex.Tests` | Testy xUnit: jednostkowe, silnika, integracyjne (prawdziwy proces Desktop) i modułu AI Lab. |

## 2. Twarde zasady użytkownika (nie wolno ich łamać)

- Podczas tworzenia i testów **żadnych transakcji na koncie REAL**. LIVE jest zablokowany na 3 poziomach (`LiveTradingFeature` w pliku, parametr mostu, fraza w panelu) i nigdy nie startuje automatycznie. Nie aktywuj LIVE samodzielnie.
- **Kryptowaluty całkowicie wykluczone** (klasyfikator działa „w razie wątpliwości – nie handluj”).
- Zakazane: martingale, grid, uśrednianie straty, zwiększanie pozycji po stracie, transakcje bez SL, ukryta dźwignia.
- Ryzyko: 0,5% na transakcję, 1,5% dziennie, maks. obsunięcie 5%, maks. 3 pozycje, dźwignia ≤ 5.
- Nie zmieniaj RiskEngine, limitów bezpieczeństwa ani aktywnych strategii bez zgody użytkownika.
- Żadnych fikcyjnych wyników testów. Jeśli czegoś nie da się sprawdzić – napisz to wprost.
- Nie usuwaj projektów ani plików użytkownika bez wyraźnej zgody.
- Komunikacja z użytkownikiem, teksty w panelu i dokumentacja: **po polsku**. Komentarze w kodzie po polsku, w stylu otoczenia.

## 3. Jak budować i testować

```bash
dotnet build tests/Apex.Tests/Apex.Tests.csproj -c Release      # buduje Core, Desktop, AI Lab, symulator i testy
dotnet test  tests/Apex.Tests/Apex.Tests.csproj -c Release      # pełny zestaw testów
dotnet build src/Platforms/cTrader/ApexBridge/ApexBridge.sln -c Release        # cBot mostu (.algo)
dotnet build src/Platforms/cTrader/ApexTradingAI/ApexTradingAI.sln -c Release  # cBot natywny (.algo)
```

Na Windows pakiet buduje `tools\windows\Zbuduj-APEX.ps1` (uruchamia testy i przerywa przy błędzie).
Ostatni wynik w tym środowisku (Linux): **301/301 testów zaliczonych**, kompilacja 0 ostrzeżeń.

## 4. Decyzja użytkownika (2026-10-18)

> „dokończ tego bota ale narazie bez tego automatycznego uczenia bota i automatycznej aktualizacji”

Moduł **AI & Auto-Updates (APEX AI Lab)** jest odłożony. Bot ma zostać dokończony i dostarczony bez niego.
Kod modułu zostaje w repozytorium do późniejszego wznowienia, ale nie powinien być częścią pakietu ani panelu.

## 5. Stan prac

### Zrobione
- Silnik, RiskEngine, ochrona wykonania (zapis stanu przed i po zleceniu), filtr wiadomości, klasyfikator instrumentów.
- APEX Desktop z panelem premium, mosty cTrader i MT5, AI Decision Center (tylko odczyt, realne zdarzenia).
- cBoty cTrader kompilują się bez ostrzeżeń; protokół sprawdzony symulatorem.
- Backtester i raport badań (`reports/RAPORT_BADAN.md`): brak strategii z potwierdzoną przewagą – jedna eksperymentalna, tylko DEMO.
- AI Lab: pełny moduł + zakładka; przegląd adwersaryjny znalazł 54 potwierdzone usterki. Poprawki grup **deploy** i **audit** scalone,
  grupa **jobs** scalona częściowo, grupy **ui** i **win** niezrobione – lista w [`docs/AI_LAB_ZALEGLOSCI.md`](docs/AI_LAB_ZALEGLOSCI.md).

### W toku / do zrobienia (kolejność)
1. Wyłączyć AI Lab z produktu: usunąć zakładkę i pośrednika `/api/ai/*` z APEX Desktop, nie budować `ApexAiLab.exe` w pakiecie,
   uprościć `Uruchom APEX.cmd` (bez `wydania\aktualne.txt`), przenieść testy AI Lab do osobnego projektu testowego, który nie blokuje budowy bota.
2. Naprawić skrypt `tools/windows/Zbuduj-APEX.ps1` tak, aby parsował się w Windows PowerShell 5.1 (UTF-8 z BOM, bez półpauz w kodzie).
3. Przegląd i dokończenie samego bota (silnik, mosty cTrader/MT5, Desktop, pakiet Windows) z poprawkami potwierdzonych błędów.
4. Aktualizacja dokumentacji (README, INSTRUKCJA_PANELU, OGRANICZENIA_I_STATUS, WYNIKI_TESTOW, INSTALACJA_WINDOWS).

### Wymaga komputera użytkownika (nie da się sprawdzić w tym środowisku)
- Uruchomienie pakietu na Windows 11, test z prawdziwym cTrader Desktop na koncie DEMO (procedura: `docs/PROCEDURA_DEMO_PEPPERSTONE.md`).
- Kompilacja `ApexBridge.mq5` w MetaEditorze i test na DEMO MT5.

## 6. Wskazówki dla Codex

- Zacznij od `git log --oneline -15` i tego pliku. Przed zmianą uruchom pełne testy, po zmianie – ponownie.
- Zmiany w `src/Apex.Core/Risk`, `Safety`, `Execution`, strategiach aktywnych i profilu `config/profile_default.json` wymagają zgody użytkownika.
- Panel jest dostępny tylko lokalnie (127.0.0.1, nagłówek `X-Apex-Token`, lista dozwolonych hostów, CSP) – nie osłabiaj tego.
- Testy integracyjne uruchamiają prawdziwy `ApexDesktop.dll` na wolnych portach – mogą działać równolegle.
- Pułapka w powłoce: `pgrep -f`/`pkill -f` z wzorcem obecnym we własnej linii poleceń zabija własną powłokę – używaj skryptów.
- Commit: krótki opis po polsku. Nie wypychaj na inne gałęzie bez zgody użytkownika.
