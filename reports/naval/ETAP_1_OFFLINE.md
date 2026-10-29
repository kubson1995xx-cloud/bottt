# APEX NAVAL COMMAND — odbiór pierwszego etapu offline

## Werdykt
**Audyt + projekt/plan + pierwszy etap offline ukończone lokalnie.** Niezależny reviewer przyjął Task1+2: brak Critical/Important, jeden Minor M1 poniżej. Nie ukończono całej misji A–F, adaptera OKX, operacyjnych flot ani nowego panelu.

Kod oceniony: `6f421651eb9a0cf2fbb07c69faf575343df31a47`, baza `819cba9ba12c14b44577508af83417f70c2e5a86`, gałąź `feature/apex-naval-command`. Repo `/home/ubuntu/projects/apex-trading-ai`. Brak push/PR/publikacji/usług/kluczy/brokera/zleceń. Gałąź pozostaje lokalnie do dalszej pracy; stare gałęzie i worktree zachowane.

## 1. Co zachowujemy
Istniejący Core C# net6, ASP.NET Desktop net8, cTrader Desktop/ApexBridge, polski panel, aktywny profil CFD i dotychczasowe strategie, centralne mechanizmy risk, history recovery, ledger/outbox, scoped admission, trwały execution PASS i partial/journal gates. Nie ma zmian w tych źródłach, cBotach, panelu i config względem nowszej bazy819cba9. Początkowe a886a15 było starszą kopią; nie powielono już ukończonych napraw historii/P&L/FX.

## 2. Co wymaga rozbudowy
Centralny arbiter ryzyka obu flot i pending reservations, jawna kompozycja Crypto/OKX z odrębnym authority, cash-spot units/inventory/fee/FX zamiast semantyki CFD, rzeczywiste historyczne dane UTC i rzetelne badania, trwały telemetry lifecycle z kursorem/epoch/gap, read-only/operator role i minimalny DTO bez sekretów. Istniejąca księga jest fundamentem do rozbudowy, nie czymś brakującym do napisania od zera. Dotychczasowy broker odbiór nadal otwarty.

## 3. OKX bez naruszania cTrader
Opcjonalny moduł C# adapterów, domyślnie off, tylko SPOT cash DEMO, bez short/borrow/lewara/withdrawal/transfer i bez fallback LIVE. cTrader/MT5 Crypto refusal pozostają. Dla Polski/EEA publiczna dokumentacja wskazuje wspólny REST host LIVE/DEMO, więc host nie jest authority: demo credentials i `x-simulated-trading: 1` wymagane przy każdym prywatnym żądaniu, do tego regionalny DEMO WS, sprawdzony account mode i allowlista. Dostępność wszystkich10 aktywów/par i subkont niepotwierdzona; wyłącznie discovery bez zgadywania USDT/USDC/EUR. Faktyczny test transakcji DEMO wymaga osobnej zgody.

## 4. C# i animowany panel
Jedno backendowe źródło prawdy, immutable read model/typowane domenowe zdarzenia, pasywny frontend. React nie nadaje risk admission, nie liczy ledgeru ani nie uznaje HTTP200 za fill. Autoryzowany stream/polling ma account/fleet/venue, freshness, epoch/watermark i gap/resync. Stary `/` działa niezależnie; nowy `/naval/` obok niego. Snapshot nie przekazuje pairing secrets na telefon.

## 5. Architektura wizualna i wdrożenie
React+TypeScript/Vite; R3F/Three lazy-load i lokalne assets, 2D radar/fallback, pięć widoków z designu. Reduced motion/mute/minimal, limit renderowania, cleanup GPU i pauza ukrytej karty. Scanning/signal/locked/submitted nie są wykonaniem; fill tylko potwierdzony, RESULT dopiero settled ledger. Brak źródła/freshness → stale/unknown, nie fikcyjny ruch rynku.

Linux VPS docelowo headless .NET host dla OKX, systemd/restart supervision z STOP/reconciliation, Windows/telefon klientami przez prywatny tunel/TLS/gateway i role. Nie wystawiono portów publicznie ani nie zainstalowano usług. Obecny cTrader pozostaje lokalną integracją Windows, nie staje się Linux/cTrader Cloud przez uruchomienie ASP.NET.

