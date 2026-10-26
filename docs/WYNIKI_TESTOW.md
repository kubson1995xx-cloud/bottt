# Wyniki weryfikacji APEX DEMO — autonomia

## Tożsamość dowodu i wydania

Źródła dostawy: gałąź `apex-demo/autonomy`. Rozstrzygający identyfikator to **`sourceCommit` w `apex-build.json` + head SHA konkretnego przebiegu APEX Windows DEMO**, nie nazwa ZIP ani dawny zielony workflow. Pakiet `926afaa` i jego 265/75/11 testów są historyczne, nie pokrywają obecnej autonomii.

Final integration review przyjął Task4, MAIN PhaseC w zakresie źródeł/zachowanej lokalnej funkcjonalności oraz zgodność schema5/certyfikatu/partial. Jedyny Important blokujący Windows dotyczył dwóch założeń separatora w `ScopedHostProcessTests.cs`: wybór Release i znalezienie rodzica `stan`. Naprawiono je przy użyciu natywnego separatora oraz `Path.GetDirectoryName`/`Path.GetFileName`, bez wyłączeń testów, zmian asercji bezpieczeństwa i bez budowania Debug jako obejścia. Nie zmieniono produkcyjnych plików C#/profilu.

## Nowe wykonanie lokalne Task5

Zmiana wejść: tylko dwa wyrażenia ścieżek w harnessie. Brakujący dowód: uruchomienie Release hosta, odmowa odtworzenia brakującego zarezerwowanego stanu i zachowanie ochronnego pełnego close.

```sh
dotnet test tests/Apex.Tests/Apex.Tests.csproj -c Release --filter 'FullyQualifiedName~MissingScopedStateBesideReservedConfigIsNotRecreatedAsFresh|FullyQualifiedName~ProductionEntryBootstrapsCoverageUnknownScopeThenRunsOnlyExplicitTechnicalRequest|FullyQualifiedName~GeneratedLabelCorruptStartupRetainsPublicFullCloseAndForeignPosition' --logger 'trx;LogFileName=focused-paths.trx' --results-directory /home/ubuntu/.hermes/cache/scratch/apex-autonomy/task5-windows
```

**4/4 PASS, 0 failed/skipped**, z przebudową wymaganych projektów Release. W tym dwa warianty closeAll/Emergency, zachowanie obcych pozycji/uszkodzonych bajtów, brak odtwarzania brakującego stanu, rzeczywisty produkcyjny host + syntetyczny lokalny peer: read-only setup → explicit technical request → SL/TP/modify/full close → signed net/terminal manifest → certyfikat/START → restart/reconnect STOP bez drugiego open. To nie broker ani Windows. Pierwsza próba uruchomienia narzędzia miała exit127 (SDK poza PATH); zachowana oddzielnie, nie zaliczona jako wykonanie testów. Po dodaniu istniejącego `/home/ubuntu/.dotnet` do PATH wykonano powyższe przypadki.

## Zachowana historia — bez ponownego pełnego Linux gate

- Task3: jeden historyczny integrated run **569/569 Core/Desktop, 126/126 platform, 50/50 Backtester, 16/16 panel**, JS PASS, solution i oba SDK `.algo` bez warnings/errors.
- Późniejsze zmienione wejścia Task3: **10/10 focused i 60/60 affected Backtester**; nie jest to nowy integrated wynik łączny.
- Task4: **65/65** nowych zachowań sześciu rzeczywistych strategii oraz **4/4** ochronnego wykonania rzeczywistego DefaultProfile → Core → SimWorld (TP3R, SL, oba trafienia SL-first, adverse gap, cash reconciliation). Początkowe 4+1 błędy fixture zachowano; poprawiono kompozycję testu `IsBacktest=true` przed konstrukcją Core, bez produkcyjnego bypassu.
- Task2A/2B i PhaseC: zachowane zaakceptowane testy migracji, trwałego certyfikatu, recovery/replay, dokładnych wolumenów, partial final fence, kompaktacji/replay barriers oraz pełnego close przy awarii zapisu. Ich liczników nie dodajemy do fikcyjnej nowej suite.

Manifest Task4 wiąże 166 inputs (SHA256 `5dd7b96115626a8e031c216a9f61bf7fc05cb3d88888f2ef94c9b4854d88d706`). Task5 celowo zmienia harness i polską dokumentację; pozostałe źródła produktu, domyślny profil i konfiguracja bramek zachowują te same bajty. Stare binaria/uruchomienia nie są przedstawiane jako nowy test Windows.

## Pierwszy rzeczywisty Windows run Task5 — zachowane niepowodzenie pakietu

