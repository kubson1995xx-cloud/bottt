# Naval — trwała publikacja OFFLINE (Task6A-P)

## Granica produktu

Publikacja jest wyłącznie **OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY**. To historyczny checkpoint wspólnego kapitału syntetycznego i ograniczony dziennik zaakceptowanych operacji. Nie jest brokerowym kontem, notowaniem live, księgą wyniku ani potwierdzeniem zlecenia. Nie nadaje uprawnień do START, wystawiania receiptów ani execution. Producent P nie dodaje auth ani UI; kandydat pasywnego hosta H jest opisany niżej i pozostaje **nieodebrany** do czasu prawdziwego testu przeglądarki.

`EngineState.Naval` nadal jest jedynym źródłem cash, filli, rezerwacji i zobowiązań. Nullable `EngineState.NavalReadJournal` jest metadanymi **tej samej rewizji i tego samego Set+Flush**, a nie drugim finansowym plikiem/ledgerem. CFD zachowuje SchemaVersion5 i domyślne null obu pól Naval. StateDigest starego `NavalPortfolioState` nie obejmuje nowego pola envelope i nie został zmieniony.

## API Core i kontrakty

- `NavalPortfolioCoordinator.ReadPublication(): NavalReadPublication?` — pasywny, odłączony immutable capture ostatniego **zatwierdzonego** stanu. Null oznacza brak baseline producenta. Odczyt nie wykonuje Reconcile/ForFleet/Start ani migracji. Kolekcje wszystkich checkpointów/batchy/flot/faktów mają prywatne kopie i blokują mutację; zagnieżdżone wartości są immutable rekordami, bez mutable dictionaries.
- `NavalReadPublication`: schema `naval-read-publication-v1`, classification, `Journal`, `AsOfSequence`, `FinancialRevision`. FinancialRevision jest rewizją envelope (`DurableRevision` w authority); nie dodano mylącego drugiego pola rewizji. AsOfSequence = LastSequence = checkpoint watermark publikacji, nie numer receiptu.
- `NavalReadJournalState`: schema `naval-read-journal-v1`, opaque GUID-N `Epoch`, `ScopeId`, `BaselineRevision`, `LastSequence`, `OldestAvailableSequence`, pełny najnowszy `Checkpoint`, maksymalnie512 `Batches` oraz `IntegrityDigest`. ScopeId wiąże portfolio, config digest i pełne członkostwo. Pojedynczy event ma ID `epoch:sequence`, fakt `epoch:sequence:ordinal` (ordinal od0).
- `NavalReadCheckpoint`: FinancialRevision, PortfolioId z reportingCurrency, ConfigDigest, SourceGeneration/SourceSequence, ReconciledUtc, **DurableMode**, bez RuntimeMode/CanStart; latches, bezpieczny reasonCode, rachunki syntetyczne i rezerwacje/fille pogrupowane wg floty. Istniejące zaakceptowane wartości są kopiowane, nie przeliczane nową księgą. `HistoricalLiquidationEquity` jest wyłącznie historyczna i ma currency. CurrentQuality, RuntimeReadiness i Coverage pozostają UNKNOWN; BrokerState NOT_CONFIGURED; BrokerAccountId, BrokerEquity, RealizedPnl i UnrealizedPnl są null. Brak WR/PF oraz wyników wyprowadzanych z cashflow.
- Rachunkowe Seed/Cash i historyczne equity są `NavalReadMoney(amount,currency)`. Każda rezerwacja i fill mają jawne baseCurrency/quoteCurrency; native immutable Reservation/Intent/Fill zachowują dokładne ekonomiczne pola Core: quantity/pending/filled/inventory w base, ceny i allocatedCost/EntryHold/CloseFeeHold/RiskHold w quote; fee zawsze z własną feeCurrency (base lub quote), nigdy domyślna waluta raportu. Holdy pochodzą z zaakceptowanego Core `Holds`, nie z nowej formuły read modelu.
- `NavalReadBatch`: Sequence/EventId/FinancialRevision, nullable CaptureUtc/ReceiveUtc, CommittedUtc, source generation/sequence i atomowa lista Facts. CommittedUtc używa **kontrolowanego źródłowego zegara Offline**, nie zegara przeglądarki ani mtime pliku. Batche bez nowej ekonomii mogą mieć pustą listę faktów.
- `NavalReadFact`: zamknięty enum Kind, FactId, opcjonalne scope keys i wyczerpujący typed payload Reservation/Fill/Coverage/Operation/Intent, walidowany względem Kind. Nie używa object/JsonElement. Refused intent zachowuje rzeczywistą syntetyczną intencję i bezpieczny kod odmowy.