## 6. Etapy i kryteria
Pełny plan `docs/superpowers/plans/2026-10-29-apex-naval.md`, design w `docs/superpowers/specs/2026-10-29-apex-naval-design.md`.
- A: aktualny audyt i plan — GOTOWE.
- B1 / Task1+2: czysty sygnał/replay offline — GOTOWE w opisanej warstwie.
- B2 / Task3: publiczne genuine data, spot fee/FX/fill model i prerejestrowane OOS — NIEZROBIONE.
- B3 / Task4–5: wspólny fleet risk, adapter OKX DEMO read/recovery, później osobno zatwierdzony E2E — NIEZROBIONE.
- C/D / Task6–7: pięć rzeczywiście podłączonych widoków i animacje z potwierdzonych zdarzeń — NIEZROBIONE.
- E/F / Task8: odporność/restart/performance, świeży Windows pakiet i Linux target smoke/instrukcje — NIEZROBIONE dla Naval.
Żaden poniższy wynik offline nie zalicza Pepperstone/OKX DEMO, Windows11 użytkownika, MT5 ani rentowności.

## Co rzeczywiście działa teraz
- `src/Apex.Core/Naval`: czysty evaluator i niezmienne badawcze manifesty ALFA BTC/ETH oraz BRAVO BTC/ETH/SOL/XRP/DOGE/ADA/LTC/LINK/BCH/AVAX.
- Long-only close>previous40high; exit long close<previous30low; filtr BTC close>SMA200 dla nowych wejść, nie wyjść; strict equality nie wyzwala. UTC/closed/confirmed/ciągłość i cutoff; przyszłe OHLCV nie zmieniają decyzji ani as-of ID.
- `tools/Apex.Naval.Replay`: lokalny wersjonowany JSON → decyzje OFFLINE/NO_EXECUTION z SHA256; żadnego holdings inference, wykonania, sizingu lub P&L. Instrukcja `docs/NAVAL_OFFLINE.md`.
- Nie zarejestrowano tych nowych reguł w runtime StrategyBook/START; nie działają jeszcze jako operacyjne floty. Limit2 i ATR14/SL2ATR/TP3R to badawcze metadane, nie udawany admission/risk.

## Dowody wykonania i ich zakres
### Nowy projekt testowy — zachowany wynik, zweryfikowane źródła
`dotnet test tests/Apex.Naval.Tests -c Release --no-restore --logger trx`: **65/65 PASS**, 36 evaluator +29 CLI, zero failed/skipped/warnings/errors. Rodzic i reviewer odczytali rzeczywisty TRX oraz porównali **52/52** source/project/doc/fixture inputy z manifestem, wszystkie identyczne. Nie uruchamiano bez potrzeby tego samego zestawu drugi raz. Behavioral RED zapisany dla reguł, CLI, precyzji SMA i explicit UTC; błędna opcja restore/compile nie była nazywana behavioral RED.

Dowody `/home/ubuntu/.hermes/cache/scratch/apex-naval-offline/`, manifest `evidence-manifest.json`. Manifest buildInputs digest `30514554b0d7a60e90160da3ad3ded42d16c03b2c611e012c050927b89496e4e`.

### Rodzicielski affected gate — nowy run
`/home/ubuntu/.dotnet/dotnet test tests/Apex.Tests -c Release --no-restore --logger trx --results-directory .../affected-core`: **634/634 PASS**, zero failed/skipped. Pętla źródeł/parametry produktu niezmienione; test konieczny, ponieważ dodane pliki Core zmieniły jego DLL.

`dotnet build APEX.Trading.Demo.sln -c Release --no-restore`: **PASS, 0 warnings/errors**, oba actual SDK cBoty DLL/.algo zbudowane. Pierwsza próba miała NETSDK1004 (brak assets istniejącego Backtester.Tests), nie błąd kodu; rozwiązano `dotnet restore APEX.Trading.Demo.sln --source /home/ubuntu/.nuget/packages`, bez sieci i bez zmian produktu. Zachowano oba logi.

Dowody `/home/ubuntu/.hermes/cache/scratch/apex-naval-parent-verification/commands.json`, `affected-core/*.trx`, `solution-build-restored.log`, `verified-worker-evidence.json`.