[40358374972](https://github.com/kubson1995xx-cloud/bottt/actions/runs/40358374972), source `ce2d3ff7e8253cbe3fa73a510ac1c3322a4abc13`: pełny Release build 0 warnings/errors; **634/634 Core/Desktop, 126/126 platform, 64/64 Backtester**, zero failures/skips. Oba poprawione przypadki ścieżek i procesowy synthetic lifecycle przeszły na Windows. Następnie kontrolowany fixture pakietu zatrzymał verifier: `Sciezka poza pakietem`. **Cały run failure; EXE smoke i ZIP nie powstały.** Log/TRX zachowane, to nie zielone wydanie.

Delta naprawcza dotyczy wyłącznie verifiera i jego fixture: kanonizacja katalogu przez to samo `.NET GetFullPath`, które już obejmuje ścieżki plików, plus diagnostyka rzeczywistych porównywanych ścieżek. Kontrola containment nie jest usunięta. Nowe focused fixtures sprawdzają alternate-separator/trailing path i odrzucenie `../outside.txt`; komplet **10 kontrolowanych przypadków PASS na PS7/Linux**, nie Windows proof. Poprzednie 8 kontrolek także przeszło lokalnie przed poprawką; różnica platform pozostaje do potwierdzenia w kolejnym actual runie. Nie powtarzano lokalnych suite produktu.

## Rzeczywisty gate Windows i pakiet

Istniejący workflow **APEX Windows DEMO** na `windows-2022` używa **Windows PowerShell 5.1**. Buduje pełne rozwiązanie Release, uruchamia twardo wszystkie suite produktu (Core/Desktop z procesowymi recovery/certification/SL/TP/net, platform, actual-source Backtester), fixtures bramki pakietu, panel i JS check, publikuje samodzielny `win-x64` oraz zgodne oba cBoty. Następnie uruchamia **prawdziwy opublikowany EXE**: HTTP, token, polski panel, brak AI Lab/REAL, zero kont i zleceń. To wymagane odrębne target-OS acceptance, nie powtórka niezmienionego Linux baseline.

Dokładny URL/status, head SHA, ID artefaktu i suma pobranego ZIP należą do zewnętrznego raportu dostawy Task5. W samym ZIP są rzeczywiste `Weryfikacja/{bot,platform,backtester}.trx` i zgodne z nimi liczniki `apex-build.json`, a `manifest.sha256` wiąże każdy plik. Porównaj te dane z przekazanym przebiegiem; nie uznawaj samego YAML za dowód sukcesu. `windowsRuntimeTested=false` jest celowo zapisane **przed** późniejszym smoke; odczytaj jego wynik w logu tego samego runu, nie modyfikuj oryginalnego ZIP po fakcie. `brokerTested=false` pozostaje prawdziwe.

Archiwalny AI Lab nie jest pakowany i ma odrębną nieblokującą kontrolę z jawnym warningiem/TRX. Historycznie Windows 180/181 (UnauthorizedAccessException w TamperedWorktreePointer), a Linux 181/181; nie poprawiamy tego archiwum i nie nazywamy go zielonym na podstawie testów produktu.

## Strategie i autentyczne badania

**Aktywny tylko IndexDipBuy / Index / Experimental, BUY-only.** D1, SMA100, RSI2<5, SMA5 exit / maks.10 barów, SL3ATR, Core protective TP3R. Pozostałe rodziny są nieaktywne/Rejected; brak automatycznej promocji i brak Validated.

**64/64 autentyczne próby CLI (48 fixed +16 WF) zakończyły się exit2 / success:false w preflight brakujących przedziałów. 0 ocenionych kont/kombinacji.** Brak zweryfikowanych historycznych sesji i anomalne wielotygodniowe luki US500. Nie fabrykowano świec ani nie zalegalizowano wszystkich luk jako świąt. **n/net/WR/PF/opłaty/DD/OOS niedostępne, nie zero.** Brak dowodu przewagi, także dla nowego TP3R; stare CSV/raporty są historyczne. BID→MID, wolumen i koszty są modelowane, nie stanowią potwierdzonych kosztów/egzekucji Pepperstone. Lata 2024–2025 nie są certified untouched holdout. Szczegóły w repo: `reports/autonomy/task4-summary.md` i `reports/autonomy/task4/attempts.csv`.

## Nadal niewykonane — broker i komputer użytkownika

Windows11 użytkownika, instalacja cTrader, rzeczywiste SDK events/history retention/LocalStorage crash durability, hidden writers/rollback, specyfikacja instrumentów, prowizje/FX/margin i świeżość danych wymagają odbioru. MAIN wymaga dedykowanego pustego DEMO hedging, read-only setup i dokładnej etykiety; ScopedReady przy CoverageUnknown nie dowodzi całej historii brokera. Native produkcyjnie odczyt/redukcja, START/TEST nowych wejść odmawiają; MT5 bez MetaEditora/odbioru.

**Bez połączenia z brokerem i bez transakcji DEMO/REAL w Task5.** Rzeczywisty test DEMO wymaga osobnej zgody na konkretne konto; PASS tylko z rzeczywistego fill/protection/modify/requested close i trwałego terminalnego rozliczenia, nigdy z importu syntetycznej próby. Dopiero PASS + fresh scope umożliwia jawny START, restart/reconnect pozostawia STOP. Emergency i pełne close własnego ID pozostają osobne, bez certyfikatu, ale wymagają bezpiecznej tożsamości DEMO i działającego mostu. Limity 0,5%/1,5%/DD5%/x5, obowiązkowe SL/TP; REAL bezwarunkowo zablokowany. Procedura: [PROCEDURA_DEMO_PEPPERSTONE.md](PROCEDURA_DEMO_PEPPERSTONE.md).