Wewnętrzne `Append(previous,candidate,cause,committedUtc,definition)` jest czyste i wymaga immutable definition wyłącznie do poprawnych walut instrumentów. To konkretne doprecyzowanie czteroargumentowej propozycji briefu: walut base nie da się wiernie odtworzyć z samego starego stanu. Cause ma jawną operację/wynik i metadata przyjętego receiptu, ale **nie OwnerToken**, permit ani surowe conflict evidence. Epoch jest dostarczany przez coordinator i zapisywany dopiero w pierwszym realnym commicie; Append nie robi I/O ani losowania.

## Fakty i finalność

Dozwolone Kind: OfflineReservationAccepted/Refused, OfflineFillAccepted, OfflineEntryCoverageAccepted, OfflineReductionCoverageAccepted, OfflineReconciliationAccepted, OfflineSafetyChanged, OfflineModeChanged.

FillAccepted emituje wyłącznie nowy indywidualny fill, który przeszedł walidację **całego** kandydata. Jeśli kolejny fill, marks/cash/observations lub final coverage unieważnią receipt, commit STOP nie zawiera żadnej części odrzuconej ekonomii. Konflikt nie staje się sukcesem. Dokładne receipt/command duplicate nie wykonuje commitu i nie zwiększa watermark. Nowy zaakceptowany receipt pokrywający już istniejący fill może zwiększyć rewizję/batch, ale nie publikuje filli ponownie i nie debituje cash ponownie.

Pending=0, inventory=0 ani finalne entry same nie kończą lifecycle. Nowy nonfinal reduction zachowuje ReductionUnsettled, close-fee hold i slot. Dopiero rzeczywiście zaakceptowane complete+final coverage daje `offlineLifecycleFinal=true` przy pending=0, inventory=0, TerminalCoverage=true i ReductionUnsettled=false. To **nie** monetary settlement ani realized P&L. Brak OrderSubmitted, broker FillConfirmed, ProtectionVerified, PositionFinalized i animacji zwycięstwa. Requested SL/TP nie są dowodem ochrony.

## Durability, stary stan i walidacja

Owner zachowuje prywatny exclusive writer lease, required-field i financial checks, istniejący StateDigest, exact revision i freeze po niejednoznacznym Set/Flush. Publikacja staje się widoczna dopiero po sukcesie trwałego commitu. Przy błędzie przed Set pozostaje stary pełny envelope; przy błędzie po Set/Flush restart może odczytać nowy pełny envelope ze stabilnymi event IDs. W procesie nie publikuje się optymistycznego batcha i nie próbuje rollbacku.

Brak/null journal w zaakceptowanym Schema5 jest dozwolony **po wszystkich starych kontrolach**. Nadal brakuje `ReductionUnsettled` → odmowa. Odczyt legacy nie zmienia ani bajtu authority. Pierwszy późniejszy commit tworzy nowy epoch i BaselineRevision równy poprzedniej rewizji, ale emituje wyłącznie aktualne fakty; checkpoint może zawierać stare fille, dziennik nie odtwarza ich lifecycle. Zero-step eksport bez baseline zwraca błąd `PRODUCER_NOT_INITIALIZED`, nie pustą rzekomo kompletną historię.

Obecny journal jest walidowany fail-closed: wymagane/nieznane/duplikowane pola, literalne UNKNOWN/null broker semantics, schema, epoch, scope, integralność, baseline/revision/sequence/retention, tożsamości batch/fact, discriminator/payload i zgodność checkpointu z finansowym stanem. Kolejny commit nie może podmienić zachowanych historycznych eventów ani epoch. Integralność jest checksumem na **zaufanym lokalnym filesystemie**, nie podpisem, broker provenance ani ochroną przed złośliwym tym samym użytkownikiem OS.

