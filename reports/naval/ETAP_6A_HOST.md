# Task6A-H — zabezpieczony host odczytu, kandydat nieodebrany

## Status
Kod `a27258f`, baza `8b19b56`, gałąź `feature/apex-naval-command`. **Funkcjonalny kandydat jest zaimplementowany; Task6A-H nie jest jeszcze odebrany.** Trwają niezależny review nowej granicy auth/odczytu i odzyskanie brakującego testu prawdziwej przeglądarki.

Nowy tryb jest opt-in, domyślnie wyłączony. Włączony wymaga prywatnego pliku skrótów sekretów i uprawnień; Operator Basic zachowuje stary panel, ReadOnly bearer ma wyłącznie dwa GET-y Naval. Brak tokenu operatora w HTML w enabled mode. Default legacy mode zachowano. Pasywny reader nie otwiera ownera ani writer lease; snapshot/events korzystają z zaakceptowanego historycznego dziennika, bez nowych finansowych obliczeń, START lub zleceń.

## Potwierdzone wykonania
Parent uruchomił `verify.py` wykonawcy z exit0; sprawdził retained TRX, źródła, bieżące binaria i rzeczywiste HTTP artefakty:
- finalny cały Apex.Tests: **804 PASS, 0 FAIL/SKIP**, w tym **67 przypadków H**;
- panel Node: **17/17 PASS**;
- solution i cached SDK consumers: **0 warnings / 0 errors**;
- po finalnym rebuildzie rzeczywiste CLI → production host: **3/3 PASS**.

Testy realnego produkcyjnego hosta objęły loader rzeczywistych tymczasowych plików digestów, auth/bootstrap/scopes, strict publikację, paginację/retencję/gap/restart, conflict latches, odczyt przy utrzymanym prawdziwym writer lease i niezmienione authority. Operatorowy istniejący EmergencyStop działał przy corrupt Naval source przez syntetycznego sparowanego peera, bez brokera. To nadal HTTP/procesowe testy, nie dowód browser Basic-cache.

Dziesięć niezmienionych producer source/test hashes odpowiada przyjętemu P. Poprzednie binary hashes nie są obecne: build zmienił informational version po doc-only commitach. Wykonawca jawnie uruchomił ponownie obecny CLI→host consumer proof, nie powtórzył finansowych suite na niezmienionych źródłach.

## Wyniki review i browser gate
**Review CHANGES REQUIRED — 1 Important lossy decimal admission.** Oryginalne raw cash z niezerowym sub-precision tail przechodziło fresh production-host jako200 dzięki `decimal.TryParse` rounding i niezmienionym typed digests. Wymagane exact representability503/noeconomics z zachowaniem valid high-scale200; Poprawka `43b4af9` gotowa: exact coefficient/scale admission,36 regresji RED→GREEN; final full840/840, targeted103/103, solutionSDK0warnings/errors i3currentCLI→hostPASS. Parent wykonał verifier exit0, exactmalformed-first503 bez economics oraz valid200 potwierdzone na oryginalnych hashach. Scoped re-review `task-6a-host-fix1-review.md` i final-DLL browser gate `task-6a-host-fix1-browser-report.md` nadal w toku; etap NIEODEBRANY. Brak Critical/auth/writer bypass wykazanego w review.

**Genuine browser gate PASS na Linux Chrome145, kandydat `a27258f`.** Parent zweryfikował exact result hash `e10027bfe1dd8193cc5cea87a558c33419ce2a38a0ca4bc8d8528b76eb11ee9b`, statusPASS,99namedrecords i cleanup0testhosts/0contexts. Native Basiccache działa po usunięciu prywatnego authhandlera, Reader nie dziedziczy Operatora, oryginalne fetches/scripts/status/decision/CSV działają; kliknięty oryginalny EmergencyStop przy Naval503 zamknął tylko synthetic APEX77 i ustawił durablelatch. Nie jest to99unit tests ani final-byte browser proof późniejszej poprawki. Raport `task-6a-host-browser-report.md`.

## Brakujący odbiór i rozstrzygnięcie
1. Implementer nie znalazł lokalnego browser runtime i zwrócił BLOCKED. Parent następnie potwierdził działające `browser_exec`, Chrome **145.0.7632.6** i widoczny filesystem repo. To tylko potwierdzenie narzędzia: łączność loopback, native Basic-cache, stare fetches/CSP i textual EmergencyStop nadal wymagają prawdziwego testu w izolowanych kontekstach. Brak sekretów w tool payloads, URL/HTML, logach, screenshotach lub traces; wyłącznie ephemeral test credentials w prywatnej pamięci/probe.
2. Sekwencja raw/typed: reader tworzy bounded immutable transport object przed porównaniem raw shape, ale dopuszcza go dopiero po strict shape, checksum, semantyce i scope. Ruling rodzica: taki porządek jest dopuszczalny jako implementacja celu „odrzuć przed admission”, o ile konstrukcja nie wykonuje I/O/authority, limity32MiB/depth64 pozostają skuteczne i review nie znajdzie normalizowanego wejścia przechodzącego do output. Koszt pomyłki: konieczna zmiana parsera i ponowne covering gates. To nie zwalnia z strict rejected-input testów.
3. Niezależny review auth/bootstrap/CSRF/ACL/path/parser/ingest/no-authority ma własny raport i może zażądać poprawek. Zielone804 nie zastępuje tego odbioru.
4. Native Windows ACL/browser, faktyczna provisioning użytkownika, mobile/VPS/TLS, private OKX/WS i broker, React/R3F pozostają nieodebrane. Lokalny trusted same-OS-user threat model jest jawny.

## Artefakty
- `.superpowers/sdd/2026-10-29-apex-naval/task-6a-host-{brief,report,review}.md`.
- `.superpowers/sdd/2026-10-29-apex-naval/task-6a-host-browser-report.md` po zakończeniu probe.
- `docs/NAVAL_READ_PUBLICATION.md`: config, auth, read endpoints i ograniczenia.
- `/home/ubuntu/.hermes/cache/scratch/apex-naval-task6a-host/20261030T014020607112Z/`: zachowane RED/superseded/final TRX, source/binary hashes i HTTP/persistence; scratch może wygasnąć.
