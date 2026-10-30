# Task6A-P — trwały dziennik publikacji OFFLINE

## Status
Kod `c58390f`, baza `a8f1b8e` (zmiana względem poprzedniej bazy `1f53901` była wyłącznie w HANDOFF). **Review: CHANGES REQUIRED — 3 Important present-metadata validation defects, etap NIEODEBRANY.** Raport `task-6a-producer-review.md` odtworzył powtórzone fill facts, coverage final sprzeczne z dołączoną rezerwacją i niedozwolone mode/resultcode na prywatnych checksum-valid kopiach. Nie wykazano duplicate cash posting ani zwykłego Append generującego takie dane. Poprawki zapisane w `f5f1715`, scoped re-review w toku (`task-6a-fix1-review.md`), etap nadal NIEODEBRANY. Parent wykonał retained verifier: exit0, 11 source hashes i20CLI artifacts zgodne. Końcowe targeted53/53journal i32/32command, full737/737CoreDesktop i168/168Backtester, solution/SDK0warnings/errors. Wszystkie trzy oryginalne kopie z review są odrzucane przez nowe built CLI exit2 bez zmiany authority/marker i bez result/publication; control nadal exit0. Zachowany poprzedni719/150 wynik jest historyczny, nie current gate. Wcześniejsze zielone wyniki nie pokrywały brakujących przypadków.

Dziennik jest nullable metadanymi tej samej trwałej transakcji EngineState, a nie drugą księgą finansową. Dostarcza typed facts/checkpoint, pasywny odłączony `ReadPublication()`, epoch/cursor/gap i retencję 512 batchy bez przycinania finansowych zobowiązań/dedupe. Stare authority bez journal nie ma rekonstruowanej historii; pierwszy rzeczywisty commit zakłada baseline. Historyczny RUNNING nie udaje bieżącej gotowości.

`naval-shared-replay` ma opcjonalny sanitized atomic `--publication`, z zachowanym dotychczasowym wynikiem komendy i kontrolą ścieżek przed otwarciem ownera. Brak HTTP/nowej usługi, Desktop composition, credentials, brokera lub UI.

## Zweryfikowane lokalne dowody
Parent dopasował **11/11 hashy źródeł** do `sourcehashes-final.json`, odczytał rzeczywiste UnitTestResult i build log:
- targeted Core **35/35**, command **14/14**;
- full affected Core/Desktop **719/719**, Backtester **150/150**, bez failed/skipped;
- solution i cached cTrader SDK consumers: **0 warnings / 0 errors**.

Wykonawca uruchomił rzeczywiste zbudowane CLI i zachował exact authority/publication/result. Revision/AsOf **18 → 23**, cash **10039.16 USD**, te same **3 fill keys**, stały epoch; continuation nie emitowała ponownych filli ani cash. Export failure **exit2** z authority hash bez zmiany; zero-step rebuild **exit0**, publikacja identyczna bajtowo z wcześniejszym zero-step eksportem. To synthetic economics, nie wynik rynku ani broker confirmation. Dokładne artefakty wymagają sprawdzenia przez independent review; sam raport wykonawcy nie jest odbiorem.

## Granice i pozostałe prace
- Wymagany review nowego atomic persistence/publication boundary, strict metadata validation i legacy/finality/retention/export semantics.
- H: passive host reader/endpoints oraz bezpieczna rola read-only jeszcze niezaimplementowane. Anonimowy legacy root udostępniający token operatora wymaga odrębnego rozwiązania access/bootstrap przed udostępnieniem prywatnych odczytów.
- React 5 widoków i R3F to kolejne etapy, nie element tej dostawy.
- Private OKX, WS/continuity, issuer, finansowe uzgodnienie, broker execution/protection i Windows runtime/durability pozostają osobnymi bramkami. Dotychczasowy polski panel, adapter i badania nietknięte.

## Artefakty
- `docs/NAVAL_READ_PUBLICATION.md`: polski schema/API/cursor/CLI i ograniczenia.
- `.superpowers/sdd/2026-10-29-apex-naval/task-6a-read-model-brief.md`: P i późniejszy H, nie łączny odbiór.
- `.superpowers/sdd/2026-10-29-apex-naval/task-6a-producer-{report,review}.md`: exact evidence i werdykt po zakończeniu.
- `/home/ubuntu/.hermes/cache/scratch/apex-naval-task6a-producer/run-cxm4tft7/`: unique sourcehashes/TRX/logi/CLI i retained state (scratch może wygasnąć).