Retencja512 dotyczy tylko batchy. Najnowszy pełny checkpoint pozostaje niezależny; finansowe Fills/Receipts/Commands, reservation tombstones i zobowiązania nie są przycinane.

## Cursor/paging

`NavalCursor(Epoch, ScopeId, Sequence).Encode()` i `.Decode(string)` używają kanonicznego base64url bez paddingu, JSON tuple `["naval-cursor-v1",epoch,scopeId,"sequence"]`, do2048 znaków. Counter mieści nieujemny Int64 i jest stringiem, również powyżej JS safe integer. Cursor jest pozycją, nigdy poświadczeniem.

`NavalReadJournal.ReadPage(publication,cursor,limit=100)` jest jedynym czystym helperem, przeznaczonym również dla późniejszego H. Limit1..100; malformed tuple/encoding/limit → ArgumentException (późniejsze H:400). Page zawiera Epoch/ScopeId/OldestAvailableSequence/AsOfSequence/NextCursor/HasMore/Gap/ResetRequired/Batches. Wynik idzie od najstarszego, **ściśle po** cursor.Sequence. NextCursor wskazuje ostatni zwrócony batch, nie head; pusty sukces zachowuje requested sequence. Inny epoch/scope, ahead-of-head, cursor<oldest-1, brak środkowego batcha lub konflikt identity/integrity → gap+resetRequired, bez batchy. Potrzebny świeży snapshot. Początkowy brak cursora nie jest żądaniem replay; konsument najpierw hydratuje snapshot bez one-shot effects. Jeden batch jest redukowany atomowo.

## Lokalny eksport i odtworzenie po awarii

Z katalogu repo, bez restore/network:

```sh
/home/ubuntu/.dotnet/dotnet tools/Apex.Backtester/bin/Release/net8.0/Apex.Backtester.dll naval-shared-replay \
  --input tests/Apex.Backtester.Tests/fixtures/synthetic/naval-shared-replay-v1.json \
  --state-dir /prywatny/scratch/state \
  --output /prywatny/scratch/result.json \
  --publication /prywatny/scratch/publication.json
```

Ostatnia opcja jest opcjonalna; stary format result i trzy wymagane opcje pozostają bez zmiany serializera. `SharedPublicationExport.Write(path,publication,inputSha256,coreSha256,backtesterSha256,sourceClockUtc)` nie otwiera ownera. Eksport bierze committed capture z już istniejącego replay/coordinatora, nie summary `Run()`.

Osobny serializer eksportu zapisuje wszystkie Int64 revision/sequence/generation i decimal jako invariant strings, enum jako string; brak named floats. Numery w prywatnym authority/legacy result nie są tym wire formatem. Późniejszy H musi zachować string contract, nie stosować domyślnego number serializer do public DTO ani JS Number do pieniędzy. Envelope ma schema `naval-shared-publication-export-v1`, temporalClassification HISTORICAL_OFFLINE_CHECKPOINT, sourceClockUtc, sourceFreshness UNKNOWN, provenance z trzema SHA256, publication i contentSha256 (SHA256 pretty-printed UTF8 JSON tych samych pól w tej kolejności **bez** contentSha256, serializer eksportu). Test sprawdza roundtrip, hash i exact bytes fields. Nie zawiera inputPath, state directory, config secrets, raw conflicts/receipts, logów ani tokenów.

Path guard działa **przed otwarciem ownera**: lokalna ścieżka, bez URI/reparse/symlink, rozdzielona od authority/input/result, także root/trailing separator/native case; sibling authority jest dozwolony. Nie ma publicznego endpointu przyjmującego path. Plik candidate ma unikalną nazwę w tym samym katalogu, na Unix prawa0600 już przy utworzeniu, flush(true), potem atomic same-directory rename. Błąd sprząta candidate; nie modyfikuje authority ani holdów. Jest to odtwarzalny artefakt publikacji, nie drugi crash-consistent finansowy store. Rodzic katalogu/ACL i lokalny writer są zaufaną granicą; Windows ACL wymagają osobnego dowodu na Windows.

