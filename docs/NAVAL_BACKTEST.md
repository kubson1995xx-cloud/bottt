# Naval — lokalny backtest cash-SPOT D1

Ta ścieżka jest **badaniem offline**, nie adapterem zleceń. Nie używa HTTP, kluczy, brokerów, private API ani LIVE/DEMO. Nie zmienia Core, profilu CFD, guardów ani panelu. Syntetyczne testy nie dowodzą rentowności.

Wiążące reguły i okresy: [`reports/naval/TASK3_PREREJESTRACJA.md`](../reports/naval/TASK3_PREREJESTRACJA.md). Semantyka raportu: `naval-cash-spot-v1`; sygnały: istniejący `NavalD1Evaluator` / `naval-d1-v1`.

## Uruchomienie

```bash
dotnet run --project tools/Apex.Backtester -c Release -- naval \
  --manifest /sciezka/dataset.json --out /nowy/katalog-wynikow \
  --from 2022-01-01 --to 2026-10-29 \
  --fleet Both --cost base --mode fixed
```

- Każda opcja jest osobną parą klucz/wartość. Nieznane, powtórzone i brakujące opcje są błędem. `--manifest`, `--out`, `--from`, `--to`, `--fleet`, `--cost`, `--mode` są wymagane.
- `--fleet Alpha|Bravo|Both`, `--cost base|elevated|double`, `--mode fixed|walk-forward`.
- Fixed domyślnie 40/30/200. Dozwolone pary `--entry-days 32 --exit-days 24` lub `48/36` oraz jawne `40/30`; inne pary odmawiają.
- Daty UTC, przedział `[from,to)`. Potrzeba co najmniej 200 ciągłych pełnych dni warmup i pełnego pokrycia wszystkich członków wybranej floty oraz BTC. Warmup nie przenosi pozycji, rachunku ani decyzji oczekujących: pierwsza decyzja powstaje po close pierwszego dnia badanego okresu.
- `--out` musi nie istnieć. Istniejące wyniki nie są nadpisywane. Sukces: exit 0, `run.json` z `success:true`. Błąd: exit 2, `success:false` na stderr i w nowym własnym katalogu (jeśli został już zarezerwowany). Przy odmowie istniejącego katalogu nie zapisuje do niego nawet błędu.

Fixed tworzy **niezależny** rachunek floty oraz niezależny rachunek każdego jej symbolu. Alpha: 3 rachunki; Bravo: 11; Both: 14. Każdy zaczyna od 10000 jednostek wspólnego quote. Wyniki dwóch flot lub symboli nie są jednym reinwestowanym portfelem.

Walk-forward:

```bash
dotnet run --project tools/Apex.Backtester -c Release -- naval \
  --manifest /sciezka/dataset.json --out /nowy/katalog-wf \
  --from 2022-01-01 --to 2026-10-29 \
  --fleet Both --cost elevated --mode walk-forward
```

WF wymaga dokładnie powyższego pełnego okresu, bez override parametrów. Trzy zamrożone foldy: 2022–2023→2024, 2023–2024→2025, 2024–2025→2026 do 29 października exclusive. Każdy IS ma trzy świeże konta i koszty **base**, niezależnie od wybranego kosztu OOS. Kandydat potrzebuje ≥10 naturalnych zamknięć **ściśle przed** cutoff; boundary i zamknięcia na cutoff nie wchodzą do score średniego net-R. Remis: 40/30, potem ordinal key. Brak eligible = `NoSelection`, bez OOS/fallbacku. Brak OOS nie omija preflight danych.

WF wykonuje konta floty, a nie osobne izolowane WF każdego symbolu; każdy IS/OOS zawiera kompletne statystyki wkładu wszystkich członków floty, także zero trades. Maksymalnie 12 uruchomień/flotę/wywołanie (9 IS + 3 wybrane OOS). Nie sumować niezależnych foldów jako compounded portfolio/DD. `NavalRunBudget` jest współdzielony wewnątrz komendy i odmawia ponad 150 prób; wywołujący **musi policzyć łączny budżet całej kampanii między komendami**. Nie uruchamiać automatycznych dalszych gridów.

## Dane lokalne

Manifest `naval-market-data-v1`: `source`, `quoteAsset`, `cutoffUtc`, `priceBasis:TRADE`, `confirmedOnly:true`, `instruments[]`. Dostępny instrument wymaga `baseAsset,instrument,status,csvPath,sha256,startUtc,endUtc,rows,gaps,listTime,metadataPath,lotSize,minSize,tickSize`; niedostępny `status:unavailable` i niepustego `unavailableReason`. `endUtc` to opening timestamp **ostatniego** baru, nie exclusive end.

