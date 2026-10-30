# Task5A — adapter OKX EEA DEMO tylko do odczytu

## Status
Kod `11fd206`, baza `52731cb`, gałąź `feature/apex-naval-command`. **Review: REQUEST_CHANGES — 6 Important i 1 Minor odtworzone na końcowym DLL. Etap NIEODEBRANY.** Raport `task-5a-okx-read-review.md` wykazał: pomijanie wspólnego UID quota przy bootstrap różnych bindings, brak wykrycia konfliktu pending ordId w stronicowaniu, ignorowanie actual tradeQuoteCcy w diagnostyce, porównanie cash/bills z różnych interwałów, cichą utratę precyzji derived decimal i malformed Unicode wymykający się Failed-read contract. Minor dotyczy pending Limit=1 z pustą stroną. Brak Critical i ujawnionego write/Core authority bypass. W toku jedna fala poprawek z nowymi behavioral regresjami, następnie wymagany scoped re-review. Zachowane 105 zielonych testów nie obejmowały tych przypadków.

Nowa biblioteka `src/Apex.Adapters.Okx/` jest odłączona od Core, Desktop, backtestera i brokera. Nie ma consumer/solution registration, kluczy, magazynu sekretów, order writes ani nowej authority. Dotychczasowy polski panel i cTrader pozostają bez zmian.

## Dostarczony zakres
- Typowane GET-only odczyty na stałym `https://eea.okx.com:443`, bez redirectów, region fallback, proxy lub cookies.
- Signing dokładnych escaped PathAndQuery, jeden frozen credential lease, gate Read-only/UID/binding/IP/spot/borrow. To lokalne wymagania, nie potwierdzenie prawdziwego konta.
- Ścisłe immutable DTO, dokładne decimal bez cichego zaokrąglania, zachowanie opaque IDs, opłat i walut.
- Bounded retry/rate/cancellation/budgets i stronicowanie z prawdziwymi cursorami.
- Snapshoty i diagnostyka sald/fill/bill jako nieufne observations, zawsze bez finansowego admission. `Collected` nie znaczy kompletnej historii. Coverage pozostaje Unknown/Gap/Conflict.

## Zweryfikowane dowody
Parent porównał **20/20 plików** z manifestem SHA256 `700563daed0136ed17faecd4c503def990c7854b161e6ec9f69c09fb72d7617b` i odczytał rzeczywiste UnitTestResult oraz build log:
- końcowy offline: **105/105 PASS**;
- cały nowy projekt: **105 PASS, 1 jawnie SKIPPED** publiczny smoke bez opt-in, zero FAIL;
- standalone library build: **0 warnings / 0 errors**;
- publiczny smoke: **1 PASS**, prawdziwe time i **568 SPOT instrumentów**, bez private headers/sekretów.

Public smoke wykonano na wcześniejszych bajtach przed finalnymi safety refinements, co raport wykonawcy jawnie dokumentuje. Nie jest byte-identical acceptance końcowej biblioteki ani dowód kontowego katalogu, DEMO key/UID/IP, dostępu do USDT, subkont lub zgody na handel. Finalna wersja ma source-bound offline/build evidence. Nie powtarzano ukończonej kampanii ani suite Core/backtestera, ponieważ nie zmieniono ich zależności.

## Pozostałe bramki
1. Niezależny review `task-5a-okx-read-review.md`: auth/allowlist/parser/time/pagination i brak authority. Zielone tests nie zastępują tego odbioru.
2. **Private DEMO smoke BLOCKED — securely configured credentials absent.** Żadnych sekretów w czacie, CLI args ani committed config. Brak provisioning i prywatnych żądań.
3. Późniejsza kompozycja sekretów, podłączenie aplikacji i UI, authenticated issuer/continuity i WS to osobne etapy. Brak automatycznego START i żadnej transakcji.
4. Bez dowodu Windows runtime, real-private connectivity, pełnej historii/final fees lub rentowności.

## Artefakty
- `docs/NAVAL_OKX_READ_ONLY.md`: polska instrukcja API i ograniczeń.
- `.superpowers/sdd/2026-10-29-apex-naval/task-5a-okx-read-{brief,report,review}.md`: wymagania, exact commands/hashes i review po zakończeniu.
- `/home/ubuntu/.hermes/cache/scratch/apex-naval-task5a/`: logi, TRX, source-hashes i wcześniejsza tożsamość public smoke (scratch może wygasnąć).