Command zwraca2 przy awarii eksportu, nawet jeśli ekonomia została wcześniej trwale przyjęta. **Nie powtarzaj operacji finansowych w celu naprawy eksportu.** Przygotuj ten sam manifest/config z `steps: []`, uruchom na tej samej authority z nowym prawidłowym --publication. Authority/revision/fille pozostają niezmienione; odbudowany plik zawiera historyczny committed checkpoint. Provenance inputSha256 dotyczy manifestu tej konkretnej komendy odbudowy, nie wszystkich historycznych receiptów; Core/tool hashes opisują binaria eksportera. Nie są brokerowym poświadczeniem pochodzenia każdego dawnego eventu.

Eksport nigdy nie staje się live-fresh od mtime, reload, poll lub zegara hosta. P przyjmuje logical sourceClockUtc; hostEpoch, observed/served times oraz freshness/age policy wymagają późniejszego H. DurableMode RUNNING po restarcie nie daje runtime readiness ani START.

## Kandydat Task6A-H — lokalny pasywny host (nie acceptance)

Implementacja w istniejącym `ApexDesktop.dll` jest opt-in. **Nie włączono jej w żadnej konfiguracji użytkownika i nie dostarczono prawdziwych poświadczeń.** Status i dokładne artefakty: [raport H](../.superpowers/sdd/2026-10-29-apex-naval/task-6a-host-report.md) (lokalny artefakt SDD; katalog jest gitignored). Nie ma nowego UI, drugiego listenera, writer endpointu ani prawa do handlu Naval.

### Konfiguracja bez sekretów

Opcjonalne pole `HostConfig.NavalRead` w `apex.config.json` ma dokładną pisownię poniżej. Brak sekcji albo `Enabled=false` zachowuje stary lokalny panel, token bootstrap i brak tras Naval. Tryb domyślny nie jest wdrożeniem odizolowanej roli ReadOnly. Nie wystawiaj go czytelnikowi jako alternatywnej anonimowej bramy Operator.

| Pole | Kontrakt |
|---|---|
| `Enabled` | Wymagany bool, domyślnie false w obiekcie opcji. |
| `CredentialFilePath` | Dla enabled: pełna stała lokalna ścieżka do istniejącego prywatnego pliku digestów. |
| `PublicationPath` | Pełna stała lokalna ścieżka eksportu; null/brak oznacza świadomie niepodłączone konto. Nieistniejący wskazany plik jest awarią503, nie pustym kontem. |
| `Scope` | Dla enabled kompletny obiekt `Id`, `PublicationScopeId`, `PortfolioId`, `ConfigDigest`, `Accounts`. |
| `Scope.Id` | Selector1..64 ASCII alfanumeryczne/`-`/`_`, ordinal, jedyny obsługiwany zakres. |
| `Scope.PublicationScopeId` | Dokładne64 hex z zaakceptowanego P; bez zmiany case lub naprawy. |
| `Scope.PortfolioId` | Obiekt Core: `PortfolioId`, `ReportingCurrency`. |
| `Scope.ConfigDigest` | Dokładny64-hex digest konfiguracji producenta. |
| `Scope.Accounts` | Niepusta lista różnych pełnych tożsamości Core: `Venue`, `Environment`, `AccountId`, `QuoteCurrency`; całe członkostwo publikacji, bez redakcji pojedynczego konta pod wspólnym cursorem. |

Nowa sekcja i zagnieżdżone obiekty odrzucają nieznane, zduplikowane i brakujące wymagane pola przed permissive deserializacją starego configu. Nie zmieniono zgodności starych CFD configów. Opcje runtime i mapę ról kopiuje się raz przed AccountManager, TCP bridge, news i HTTP bind. Błąd daje exit2 z zamkniętym kodem `NAVAL_CONFIG_INVALID`, `NAVAL_CREDENTIAL_INVALID`, `NAVAL_PATH_INVALID`, `NAVAL_PERMISSIONS_INVALID` albo `HOST_CONFIG_INVALID`, bez JSON-u, ścieżki i wyjątku. Host nie tworzy pliku credential/publication ani nowej authority.