- Wspierane jawnie quote: USDT/USDC. Inne waluty wymagają świadomego rozszerzenia, nigdy FX=1. Nie dowodzi to dostępności par na rachunku użytkownika.
- CSV: dokładny header `timestamp,open,high,low,close,volume`, pełne opening timestamp UTC `Z`, invariant decimal bez wykładników/zaokrąglania przez parser. `volume` jest base volume. Brak interpolacji, napraw sortowania, duplikatów i luk. Każdy używany bar jest confirmed TRADE według jawnej deklaracji datasetu; to nie autoryzacja ani dowód prywatnego feedu.
- Hash i parse CSV dotyczą tego samego snapshotu bajtów; `CsvSnapshot` zachowuje oryginalne teksty decimal. Manifest i metadata też są hashowane. Zmiana pliku po Load nie zmienia załadowanych barów.
- Ścieżki są względne do manifestu, bez traversal/absolute/symlink (również w katalogach nadrzędnych).
- Metadata JSON wymaga `instrumentMetadata.instId,baseCcy,quoteCcy,lotSz,minSz,tickSz` zgodnych z manifestem. Bieżący `listTime` jest stringiem Unix milliseconds (albo jawnym ISO UTC Z), metadata lot/min/tick dodatnimi decimal strings.
- CURRENT metadata jest zamrożonym założeniem, **nie** historyczną specyfikacją. Loader odmawia okresu, którego warmup wypada po niedostatecznym pokryciu albo przed deklarowanym current listTime; nie odrzuca dawnych źródłowych barów tylko dlatego, że poprzedzają bieżący relisting.
- Niedostępny członek blokuje pełną flotę, nie zamienia jej w n=0/net=0. API `NavalRunSpec.Assets` pozwala jawnie badać dostępny podzbiór/pojedynczy symbol bez twierdzenia o pełnej flocie.

Downloader jest osobnym narzędziem. Backend nie pobiera datasetu. Nie uruchamiano tu kampanii na rzeczywistych cenach.

## Ekonomia i wykonanie

- `SpotAccount`: quote cash + base inventory, bez kredytu, short i pyramiding. Buy odejmuje notional i quote fee; base fee zmniejsza otrzymany inventory. Sell uznaje wyłącznie sprzedany notional minus quote fee; base fee zużywa dodatkowy inventory. Ujemna opłata/rebate i trzecia fee currency bez FX odmawiają atomowo. To jawna wewnętrzna konwencja dodatniej opłaty, nie parser podpisanych pól fee OKX.
- Partial generuje cashflow, ale nie finalny trade. Dopiero inventory=0 publikuje jeden wynik całej pozycji. Powtórny fill ID odmawia bez ponownego cash credit. `CostBasis` jest execution-price basis pozostałego inventory, bez drugiego debetu cash; pełna ekonomia jest w ledgerze.
- Net zamkniętej pozycji = suma jej quote cashflows. Fees = quote fee + base fee wyceniona po executionprice danego fill. Gross execution = net + fees. Gross reference = gross execution + jawny directional price impact faktycznie kupionych/sprzedanych ilości, przy niezmienionej wycenie fee; dla base-fee to audyt counterfactual, nie osobna historyczna cena. W badaniu fee jest zawsze quote.
- Buy execution wyżej, sell niżej o `(halfspread+slippage)/10000`. Price impact nie jest ponownie odejmowany od cash jako opłata. Audit zapisuje reference/executed i rozbicie spread/slippage.
- Base: fee .001, halfspread 2bps, slippage 5bps; elevated: .0015/3/7.5bps; double: .002/4/10bps. Są to **założenia**, nie rzeczywisty tier/opłaty konta.
- Wilder ATR14 liczone przyczynowo od początku źródłowej historii. SL=executed entry−2×ATR, TP=executed entry+3×odległość SL; obie referencje zaokrąglone w dół do tick. Drift `abs(open−signal close) > .5×ATR` odmawia. Lot w dół, minimum nigdy nie powiększa pozycji. Sizing po modeled open ogranicza koszt wejścia do cash oraz planowaną stratę SL z obiema quote fee i sell impact do .5% bieżącej liquidation equity. **Gap może przekroczyć planowane ryzyko**; nie ma gwarancji stop-price fill.
- Sygnały generuje wyłącznie istniejący evaluator. Prefix minimum 201 barów zachowuje as-of/no-lookahead; ATR używa pełnego prefiksu. Close signal realizuje się dopiero na kolejnym realnym open, raportowy timestamp open+1 tick respektuje `EarliestExecutionUtc` evaluatora; nie twierdzi, że istnieje tickowy feed.
- Ranking: distance/ATR malejąco, tie instrument ordinal. Liquidity: średni close×base volume z ostatnich 20 zakończonych dni ≥5000000 quote; nie używa volume/high/low dnia wejścia.
- Kolejność: protective gaps → channel exits → ranked entries → intrabar protection (także nowych wejść) → close marks/nowe sygnały. Gap SL po open, gap TP konserwatywnie po target, oba hits SL-first. Intrabar execution timestamp jest **czasem obserwacji close**, nie znaną chronologią. Boundary po ostatnim znanym close i z pełnym kosztem.
- Max 2 pozycje/flotę, wspólny cash. Risk: daily 1.5%, DD 5%; oba latch do końca runu. Open jest obserwowany po **całej fazie protective gaps**: cash z gap fills + pozostałe inventory po open, bez przejściowego peak ponad wykonywanym konserwatywnie TP. Channel exits i kolejne ranked entries zachowują checks po fill na spójnych markach open (także przed następnym admission). Na close **wszystkie** close-observed protective fills i close marks pozostałych pozycji są oceniane razem, bez checków/peak pomiędzy ordinal fills; boundary jest oceniane po całej likwidacji po tych samych close. Prawdziwy open-only breach pozostaje sticky mimo późniejszego odbicia. Halt blokuje nowe wejścia, nie wyjścia. D1 nie dowodzi tick enforcement.