### Actual executable / synthetic smoke
Wykonawca actual CLI exit0/0: identyczne bajty outputu. Rodzic niezależnie uruchomił `dotnet run --project tools/Apex.Naval.Replay -c Release --no-build -- --input tests/Apex.Naval.Tests/fixtures/synthetic/naval-replay-v1.json --output <nowy-plik>` i odczytał **4 decyzje**: ALFA ETH EntrySignal; BRAVO SOL EntrySignal; ALFA ETH ExitSignal bez BTC; BRAVO ADA Wait/MissingBtcData. Mode OFFLINE, execution NO_EXECUTION. Invalid-schema smoke exit2, brak outputu.

Input fixture SHA256 `c9f9814c1834328db4773cf9f18612ce89927cd35d470082038bdc983a9d569a`; oba outputy wykonawcy `3be8515bd2516f12adf9acf690a2c22d28ba57c8e83f5c33e4fafd857156617b`. Fixture to SYNTHETIC, nie pobrane ceny ani wynik finansowy.

Po commicie i solution build zmienił się hash wspólnego Core.dll; AssemblyInformationalVersion zawiera nowe SHA6f42165. Zachowane kopie obok replay/testów mają pierwotny hash wykonawcy. Weryfikacja jawnie wykazała 7/8 identycznych artefaktów przy 52/52 identycznych inputach. Nie twierdzimy, że wszystkie binaria są identyczne; source-bound65 i nowy634 to osobne dowody, nie fikcyjna suma testów.

### Poprzedni Windows baseline — reuse, nie nowy Naval Windows run
Windows run40360216699 /source819cba9: 634 bot /126 platform /64 backtester /16 panel PASS i EXE smoke PASS. Niezmienione platform/panel bez kolejnego lokalnego full runu. Archiwum AI Lab180/181, jawnie nieblokujące i poza produktem. Brak nowego Windows ZIP Naval/OKX; stary artefakt tego nie dowodzi.

## Niezależny przegląd i jawne decyzje
Reviewer zaakceptował slice; pełne review w lokalnym ledger workspace `offline-review.md`. Wykonano bounded failure injection stdout `/dev/full`, nie powtórkę full suite.

**M1 — odłożony Minor:** awaria stdout po udanym rename raportu zwraca exit2 i błędny komunikat „no replay published”, mimo że pełny JSON istnieje. Nie dotyczy sygnałów, inputu ani zleceń; przy takim błędzie najpierw sprawdzić plik. Poprawka komunikatu może być osobnym małym zadaniem, nie ukrytą zmianą po zielonych testach.

Decyzje wykonawcze (jawne ograniczenia, nie uzgodnione parametry inwestycyjne):
1. earliestExecutionUtc=signal close+1 tick .NET jest wyłącznie dolną granicą po close, nie faktycznym next tick/fillem; późniejszy cutoff nie uprawnia do realizacji wstecz. Koszt błędnej interpretacji: niepoprawny przyszły model fill, dlatego execution wymaga oddzielnej specyfikacji.
2. Walidowany cały użyty zamknięty prefiks UTC, bez sortowania/naprawy; future OHLCV/confirmed ignorowane, struktura JSON/timestampy nadal prawidłowe. Koszt: konserwatywna odmowa na historycznej luce, brak brokerowego kalendarza.
3. BTC i coin muszą mieć ten sam quote i dobę. Koszt: odmowa dopóki nie powstanie jawny FX/model danych.
4. Lookbacki konfigurowalne1..10000, defaults40/30/200; ATR/SL/TP/limity tylko research metadata. Koszt: testy parametrów nie są dowodem OOS ani bezpiecznego sizingu.
5. Odmowa wszystkich istniejących outputów oraz URI/UNC/symlink/reparse, nie tylko input overwrite. Koszt: nowa nazwa output przy każdym runie, konserwatywne ograniczenia filesystemu.

## Następny nieukończony deliverable
Task3: publiczne, regionalnie poprawne discovery instrumentów i genuine UTC candles, dataset provenance/coverage + rzeczywisty model spot/kosztów/FX w istniejącym backtesterze. Nie wznawiać ukończonych Task1+2 od zera. Task4 risk i Task5 private DEMO muszą poprzedzić operacyjny handel flot; animacje nie mają obchodzić tych bramek.