Obie ścieżki muszą być poza source/webroot/publish/package/authority. Nie są przyjmowane z żądania, env ani argumentu roli. Brak URI/UNC, relatywnej ścieżki, rozszerzania zmiennych, globów i komponentów symlink/reparse; brak fallback search. Kontrole rodziców odrzucają `.naval-owner.lock` i `.naval-initialized` **bez otwierania locka**. Właściciel wdrożenia nadal odpowiada za lokalny filesystem i jawnie rozdzielony katalog eksportu.

Unix: prywatny bezpośredni katalog0700; credentials0600; publication0400 lub0600. Dostępne dla obcej grupy/świata writable ancestors są odrzucane. Ancestor pod wcześniejszym0700 nie jest dostępny dla innego UID i nie zwiększa uprawnień (np. prywatny Hermes scratch); ten sam użytkownik OS/root jest zaufany. Nie ma wyjątku omijającego prawa na `/tmp`. Windows: używane są istniejące API ACL, kontrola ownera, DACL i wszystkich dziedziczonych grantów; prywatne cele tylko aktualny użytkownik/SYSTEM/Administrators, bez obcych modyfikacji ancestorów. Nieudowodnione ACL są odrzucane. **Brak natywnego wykonania Windows — nie potwierdzono zgodności typowych katalogów Windows, działania ACL ani browser cache na Windows.**

### Prywatny plik `naval-local-access-v1`

To opis schematu, nie fixture do wdrożenia i nie provisioning:

- Dokładnie `schema: "naval-local-access-v1"` i `principals` (1..16).
- Każdy principal ma dokładnie `id` (unikalny identyfikator1..64 ASCII alfanumeryczne/`-`/`_`), `role` (`Operator` albo `ReadOnly`), `secretSha256` (64 **lowercase** hex SHA256 dokładnego sekretu ASCII) i `allowedScopeIds` (dla H dokładnie jedyny skonfigurowany selector, bez duplikatów).
- Co najmniej jeden Operator i jeden ReadOnly; brak powtórzonych digestów również między rolami. Nieznane/duplikowane/brakujące/null pola, nadmiar64KiB i depth>8 są błędem.
- Sekret wejściowy: kanoniczny **unpadded base64url dokładnie32 losowych bajtów**, czyli43 ASCII. Nie ludzki password, stary ApiToken ani PairingKey. Administrator musi pozyskać entropię z prywatnego bezpiecznego narzędzia. Plik przechowuje wyłącznie digest, nie reusable plaintext.
- Operator: przeglądarkowy/native HTTP Basic, login=`id`, password=sekret. ReadOnly: Bearer w nagłówku klienta. Basic z credentialem ReadOnly nie staje się Operatorem, Bearer Operatora nie zastępuje Basic. Role/granty pochodzą wyłącznie z prywatnej mapy serwera.
- Authorization≤4096 ASCII; dokładnie jedna poprawna wartość, bez combined/multiple. Kandydat ma walidację kanonicznego kodowania przed SHA256 i fixed-time porównanie bajtów digestu. Niepusty `X-Apex-Token` w enabled jest odrzucany; pusty legacy nagłówek panelu jest dozwolony.
- Mapa jest zamrożona do restartu. Prywatna atomowa wymiana pliku + restart służy rotacji/revocation; GET nie reloaduje mapy. Brak API zarządzania credentialami.

Sekret nigdy nie trafia do HTML/meta/JS/bootstrap, URL, query/fragment, commita, logu, raportu ani screenshot/trace. Testy używają wyłącznie efemerycznych RNG sekretów i digestów w prywatnym scratch, konstruują Authorization w procesie testowym i usuwają procesy/pliki w cleanup. Realne dane administrator przekazuje przez prywatny manager/prompt — nigdy przez chat.

### Granica wejścia i stary panel

Pozostaje IPv4 loopback i dokładny Host `127.0.0.1:<efektywny-port>` lub `localhost:<efektywny-port>`, istniejący CSP, DENY, nosniff i no-referrer. Enabled dodaje no-store również do błędów. Brak CORS i zaufania do forwarded host/user/role.

