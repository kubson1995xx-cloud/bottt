# Task8A — paczka Linux istnieje, poprawki weryfikatora w toku

## Werdykt niezależnego review: REQUEST CHANGES
Raport `task-8a-delivery-review.md` zgłosił **3 Important, 0 Critical** w nowym weryfikatorze: re-manifested private-map/cache/replay payloads, unlisted case-alias w nested/outer inventory i niespójne frontend TAP log/hash/count references. Copy-only probes potwierdziły luki; review potwierdził też rzeczywisty czysty ZIP, jego SHA/CRC/inventory i zachowane testy. Nie stwierdzono tych zanieczyszczeń w obecnej paczce.

Fix round1 kod `a72e0c0` ma controlled89/89, root18/18 i7/7 exactreviewchecks (control0,6mutantów1). Scoped re-review w toku: `task-8a-fix1-review.md`; brak odbioru. NowyZIP `/home/ubuntu/.hermes/cache/scratch/apex-task8a-fix1/run-20261030T151303Z-d2b504d1/APEX-Linux-Naval-fix1-corrected.zip`,1504950bytes,SHA`ea80280993fe40d20793c3568c4c68c17ba8d32032b646b604625a87bb4eb9dd`,CRC PASS/49files. Parent porównał383sourceinputs i old/newZIP: zmieniło się tylko5tooling/metadatafiles, runtime/assets/logi/docs są bajtowo identyczne. Dlatego wcześniejsze849/126/168/109packaged/100browser są reused, nie nowymwykonaniem. Honestengineeringresealprovenance w metadanych, oldZIP preserved. Odbiór i durablecopy dopiero po scopedreview.

Poniższe dowody kandydata są autentyczne, ale **nie zamykają odbioru weryfikatora**.

## Aktualny kandydat — poprzednie BLOCKED wpisy są historią
Kod pakowania `ba29e41`. **ZIP istnieje, lecz Task8A nie jest jeszcze odebrany** — `task-8a-delivery-review.md` w toku. Wykonanie opisuje `task-8a-delivery-report.md` od124. Parent sprawdził383/383shippedinputhashes, rzeczywiste UnitTestResult i SHA/CRC ZIP-a.

- ZIP: `/home/ubuntu/.hermes/cache/scratch/apex-naval-task8a/run-new-20261030T143435Z-da83b139/APEX-Linux-Naval-final.zip`.
- Rozmiar1502408bytes, SHA256 `05918ff2e8a4e3040be8dae69d7ea92a352dccf6c1c24d5163f20b0bc017287b`, CRC PASS,49regularmembers/48outermanifestentries/4nestedartifacts.
- TargetLinuxx64/glibc framework-dependent, wymaga Microsoft.AspNetCore.App8. Brak WindowsEXE/runtime acceptance.
- Finalintegrated actual849/849bot,126/126platform,168/168Backtester; extracted-package109/109, frontend58/58,build4/4,panel18/18,controlledpackage52/52,notice14/14. To odrębnebramki, nie sztucznie scalonyrun.
- FreshChrome145 z extractedDLL:100passingcheckrecords, nativeBasiccache, pięćwidoków/lazychunkparity/SVGminimal/H503textEmergencyStop/synthetic77onlyclose/durablelatch/restart/defaultzeroaccount. To niebroker/physicalGPU/phonebenchmark. 80packagedprocesses i3browserhosty zakończone, privatefixtures iisolatedcontexts usunięte według zachowanegoreadback.
- Sealedmetadane uczciwie `sourceCommit=dacf8eb`, `sourceDirty=true`, exactsourceinputs. Późniejszycommit tychsamych źródeł nie upoważnia do podmiany ZIP metadata bez ponownychtestów.

Następnie wymagany packagingreview, durablecopy poza scratch i parentacceptance. Nadal brak privateOKX/WS/trustedissuer/realDEMOE2E/WindowsCI/deployment/mobileTLS/profitability. Nie traktować lokalnego hosta historycznych danych jako uruchomionego bota OKX.

## Historia — przerwany pierwszy run

## Aktualna dyspozycja
Częściowa implementacja narzędzi, testów i instrukcji powstała w 15 plikach względem `b14fc3e`; nie jest odebrana ani opublikowana. Nie powstał produkcyjny ZIP. Nie dostarczać kontrolowanego dummy archiwum jako aplikacji.