## Raporty i definicje

`run.json` zawiera status, konfigurację, argumenty, hashes assembly Core/Backtester, manifestu/CSV/metadata, listę ukończonych kont, count prób i WF selection. Każde konto ma `account.json`, `trades.csv`, `cashflows.csv`, `equity.csv`, `refusals.csv`, `decisions.csv` (nagłówek także przy pustym zbiorze).

`account.json` zawiera wszystkie operacje, fee, boundary, net-R, n/gross reference/gross execution/net/fees/WR/PF/average win/loss, oba DD, pełną serię equity i per-symbol attribution, segmenty roczne oraz residual uzgodnienia. PF bez losses = null (nie 999), WR/średnie bez odpowiedniej próby = null. WR jest ułamkiem 0..1. Average loss jest podpisane ujemnie.

- Closed balance = start + net całkowicie zakończonych pozycji, obejmuje ich wszystkie partial/fee. To nie wolny quote cash podczas trzymania inventory.
- Liquidation equity = cash + inventory po modeled sell i quote fee; close-sampled DD jest odrębny od closed-balance DD. Latch ryzyka dodatkowo obserwuje dostępne open/fills.
- IntrabarLowStress: wspólny low wszystkich pozycji **przed ochroną intrabar**, po entry/gap/channel. Jest konserwatywnym niesynchronizowanym boundem, nie rzeczywistym DD; nie steruje haltem jak rzekomy tick feed.
- Roczny netLiquidationEquityChange to różnica realnych marków granicznych. Roczne operation fees/cashflows obejmują również wejścia pozycji zamykanej później. Close-observed intrabar/boundary przypisane są do kończącego się D1. Roczne trade metrics grupują całe zakończone pozycje i nie muszą równać się rocznej zmianie equity.
- Pełna likwidacja uzgadnia start+quote cashflows=end cash=sum start+trade net; residual toleruje jedynie decimal accumulation ≤1e-12 quote. Nie sumować kont/foldów jako wspólnego DD.

## Weryfikacja

```bash
dotnet test tests/Apex.Backtester.Tests -c Release --no-restore --filter FullyQualifiedName~Naval
# Końcowa bramka affected suite (raz po zakończeniu zmian):
dotnet test tests/Apex.Backtester.Tests -c Release --no-restore
```

Testy mają wyłącznie jawne syntetyczne fixtures, literalne cashflow, odmowy loadera, realny evaluator/runner, protection, causality, risk, raporty/CLI oraz traded WF z mutacją OOS. Wyniki badań rynkowych pozostają osobnym, nieuruchomionym etapem po review i weryfikacji danych.
