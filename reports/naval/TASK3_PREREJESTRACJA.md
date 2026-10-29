# Naval Task3 — prerejestracja danych, kosztów i badań

Zapis przed uruchomieniem obliczeń wyników. Baza d3625e8, istniejący evaluator naval-d1-v1 pozostaje bez zmian. Zlecenie kontynuacji obejmuje Task3 i kolejne etapy autonomicznie; bramki broker/DEMO trade/LIVE nadal obowiązują. Nie przeliczać ponownie ukończonego Task1/2 ani archiwalnego CFD research.

## Dane i zakres
- Oficjalne publiczne OKX, D1 UTC (`1Dutc`), wyłącznie confirm1 i pełne zakończone dni. Download od2020-01-01 do2026-10-29T00:00Z exclusive (ostatnie open2026-10-28); faktyczny listing/pokrycie mogą być krótsze. Repo nie zawiera surowych danych; `/home/ubuntu/projects/apex-data/naval-okx`, raw response + metadata/hash/URLs i normalized CSV z tym samym meaning.
- Dokładnie BTC ETH SOL XRP DOGE ADA LTC LINK BCH AVAX. Publiczne instrument discovery, wspólny quote wybrany jawnie przed wynikami: preferowane USDT jeśli publiczny katalog EEA go udostępnia, inaczej brak lub osobno udokumentowany wspólny quote. Research w jednostkach tego quote, NIE USD/PLN. Nie wnioskować o dostępności tradingu na polskim koncie. Bez FX=1 dla różnych walut, bez private requests/keys.
- Primary requested eval `[2022-01-01,2026-10-29)`; przynajmniej200 zakończonych dni warmup przed początkiem, bez state/balance/pozycji z warmup. Listing/leading coverage może zmienić eligible okres wyłącznie według danych PRZED obejrzeniem wyników i z zapisanym amendmentem; nigdy dobór pod zysk. Niezgodne/missing/gap → failed/unavailable, nie synthetic fill. Niedostępny członek floty blokuje twierdzenie o pełnym portfelu tej floty; per-symbol wyniki nadal jawne.
- Dane to ceny transakcyjne (TRADE), nie BID/MID/ASK. Nie rekonstruują historycznego spreadu/depth. Spread i slippage są jawnym modelem, nie opłatami zweryfikowanego konta. Bieżące tick/lot/min metadata mogą być użyte jako zamrożone założenia historyczne, nie odtworzona historia specyfikacji.

## Reguły i finansowanie
- ALFA BTC/ETH i BRAVO10, oryginalne40/30/BTC SMA200, long-only i bez pyramiding. Każda flota oddzielnie finansowany rachunek badawczy10000 jednostek quote, max2 aktywne pozycje; nie sumować ich wyników jako jednego compounded portfolio. Wspólny arbiter powstaje dopiero Task4.
- SL2×ATR14 (Wilder), TP3R jawnie zgodnie z zapisanymi metadanymi. Signal na closeD1, fill najwcześniej następnym realnym open. Referencyjny poziom nie jest executionprice. Entry drift ponad0,5×ATR od sygnału → pominięte. Recompute sizing po rzeczywistym modeled open. Nie short/borrow/leverage; cash/inventory nie może być ujemne.
- Ryzyko trade≤0,5% bieżącego equity (z fee/slippage reserve), limit daily1,5%, latch maxDD5%, max2/fleet. D1 ogranicza obserwację daily/DD: sprawdzanie na dostępnych markach i blokowanie kolejnych wejść, nie obietnica egzekucji tickowej. Drawdown halt trwa do końca konkretnego niezależnego runu; nie resetować tylko po to by uzyskać nowe transakcje.
- Kolejność dzienna: protective gaps istniejących pozycji, oczekujące wyjścia kanałowe z poprzedniego close, nowe wejścia z poprzedniego close po rankingu/risk, intrabar ochrony (także pozycji otwartych na open), mark i nowe decyzje close. Nie używać low/high/volume dnia egzekucji do decyzji podjętej na jego open.
- Stop gap: fill po gorszym open plus sell impact; TP gap: konserwatywnie poziomTP minus impact, nie dodatkowy przyszły zysk. Jeśli SL iTP dotknięte na jednej OHLC, SL-first. Boundary liquidation po ostatnim dostępnym close z kosztami, jawnie boundary, bez przyszłych cen.
- Ranking: breakout distance/ATR malejąco, tie symbol ordinal; płynność≥5000000 quote dziennie średniego close×volume z20 poprzednich zakończonych dni. To przybliżenie turnover z D1, nie historyczny orderbook. Brak takich danych odmawia, nie PASS.