W enabled anonymous/nieprawidłowy credential daje401. Root/static/legacy challenge to `Basic realm="APEX Local Operator", charset="UTF-8"`; dwa zasoby Naval challenge Bearer. ReadOnly ma wyłącznie dokładne dwa GET-y poniżej; root, raw index, assets, legacy APIs, zapisy i nieznane ścieżki dają403, a usunięcie Bearera wraca do401, nigdy HTML-u Operatora. Operator dostaje stary panel z **pustym meta apex-token** zarówno na `/`, jak i `/index.html`. Nie ma sekretu JS ani token fallbacku. Same-origin przeglądarka ma używać natywnego Basic cache; to wymaga osobnego rzeczywistego testu, nie dowodu HttpClient/VM.

Tylko dokładne `/bridge` zachowuje osobne natywne pairing hello i loopback/no-Origin. Każdy HTTP Authorization albo Origin na tej ścieżce daje403; `/bridge/...` i case aliases nie omijają entry policy. TCP pairing nie jest zastępowane web credentialem. Startup log w enabled nie drukuje PairingKey. Uprzywilejowane stare `/api/status` pozostaje starym kontraktem Operatora; nie jest DTO Naval.

Dla unsafe legacy metod Basic nie omija CSRF: obcy/null Origin, inny port i `Sec-Fetch-Site: cross-site` dają403 przed wykonaniem. Dokładny same-origin albo natywny klient bez browser headers może użyć Basic; POST nadal wymaga application/json (inaczej415). Stary `emergency` na oryginalnej account route nie zależy od czytelności publikacji. Nie dodano Naval START/command/receipt/issuer/financial endpointu.

### Dwa kontrakty GET

- `GET /api/naval/snapshot?scope=<selector>&requestGeneration=<correlation>`.
- `GET /api/naval/events?scope=<selector>&hostEpoch=<snapshot-hostEpoch>&cursor=<canonical>&limit=<1..100>&requestGeneration=<correlation>`.

Scope pominięty wybiera jedyny skonfigurowany. Obcy selector lub poprawnie zakodowany obcy scope cursora →403 przed file capture/ReadPage. Nieznane/duplikowane query →400. Opcjonalne requestGeneration1..64 ASCII alfanumeryczne/`-`/`_` jest tylko echem korelacji, nie principalem, licznikiem ani ochroną nowego UI. Events wymaga hostEpoch GUID-N i kanonicznego Core cursora; limit domyślnie100, kanoniczny integer1..100. Brak/malformed →400. Nie ma aliases `/naval`, case/trailing-slash resource aliases ani zapisów; Operator nieznana Naval route404, non-GET na istniejącym zasobie405 (`Allow: GET`), ReadOnly403, anonymous401.

Dedicated JSON ma schemaVersion `naval-host-read-v1`, hostEpoch, scope, requestGeneration, servedUtc, observedUtc, availability/reasonCode, classification/temporalClassification, epoch/scopeId, sourceGeneration/sourceSequence, snapshotVersion/asOfSequence/financialRevision, sources i broker. Counter/revision/generation oraz decimal money/quantity nadal są **stringami**, nie JS Number. `snapshotVersion=FinancialRevision`; `asOfSequence=LastSequence`, bez wersji odświeżania.

Snapshot dodaje dokładny immutable checkpoint, oldestAvailableSequence i zakodowany cursor na head. Events dodaje publiczne Core `page`, `nextCursorEncoded` oraz top-level gap/resetRequired/batches. `page.nextCursor` zachowuje Core `{epoch,scopeId,sequence}`; batche są earliest-first ściśle po cursorze, niezmienione/atomowe; pusty sukces nie przesuwa pozycji. Publiczny `NavalReadJournal.ReadPage` jest jedynym paging algorytmem.

| Stan | HTTP / treść |
|---|---|
| Poprawna historyczna publikacja |200, pełny odłączony capture; nigdy broker READY. |
| Inny journal epoch / cursor ahead / retention gap |200, page gap/reset=true, brak batchy. |
| Inny hostEpoch |409 `HOST_EPOCH_CHANGED`, gap/reset=true, brak batchy, wymagany nowy snapshot. |
| PublicationPath null |Snapshot200 `NOT_CONFIGURED / NO_PUBLICATION_CONFIGURED`, checkpoint/cursor/finanse null; events200 `SNAPSHOT_UNAVAILABLE`, page null/reset=true. Nie tworzy sztucznego epoch/sequence0. |
| Skonfigurowany brak/uszkodzenie/permissions/hash/schema/identity/oversize |503 UNKNOWN, zamknięty reason, brak last-good money, ścieżek i exception text. Błąd skonfigurowanego źródła ma pierwszeństwo przed hostEpoch409. |
| Proven conflict/backward/lineage replacement |503 `PUBLICATION_CONFLICT`, `PUBLICATION_BACKWARD` albo `PUBLICATION_LINEAGE_CHANGED`, latch do restartu. |

