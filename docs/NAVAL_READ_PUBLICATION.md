# Naval — trwała publikacja OFFLINE (Task6A-P)

## Granica produktu

Publikacja jest wyłącznie **OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY**. To historyczny checkpoint wspólnego kapitału syntetycznego i ograniczony dziennik zaakceptowanych operacji. Nie jest brokerowym kontem, notowaniem live, księgą wyniku ani potwierdzeniem zlecenia. Nie nadaje uprawnień do START, wystawiania receiptów ani execution. Nie dodano tras HTTP, konfiguracji Desktop, auth ani UI.

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

## Nadal nieodebrane

Task6A-H/endpoints i pasywny reader bez writer lease; rozwiązanie operator-token bootstrap `/` i izolowanej roli read-only; React/TS/Vite pięć podłączonych widoków i anulowanie fetch/account-generation; R3F; prywatny OKX/WS/recovery/real broker fill/settlement/settled-result/statistics; realne DEMO/LIVE, Windows durability/ACL/cTrader E2E oraz VPS/mobile TLS/auth. Linux synthetic proof nie zastępuje tych bramek. Niezależny bounded review i decyzja rodzica pozostają osobnym krokiem; ta dokumentacja nie deklaruje acceptance.