## Koszty (ZAŁOŻENIA MODELOWANE, nie zweryfikowane fee tier)
- Base: taker0,001 (10bps) notional per fill, spread half2bps per side i dodatkowy slippage5bps per side.
- Elevated: fee0,0015, halfspread3bps, slippage7,5bps. Double: fee0,002, halfspread4bps, slippage10bps.
- Nie zakładać maker fills. Kierunkowe ceny wykonania niekorzystne dla buy/sell. Nie odejmować spread/slippage drugi raz po uwzględnieniu w fillprice. Kwanty/minimum walidowane decimal; sizing w dół z rezerwą fee, minimum odmawia bez zwiększania ryzyka.
- Konto i quote muszą być takie same. Fee może być quote/base; cash/inventory i costbasis mają rozliczać ją dokładnie raz, także po partial/final, z testami literalnych deltas. Inna waluta fee bez znanego modelu FX → odmowa, nie kurs1. Actual research base scenario fee w quote; alternatywny feeCurrency to test modelu, nie faktyczne dane konta.

## Warianty i walk-forward
- Główny wynik bez optymalizacji40/30/200. Sąsiedzi do stabilności:32/24/200,48/36/200; żadnego szukania setek parametrów po wynikach.
- Każdy wariant kosztów osobno; roczne segmenty i pełne krzywe/cashflow. Per-symbol oraz actual multi-symbol per-fleet accounts, z pełnym raportem także failures/zero trades. Brak wyniku = unavailable, nie n=0/net=0.
- Walk-forward3 logiczne foldy: train2022–2023→OOS2024; train2023–2024→OOS2025; train2024–2025→OOS2026-01-01..2026-10-29. Train tylko base costs, candidates trzy powyżej. Minimum10 naturalnych zakończonych transakcji kandydata, score średnieR naturalnych wyników, tie baseline40/30 potem deterministic key. Wyniki zamknięte na/po train cutoff oraz wymuszone boundary wykluczone z selekcji; pełna ekonomia zachowana w rachunku.
- Brak eligible kandydata = null/NoSelection, bez fałszywego fallbackwinner. Odrębnie można pokazać stałe40/30 w tych samych OOS latach, ale to nie selectedWF.
- Świeży rachunek/run na każdy IS/OOS/candidate/cost, warmup tylko wskaźniki. OOS nie może wpływać na selection; test przyszłej mutacji musi faktycznie zawierać transakcje. Wszystkie okresy to badania historyczne; nie deklarować certified untouched holdout.
- Bounded≤150 independent runs na pierwszą kampanię, bez automatycznych kolejnych gridów. Raportować przekroczenie budżetu i nieukończone accounts. Nie promować strategii na Validated ani nie aktywować live/DEMO na podstawie wyniku.

## Wymagane raporty
Dla każdego konta/fleet/symbol/range/variant/cost: n, gross execution/reference (definicje), net, fee, modeled spread/slippage, WR/PF/null gdy nieokreślone, average win/loss, closed-balance oraz close-sampled liquidation equity DD osobno, equity series, all trades z boundary, per-operation cashflows i powody odmowy. Intrabar stress z równoczesnych low tylko jako konserwatywny bound, nie rzeczywisty zsynchronizowany DD. Uzgodnienie start+cashflows+inventory mark=end; końcowy full liquidation net i fees bez podwójnego księgowania. Suma niezależnych rachunków nie udaje wspólnego portfolio/DD.