## Rzeczywiste wyniki
Parent odczytał UnitTestResult w `bot.trx`: **846 wykonanych, 845 PASS, 1 FAIL, 0 skipped**. Failing test: `ScopedHostProcessTests.ProductionEntryBootstrapsCoverageUnknownScopeThenRunsOnlyExplicitTechnicalRequest` — odmowa bezpiecznego zacieśnienia SL i późniejszy timeout. Mechanizm bezpieczeństwa nie został osłabiony. Przyczyna fixture freshness/clock pozostaje hipotezą do diagnozy, nie ustalonym faktem.

Inne wykonane bramki: frontend **58/58**, strict TS/build, build tests **4/4**, panel **18/18**, kontrolowane package fixtures **50/50** po zachowanych **35 fail / 15 pass RED**; solution/SDK build **0 warnings/errors**. Source-only Naval host cases **109/109** są podzbiorem pełnego runu, nie testem rozpakowanej paczki.

Drugi niezależny blocker: genuine zainstalowany `@react-three/fiber@9.6.1` deklaruje MIT, lecz nie zawiera LICENSE/NOTICE. Brak oryginalnej informacji licencyjnej zatrzymuje notice gate; nie zastąpiono jej wymyśloną treścią.

## Aktywne odblokowanie
- Osobna bounded diagnoza process-testu, bez zmian production Core/SL guardów; test-only fix wyłącznie po udowodnieniu przyczyny i RED→GREEN. `task-8a-stop-diagnosis-report.md`.
- Bounded read-only pobranie genuine upstream LICENSE przypiętej do wersji/commita z oficjalnego źródła, z zachowaniem raw bytes/provenance/hash. Bez instalacji, nowych zależności lub zmiany pinów/locka. `task-8a-fiber-notice-report.md`.

Następnie trzeba wznowić istniejącą częściową implementację, nie budować H/UI/R3F ponownie: finalny source-bound integrated gate, product publish/ZIP/SHA/CRC, bezpieczne niezależne rozpakowanie, zgodność manifestów i wszystkich lazy assets, rzeczywiste extracted-package host/browser, cleanup i independent review. Wszystkie te bramki są nadal **NIEWYKONANE**.

## Kontynuacja — kandydat odblokowania, review i pakowanie aktywne
`4f07d7a` zawiera tylko testowy source-clock fix i genuine upstream Fiber notice. Odtworzony race: powtórzona publikacja niezmiennej ceny stemplowała sourceUTC o92.159ms później niż captured engine UTC, więc unchanged futurequote guard odmawiał. Nie starvation. Poprawka preserve lastticktime, bez Core/guardchange; same instrumented host causalRED0/1→GREEN1/1, final uninstrumented target34/34. Parent odczytał TRX i hash source/Core. Stary failed845/846 pozostaje zachowany, nie jest ponownie opisany jako PASS.

Licencja1072bytes została pobrana z commitaprzypisanego oficjalnym npmmetadata do9.6.1 i zweryfikowana byte-for-byte. SHA`9c35b5de7b7493a707fffe4eb23bd2f7f449153c1911f7c6eefb4e591fd5349a`. Parent autoryzuje wyłącznie exactversion/manifest/notice/provenance-pinned builderfallback, nigdy generycznyMIT.

Niezależny unblock review **PASS**, `task-8a-unblock-review.md`, commit `4f07d7a`: spec/quality bez Critical/Important/minor. Parent zaakceptował wyłącznie test-only source-clock correction i version-bound notice, nie całość pakowania. Pakowanie wznowione z częściowych15files (`task-8a-delivery-report.md` append): noticechecks, freshfullbuilder, actualZIP/extract/host/browser. Finalproductbramki nadal nieodebrane do rzeczywistego wykonania i niezależnego packagingreview.

## Artefakty
- `.superpowers/sdd/2026-10-29-apex-naval/task-8a-delivery-{brief,report}.md` — dokładne wymagania i raport BLOCKED.
- `/home/ubuntu/.hermes/cache/scratch/apex-naval-task8a/run-20261030T134453Z-7e689dc3/` — zachowane źródła przed runami, TRX i kontrolowane negative fixtures.
- `APEX-Linux-Naval-final/Weryfikacja/bot.trx` SHA256 `cb35a5489436f6919f50c735a8f57fc56c793e59da4eab44c4b10ee3237a9e3b`.
- `verification-summary-final2.json` SHA256 `bceedc5a088cbe0388fd85ef40ec199c4c75a12d886c5c1e7e8339dc92a418e8`.

Windows CI/artifact/ACL/native browser, rzeczywiste VPS/systemd/mobileTLS, credentials, prywatny OKX/WS/broker reconciliation oraz osobno zatwierdzona transakcja DEMO pozostają poza tym zakresem. Brak LIVE i potwierdzenia rentowności.