`sources[0]` opisuje `OFFLINE_PUBLICATION`: sourceClockUtc, najnowsze nie-null CaptureUtc/ReceiveUtc, ReconciledUtc, ostatni CommittedUtc, ageSeconds jako nullable decimal string, UNKNOWN quality/coverage, HISTORICAL albo UNKNOWN freshness, zawsze stale=true. Nieznane/przyszłe/regresywne czasy nie dają świeżości. observedUtc jest pierwszą akceptacją danej publikacji; poll/mtime/zmieniony export provenance/sourceClock przy tym samym committed payload nie resetuje go ani source metadata. Source age może rosnąć wraz z servedUtc. Broker zawsze NOT_CONFIGURED, accountId/balance/equity/P&L/statistics null, quality/coverage/runtimeReadiness UNKNOWN. DurableMode RUNNING i syntetyczny cash nie są realnym saldem ani gotowością.

### Passive capture, ingest i ograniczenia

Każdy autoryzowany GET czyta jeden bounded file handle read-only/share Read|Delete (32MiB/depth64), bez retry finansowej operacji, watcherów, sidecar DB, napraw i writer lease. Atomic rename daje stary albo nowy pełny capture. Reader ma własne prywatne DTO envelope/serializer, a nie zależność od Backtester ani reflection do jego API. Surowe pola są rekurencyjnie porównywane z pełną typed canonical shape **przed dopuszczeniem**, również getter-only UNKNOWN/null, brakujące/unknown/duplicate pola i literalne enumy. Digest to dokładne typed ordered pretty UTF8 content P, nie ad-hoc sort kluczy. Publiczne ReadPage waliduje accepted Core journal i trzy naprawione semantyczne przypadki P; host nie replikuje księgi ani financial validatora.

Jedna bramka service obejmuje capture/ingest/response. Identyczna publikacja jest idempotentna nawet po rebuild provenance. Większa rewizja wymaga monotonic source generation/sequence i identycznego canonical retained overlap. Ten sam revision z innym payload, zmieniony overlapping event/baseline, rollback lub nowy lineage nie udaje pustego success. Przejściowy zły capture może odzyskać poprawny nonconflicting source; dowiedziony conflict pozostaje latched. Restart zmienia tylko hostEpoch; nie zapisuje ekonomii ani zabezpieczenia anti-rollback między restartami. Zaufany lokalny writer/OS user/admin pozostaje granicą — checksum nie jest podpisem ani broker provenance.

### Dowody i bramki, których nie wolno utożsamiać

Real production-process tests uruchamiają `ApexDesktop.dll`, nie SyntheticHostEntry, i przechodzą przez prawdziwy digest loader. Obejmują CLI→HTTP, held real owner + odmowę drugiego ownera, authority bytes/file set, snapshot/page/restart/gap/corrupt/conflict i legacy EmergencyStop. Node VM empty-token test sprawdza jedynie konstrukcję requestu. Dokładne RED/GREEN/TRX/source hashes/build/HTTP są w raporcie H. **Prawdziwe przeglądarkowe Basic cache + pusty meta + stary panel/EmergencyStop są BLOCKED przez brak dostępnego lokalnego runtime — H nie jest odebrane.**

Nadal osobno: bounded independent H review/akceptacja rodzica; prywatny provisioning i Windows ACL/browser/native cTrader E2E; prywatny OKX/WS/history/fill/finality/settlement/statystyki; jakakolwiek zatwierdzona realna transakcja DEMO; mobile/VPS/TLS/gateway/device lifecycle; React/TS/Vite pięć podłączonych widoków, anulowanie fetch/account-generation/reducer fencing i R3F. Nie wykonano installu, remote CI, zmiany usług/konfiguracji użytkownika, brokerowego połączenia, commita ani push.
