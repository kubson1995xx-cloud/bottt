# WYNIKI TASK3 — rzeczywiste badanie Naval / OKX, odzyskane po timeout



## Status i wniosek

Zakres badawczy zamrożonej kampanii jest pokryty: pięć fixed, WF base/elevated/double z rzeczywistymi eksportami, jawnym reuse IS i zachowanym NoSelection. **Nie jest to osiem udanych oryginalnych komend CLI.** Sześć CLI zakończyło się sukcesem, siódma została zabita timeoutem 420 s, ósmej nie uruchamiano. Nie utworzono zastępczego `run.json` w przerwanym katalogu.

**Główne fixed 40/30 są dodatnie w obu flotach przy wszystkich trzech kosztach, ale selected WF BRAVO traci w 2024 i 2025 we wszystkich kosztach. ALFA nie ma selekcji na 2024; dodatnie OOS 2025/2026 mają tylko 7 i 4 transakcje. To ograniczony materiał historyczny, nie wystarczający dowód trwałej przewagi ani podstawa promocji na Validated.**

Wartości pieniężne: **USDT**, nie USD/PLN. Rachunek startuje z 10000 USDT. ALFA (`Alpha`) obejmuje BTC/ETH; BRAVO (`Bravo`) obejmuje BTC, ETH, SOL, XRP, DOGE, ADA, LTC, LINK, BCH, AVAX. Każda flota, isolated symbol i fold to osobno finansowane konto; nie łączymy ich kapitału, P&L ani DD w portfolio.



## Tożsamość, dane i koszty

- Zaakceptowany backend: `43d5bbfd472c340ac0ad5dbd77ea7468ee6122ca`; prerejestracja `6050d20`, `reports/naval/TASK3_PREREJESTRACJA.md`. Parametry i sticky risk limity bez zmian; brak zleceń, kluczy, leverage, private API i nowych pobrań.

- Manifest `/home/ubuntu/projects/apex-data/naval-okx/dataset.json`: `692ea89d11281307d416068e5e78302d1f791cf60a17c65451ac3ba0a74b3703`.

- Backtester DLL: `ebd3c198c88098442b2c44b6852e246b67f058defeb419934a04bd0f6fb20c37`; Core DLL: `7c18fef33d7d98b85f98711df8420f001dc10112e967435ac619dc5f20e73f8b`; evaluator `naval-d1-v1`; accounting `naval-cash-spot-v1`.

- Przed/po recovery identyczne **489 pliki** danych/raw/provenance/metadata, tracked C#/projektów i DLL oraz planu/stanu oryginalnego. `before-hashes.json` i `after-hashes.json` mają zgodną zawartość i hash `9673e6ee439904cd039d7c8eee7811c0fa5c784f5b7e1e03cf746605cb6b7e7b`. Wszystkie 18 zaakceptowanych wejść `final-inputs.json` zgodne. Nie przebudowano produktu i nie powtarzano suite.

- Źródło: zapisane oficjalne publiczne OKX EEA, TRADE D1 UTC (`1Dutc`), confirm=1; 10 instrumentów, 24389 normalizowanych barów. Każdy ma 1762 dni primary `[2022-01-01,2026-10-29)` i ≥200 dni warmup. Ostatni bar open 2026-10-28, close 2026-10-29; daty `to` są exclusive.

- Raw payloads, URL/fetch/status/hash i metadane są poza Git w dataset `raw/`, `provenance/`, `metadata/`. Weryfikację pobrania/normalizacji zachowano w `verification.json` i `parent-coverage-verification.json`; recovery nie pobierał ani nie zastępował cen. Zgodny hash i parse nie dowodzą historycznej wykonalności ani dostępności na polskim rachunku.

- CURRENT `listTime/lot/min/tick` to zamrożone założenie historyczne, nie historia specyfikacji. Przykładowe current listTime 2021-01-29 nie usuwa wcześniejszych autentycznych barów; przed primary/warmup jest spełniony. Katalog publiczny nie jest autoryzacją rachunku. TRADE nie jest BID/ASK, brak historycznego orderbook/depth i tickowej chronologii.

- Koszt base: fee 0.001/fill, halfspread 2 bps, slippage 5 bps. Elevated: 0.0015/3/7.5. Double: 0.002/4/10. Są to modele, nie zweryfikowany tier OKX. Quote fee odjęta raz; spread/slippage pogarsza execution price, nie jest drugim debetem.

- Reguły: long-only cash SPOT, 40/30/BTC SMA200, sąsiedzi 32/24 i 48/36; SL 2×ATR14 Wilder, TP 3R, max2 pozycje, trade risk 0.5%, daily halt 1.5%, DD halt 5%, latch do końca runu. No borrow/short/pyramiding. Warmup nie przenosi konta ani pozycji.



### Pokrycie wszystkich instrumentów

| Symbol | D1 bars | Pierwsze open UTC | Ostatnie open | Primary / warmup | Status |
|---|---|---|---|---|---|
| BTC | 2493 | 2020-01-01 | 2026-10-28 | 1762 / ≥200 | available |
| ETH | 2493 | 2020-01-01 | 2026-10-28 | 1762 / ≥200 | available |
| SOL | 2219 | 2020-10-01 | 2026-10-28 | 1762 / ≥200 | available |
| XRP | 2493 | 2020-01-01 | 2026-10-28 | 1762 / ≥200 | available |
| DOGE | 2493 | 2020-01-01 | 2026-10-28 | 1762 / ≥200 | available |
| ADA | 2493 | 2020-01-01 | 2026-10-28 | 1762 / ≥200 | available |
| LTC | 2492 | 2020-01-02 | 2026-10-28 | 1762 / ≥200 | available |
| LINK | 2493 | 2020-01-01 | 2026-10-28 | 1762 / ≥200 | available |
| BCH | 2493 | 2020-01-01 | 2026-10-28 | 1762 / ≥200 | available |
| AVAX | 2227 | 2020-09-23 | 2026-10-28 | 1762 / ≥200 | available |


Brak instrumentów unavailable i brak odmowy danych dla wymaganych kont. Wynik zero trades jest pokazany jako zero; NoSelection nie jest zerowym kontem OOS.



## Fixed — actual shared-cash fleet accounts

Pełny okres `[2022-01-01,2026-10-29)`. `Gref` = gross reference, `Gexe` = gross execution; Gexe = net + fee, Gref = Gexe + directional price impact. WR w %. PF=null, jeśli brak strat; nie zastępujemy null liczbą 999. Średnia strata jest ujemna.

| Wariant | Flota | n | Gref | Gexe | Net | Fee | WR% | PF | Avg win | Avg loss |
|---|---|---|---|---|---|---|---|---|---|---|
| base-40-30 | Alpha | 21 | 388.30 | 367.15 | 336.94 | 30.21 | 33.33 | 1.51 | 143.38 | -47.62 |
| base-40-30 | Bravo | 31 | 520.61 | 497.71 | 464.99 | 32.71 | 32.26 | 1.45 | 149.68 | -49.13 |
| elevated-40-30 | Alpha | 21 | 383.00 | 351.90 | 307.47 | 44.43 | 33.33 | 1.46 | 139.20 | -47.64 |
| elevated-40-30 | Bravo | 31 | 518.67 | 484.85 | 436.52 | 48.32 | 32.26 | 1.42 | 146.81 | -49.12 |
| double-40-30 | Alpha | 21 | 377.99 | 337.32 | 279.22 | 58.10 | 33.33 | 1.42 | 135.20 | -47.65 |
| double-40-30 | Bravo | 32 | 468.05 | 422.23 | 356.79 | 65.45 | 31.25 | 1.33 | 144.01 | -49.24 |
| base-32-24 | Alpha | 20 | 269.35 | 249.32 | 220.70 | 28.62 | 30.00 | 1.35 | 143.30 | -45.65 |
| base-32-24 | Bravo | 34 | 898.88 | 873.35 | 836.90 | 36.46 | 38.24 | 1.85 | 140.37 | -47.05 |
| base-48-36 | Alpha | 36 | 1142.01 | 1104.39 | 1050.65 | 53.74 | 41.67 | 2.01 | 139.56 | -49.65 |
| base-48-36 | Bravo | 31 | 472.28 | 449.14 | 416.08 | 33.06 | 32.26 | 1.38 | 149.72 | -51.48 |


| Wariant | Flota | Spread | Slippage | Close equity DD% | Closed balance DD% | Halt | RiskHalt refusals | Boundary n |
|---|---|---|---|---|---|---|---|---|
| base-40-30 | Alpha | 6.04 | 15.11 | 5.23 | 2.97 | TAK | 55 | 0 |
| base-40-30 | Bravo | 6.54 | 16.36 | 5.03 | 3.94 | TAK | 231 | 0 |
| elevated-40-30 | Alpha | 8.89 | 22.22 | 5.18 | 2.97 | TAK | 55 | 0 |
| elevated-40-30 | Bravo | 9.66 | 24.16 | 5.00 | 3.94 | TAK | 231 | 0 |
| double-40-30 | Alpha | 11.62 | 29.05 | 5.13 | 2.97 | TAK | 55 | 0 |
| double-40-30 | Bravo | 13.09 | 32.72 | 5.46 | 4.42 | TAK | 229 | 0 |
| base-32-24 | Alpha | 5.72 | 14.31 | 5.16 | 2.97 | TAK | 62 | 0 |
| base-32-24 | Bravo | 7.29 | 18.23 | 5.08 | 3.58 | TAK | 214 | 0 |
| base-48-36 | Alpha | 10.75 | 26.87 | 4.75 | 2.48 | NIE | 0 | 1 |
| base-48-36 | Bravo | 6.61 | 16.53 | 5.50 | 4.42 | TAK | 203 | 0 |


Sąsiedzi są testem wrażliwości, nie wyborem po wyniku. ALFA 48/36 ma inny wynik i brak haltu; BRAVO 32/24 różni się od 40/30. Koszt zmienia sizing, ochrony i czas haltu, dlatego porównanie nie musi mieć tej samej liczby transakcji (BRAVO double n=32 vs base n=31). Nie wybrano nowych parametrów na podstawie tych wyników.



### Roczne segmenty rachunków floty (bez resetu kapitału lub haltu)

`ΔEq` jest różnicą rzeczywistych granicznych liquidation marks w tym samym fixed koncie. `Trade net`/n grupują całe zamknięte pozycje po dniu obserwacji wyjścia; mogą różnić się od ΔEq. `Fee ops` obejmuje wejścia/wyjścia w roku, także pozycji zamkniętych w kolejnym roku. Rok 2026 kończy się 29 października exclusive. Zera po halcie są wynikiem utrzymania latchu, nie brakującymi danymi.

| Wariant | Flota | Rok | ΔEq USDT | Trade n | Trade net | Fee ops |
|---|---|---|---|---|---|---|
| base-40-30 | Alpha | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| base-40-30 | Alpha | 2023 | 381.59 | 9 | 368.45 | 15.09 |
| base-40-30 | Alpha | 2024 | -44.65 | 12 | -31.52 | 15.13 |
| base-40-30 | Alpha | 2025 | 0.00 | 0 | 0.00 | 0.00 |
| base-40-30 | Alpha | 2026 | 0.00 | 0 | 0.00 | 0.00 |
| base-40-30 | Bravo | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| base-40-30 | Bravo | 2023 | 813.27 | 17 | 799.61 | 18.78 |
| base-40-30 | Bravo | 2024 | -348.28 | 14 | -334.62 | 13.93 |
| base-40-30 | Bravo | 2025 | 0.00 | 0 | 0.00 | 0.00 |
| base-40-30 | Bravo | 2026 | 0.00 | 0 | 0.00 | 0.00 |
| elevated-40-30 | Alpha | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| elevated-40-30 | Alpha | 2023 | 360.59 | 9 | 350.33 | 22.18 |
| elevated-40-30 | Alpha | 2024 | -53.12 | 12 | -42.85 | 22.25 |
| elevated-40-30 | Alpha | 2025 | 0.00 | 0 | 0.00 | 0.00 |
| elevated-40-30 | Alpha | 2026 | 0.00 | 0 | 0.00 | 0.00 |
| elevated-40-30 | Bravo | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| elevated-40-30 | Bravo | 2023 | 789.50 | 17 | 777.06 | 27.75 |
| elevated-40-30 | Bravo | 2024 | -352.98 | 14 | -340.53 | 20.57 |
| elevated-40-30 | Bravo | 2025 | 0.00 | 0 | 0.00 | 0.00 |
| elevated-40-30 | Bravo | 2026 | 0.00 | 0 | 0.00 | 0.00 |
| double-40-30 | Alpha | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| double-40-30 | Alpha | 2023 | 340.47 | 9 | 332.95 | 28.99 |
| double-40-30 | Alpha | 2024 | -61.25 | 12 | -53.73 | 29.11 |
| double-40-30 | Alpha | 2025 | 0.00 | 0 | 0.00 | 0.00 |
| double-40-30 | Alpha | 2026 | 0.00 | 0 | 0.00 | 0.00 |
| double-40-30 | Bravo | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| double-40-30 | Bravo | 2023 | 766.32 | 17 | 755.05 | 36.47 |
| double-40-30 | Bravo | 2024 | -409.53 | 15 | -398.26 | 28.98 |
| double-40-30 | Bravo | 2025 | 0.00 | 0 | 0.00 | 0.00 |
| double-40-30 | Bravo | 2026 | 0.00 | 0 | 0.00 | 0.00 |
| base-32-24 | Alpha | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| base-32-24 | Alpha | 2023 | 411.34 | 9 | 398.17 | 15.14 |
| base-32-24 | Alpha | 2024 | -190.64 | 11 | -177.46 | 13.48 |
| base-32-24 | Alpha | 2025 | 0.00 | 0 | 0.00 | 0.00 |
| base-32-24 | Alpha | 2026 | 0.00 | 0 | 0.00 | 0.00 |
| base-32-24 | Bravo | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| base-32-24 | Bravo | 2023 | 844.26 | 17 | 830.56 | 18.85 |
| base-32-24 | Bravo | 2024 | -7.36 | 17 | 6.33 | 17.61 |
| base-32-24 | Bravo | 2025 | 0.00 | 0 | 0.00 | 0.00 |
| base-32-24 | Bravo | 2026 | 0.00 | 0 | 0.00 | 0.00 |
| base-48-36 | Alpha | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| base-48-36 | Alpha | 2023 | 351.27 | 9 | 338.18 | 15.04 |
| base-48-36 | Alpha | 2024 | 144.66 | 16 | 157.75 | 20.45 |
| base-48-36 | Alpha | 2025 | 274.26 | 7 | 274.26 | 10.79 |
| base-48-36 | Alpha | 2026 | 280.45 | 4 | 280.45 | 7.47 |
| base-48-36 | Bravo | 2022 | 0.00 | 0 | 0.00 | 0.00 |
| base-48-36 | Bravo | 2023 | 816.83 | 16 | 803.17 | 17.92 |
| base-48-36 | Bravo | 2024 | -400.75 | 15 | -387.09 | 15.14 |
| base-48-36 | Bravo | 2025 | 0.00 | 0 | 0.00 | 0.00 |
| base-48-36 | Bravo | 2026 | 0.00 | 0 | 0.00 | 0.00 |




### Izolowane konta symboli (nie wkład do floty)

Każdy z poniższych wierszy to osobne konto 10000 USDT, a nie transakcje wydzielone z rachunku floty. W komórce: **n / net / fee / PF / closeEqDD% / halt**. `0`/`null` zachowane. Pełne Gref/Gexe/WR/średnie/DD obu typów są w indeksie `account-metrics.csv` i wskazanym `account.json`.

#### Stałe 40/30 — koszty

| Flota/symbol | base-40-30 | elevated-40-30 | double-40-30 |
|---|---|---|---|
| Alpha/BTC | 19 / 520.51 / 30.64 / 2.01 / 2.70 / NIE | 19 / 487.38 / 44.95 / 1.94 / 2.68 / NIE | 19 / 455.69 / 58.66 / 1.88 / 2.66 / NIE |
| Alpha/ETH | 18 / 538.25 / 23.20 / 2.07 / 2.71 / NIE | 18 / 512.35 / 34.21 / 2.02 / 2.68 / NIE | 18 / 487.34 / 44.85 / 1.97 / 2.65 / NIE |
| Bravo/BTC | 19 / 520.51 / 30.64 / 2.01 / 2.70 / NIE | 19 / 487.38 / 44.95 / 1.94 / 2.68 / NIE | 19 / 455.69 / 58.66 / 1.88 / 2.66 / NIE |
| Bravo/ETH | 18 / 538.25 / 23.20 / 2.07 / 2.71 / NIE | 18 / 512.35 / 34.21 / 2.02 / 2.68 / NIE | 18 / 487.34 / 44.85 / 1.97 / 2.65 / NIE |
| Bravo/SOL | 21 / 339.48 / 19.23 / 1.48 / 3.73 / NIE | 21 / 322.46 / 28.50 / 1.46 / 3.76 / NIE | 21 / 305.76 / 37.54 / 1.43 / 3.79 / NIE |
| Bravo/XRP | 22 / 256.84 / 21.32 / 1.35 / 4.11 / NIE | 22 / 238.91 / 31.56 / 1.32 / 4.13 / NIE | 22 / 221.37 / 41.54 / 1.30 / 4.15 / NIE |
| Bravo/DOGE | 20 / 159.34 / 17.49 / 1.23 / 2.64 / NIE | 20 / 143.19 / 25.91 / 1.20 / 2.63 / NIE | 20 / 127.50 / 34.13 / 1.18 / 2.62 / NIE |
| Bravo/ADA | 5 / -21.57 / 4.91 / 0.87 / 1.98 / TAK | 5 / -24.80 / 7.28 / 0.85 / 1.96 / TAK | 5 / -27.92 / 9.58 / 0.83 / 1.94 / TAK |
| Bravo/LTC | 21 / -276.80 / 20.96 / 0.67 / 4.02 / NIE | 21 / -287.85 / 31.01 / 0.66 / 4.05 / NIE | 21 / -298.70 / 40.79 / 0.64 / 4.07 / NIE |
| Bravo/LINK | 19 / 63.18 / 16.39 / 1.10 / 2.44 / NIE | 19 / 51.54 / 24.31 / 1.08 / 2.43 / NIE | 19 / 40.06 / 32.05 / 1.06 / 2.42 / NIE |
| Bravo/BCH | 13 / -50.04 / 10.77 / 0.90 / 2.92 / NIE | 13 / -56.85 / 15.98 / 0.88 / 2.91 / NIE | 13 / -63.52 / 21.08 / 0.87 / 2.91 / NIE |
| Bravo/AVAX | 16 / 173.61 / 13.39 / 1.31 / 3.29 / NIE | 16 / 163.28 / 19.87 / 1.29 / 3.28 / NIE | 16 / 153.17 / 26.22 / 1.27 / 3.27 / NIE |


#### Sąsiedzi — base

| Flota/symbol | base-32-24 | base-48-36 |
|---|---|---|
| Alpha/BTC | 20 / 467.91 / 31.84 / 1.83 / 3.19 / NIE | 18 / 542.66 / 29.43 / 2.09 / 2.21 / NIE |
| Alpha/ETH | 18 / 568.35 / 23.29 / 2.20 / 2.71 / NIE | 18 / 493.66 / 23.16 / 1.94 / 2.71 / NIE |
| Bravo/BTC | 20 / 467.91 / 31.84 / 1.83 / 3.19 / NIE | 18 / 542.66 / 29.43 / 2.09 / 2.21 / NIE |
| Bravo/ETH | 18 / 568.35 / 23.29 / 2.20 / 2.71 / NIE | 18 / 493.66 / 23.16 / 1.94 / 2.71 / NIE |
| Bravo/SOL | 21 / 432.65 / 19.33 / 1.68 / 3.05 / NIE | 20 / 362.70 / 18.85 / 1.53 / 3.52 / NIE |
| Bravo/XRP | 23 / 404.83 / 22.69 / 1.54 / 4.11 / NIE | 20 / 360.99 / 18.79 / 1.56 / 3.26 / NIE |
| Bravo/DOGE | 23 / 38.81 / 20.21 / 1.05 / 3.32 / NIE | 20 / 159.34 / 17.46 / 1.23 / 2.64 / NIE |
| Bravo/ADA | 17 / 170.82 / 14.55 / 1.30 / 3.45 / NIE | 14 / 112.28 / 12.17 / 1.24 / 2.96 / NIE |
| Bravo/LTC | 22 / -325.41 / 22.00 / 0.63 / 4.50 / NIE | 17 / -267.45 / 16.64 / 0.61 / 4.89 / NIE |
| Bravo/LINK | 19 / 79.99 / 16.34 / 1.12 / 2.44 / NIE | 19 / 63.18 / 16.38 / 1.10 / 2.44 / NIE |
| Bravo/BCH | 14 / -99.79 / 11.77 / 0.81 / 3.41 / NIE | 12 / -18.04 / 9.99 / 0.96 / 2.61 / NIE |
| Bravo/AVAX | 18 / 269.56 / 15.06 / 1.44 / 3.08 / NIE | 15 / 224.91 / 12.65 / 1.44 / 3.29 / NIE |




### Per-symbol attribution wewnątrz rzeczywistych flot

To **wkład w współdzielone cash/limit2**, nie niezależne konto. W komórce **n / net / fee**. Np. ADA/AVAX/LTC mogą mieć zero, mimo transakcji na ich izolowanych kontach. Pełne metryki wszystkich symboli/lat/IS/OOS, również zera, zachowuje `symbol-attribution.csv` i oryginalne JSON.

| Flota/symbol | base-40-30 | elevated-40-30 | double-40-30 | base-32-24 | base-48-36 |
|---|---|---|---|---|---|
| Alpha/BTC | 11 / 258.68 / 16.67 | 11 / 240.87 / 24.49 | 11 / 223.82 / 32.00 | 11 / 61.95 / 16.35 | 18 / 547.70 / 30.02 |
| Alpha/ETH | 10 / 78.26 / 13.54 | 10 / 66.60 / 19.94 | 10 / 55.39 / 26.10 | 9 / 158.75 / 12.26 | 18 / 502.95 / 23.72 |
| Bravo/BTC | 7 / 75.60 / 10.72 | 7 / 64.99 / 15.74 | 7 / 54.87 / 20.57 | 8 / 225.59 / 12.43 | 7 / 46.31 / 10.69 |
| Bravo/ETH | 3 / -148.77 / 3.08 | 3 / -148.78 / 4.55 | 3 / -148.78 / 5.98 | 2 / -66.31 / 2.06 | 3 / -148.81 / 3.10 |
| Bravo/SOL | 9 / 540.96 / 7.57 | 9 / 530.52 / 11.23 | 9 / 520.23 / 14.81 | 9 / 616.78 / 7.60 | 8 / 594.38 / 6.84 |
| Bravo/XRP | 2 / -104.80 / 2.10 | 2 / -104.57 / 3.10 | 2 / -104.35 / 4.07 | 2 / -105.84 / 2.10 | 2 / -105.10 / 2.10 |
| Bravo/DOGE | 4 / -1.53 / 3.84 | 4 / -4.22 / 5.68 | 5 / -58.88 / 9.45 | 6 / 98.52 / 5.99 | 4 / -3.07 / 4.05 |
| Bravo/ADA | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/LTC | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/LINK | 3 / 40.77 / 2.85 | 3 / 38.25 / 4.22 | 3 / 35.75 / 5.56 | 3 / 58.25 / 2.82 | 3 / 40.62 / 2.86 |
| Bravo/BCH | 3 / 62.76 / 2.56 | 3 / 60.33 / 3.80 | 3 / 57.96 / 5.01 | 3 / 63.09 / 2.57 | 3 / 44.35 / 2.54 |
| Bravo/AVAX | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 1 / -53.19 / 0.90 | 1 / -52.60 / 0.89 |




## Walk-forward — selekcja i actual OOS

Trzy kandydatury na każdy IS, wszystkie z **base cost niezależnie od kosztu OOS**. Score = średnie net-R tylko naturalnych trade z entry≥IS from i exit **ściśle przed** IS cutoff; boundary nie jest selekcją, ale pozostaje w pełnym account P&L/fee/equity. Minimum 10 naturalnych zakończeń każdego kandydata. Tie: 40/30, potem ordinal. C# recovery odtwarza wyłącznie rzeczywiste eksportowane fills w SpotAccount, uzgadnia dokładne SpotTrade i wywołuje istniejące `NavalWalkForward.Select`; to nie nowy backtest. Niezależny Python ponownie liczy sample, score i ranking. Selekcja base/elevated identyczna we wszystkich 6 foldach; double jawnie reuse base selection.

| Flota/fold | IS [from,to) | OOS [from,to) | 40/30 n:scoreR | 32/24 n:scoreR | 48/36 n:scoreR | Selection |
|---|---|---|---|---|---|---|
| Alpha/1 | 2022-01-01..2024-01-01 | 2024-01-01..2025-01-01 | 9:null | 9:null | 9:null | NoSelection |
| Alpha/2 | 2023-01-01..2025-01-01 | 2025-01-01..2026-01-01 | 21:0.329460 | 20:0.232293 | 25:0.402367 | 48-36-200 |
| Alpha/3 | 2024-01-01..2026-01-01 | 2026-01-01..2026-10-29 | 12:-0.039872 | 11:-0.301841 | 23:0.368960 | 48-36-200 |
| Bravo/1 | 2022-01-01..2024-01-01 | 2024-01-01..2025-01-01 | 17:0.919055 | 17:0.952525 | 16:0.980326 | 48-36-200 |
| Bravo/2 | 2023-01-01..2025-01-01 | 2025-01-01..2026-01-01 | 31:0.301844 | 34:0.482045 | 31:0.271563 | 32-24-200 |
| Bravo/3 | 2024-01-01..2026-01-01 | 2026-01-01..2026-10-29 | 13:-0.405136 | 16:0.074788 | 14:-0.447626 | 32-24-200 |


**ALFA fold1: n=9 dla każdego kandydata → NoSelection → brak OOS 2024 dla base/elevated/double. Nie ma fallbacku ani udawanego wyniku n=0/net=0.** Preflight pełnych flot i OOS wykonany także dla tego folda.



### OOS — osobne świeże konta, bez sumowanego portfolio

| Flota | Fold | Koszt | Wybrane | n | Gref | Gexe | Net | Fee | WR% | PF | EqDD% | BalDD% | Halt |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Alpha | 2 | base | 48-36-200 | 7 | 278.77 | 271.58 | 261.30 | 10.28 | 42.86 | 2.49 | 2.67 | 1.68 | NIE |
| Alpha | 2 | elevated | 48-36-200 | 7 | 277.52 | 266.95 | 251.84 | 15.11 | 42.86 | 2.43 | 2.62 | 1.69 | NIE |
| Alpha | 2 | double | 48-36-200 | 7 | 276.23 | 262.40 | 242.64 | 19.76 | 42.86 | 2.38 | 2.58 | 1.69 | NIE |
| Alpha | 3 | base | 48-36-200 | 4 | 272.19 | 267.33 | 260.40 | 6.94 | 75.00 | 6.02 | 1.21 | 0.50 | NIE |
| Alpha | 3 | elevated | 48-36-200 | 4 | 266.57 | 259.45 | 249.28 | 10.17 | 75.00 | 5.81 | 1.20 | 0.50 | NIE |
| Alpha | 3 | double | 48-36-200 | 4 | 261.22 | 251.93 | 238.66 | 13.27 | 75.00 | 5.61 | 1.18 | 0.50 | NIE |
| Bravo | 1 | base | 48-36-200 | 14 | -288.29 | -297.79 | -311.37 | 13.57 | 14.29 | 0.48 | 5.50 | 4.42 | TAK |
| Bravo | 1 | elevated | 48-36-200 | 14 | -283.34 | -297.39 | -317.48 | 20.09 | 14.29 | 0.47 | 5.48 | 4.42 | TAK |
| Bravo | 1 | double | 48-36-200 | 14 | -278.47 | -296.97 | -323.39 | 26.43 | 14.29 | 0.46 | 5.46 | 4.42 | TAK |
| Bravo | 2 | base | 32-24-200 | 14 | 23.87 | 13.08 | -2.34 | 15.41 | 28.57 | 0.99 | 3.12 | 2.61 | NIE |
| Bravo | 2 | elevated | 32-24-200 | 14 | 25.05 | 9.13 | -13.60 | 22.73 | 28.57 | 0.97 | 3.08 | 2.61 | NIE |
| Bravo | 2 | double | 32-24-200 | 14 | 26.20 | 5.33 | -24.49 | 29.82 | 28.57 | 0.95 | 3.05 | 2.62 | NIE |
| Bravo | 3 | base | 32-24-200 | 6 | 93.35 | 87.81 | 79.91 | 7.90 | 33.33 | 1.39 | 1.40 | 1.01 | NIE |
| Bravo | 3 | elevated | 32-24-200 | 6 | 92.22 | 84.07 | 72.43 | 11.65 | 33.33 | 1.36 | 1.41 | 1.01 | NIE |
| Bravo | 3 | double | 32-24-200 | 6 | 91.16 | 80.48 | 65.21 | 15.26 | 33.33 | 1.32 | 1.42 | 1.01 | NIE |


| Flota | Fold | Koszt | Spread | Slippage | Avg win | Avg loss | Boundary n |
|---|---|---|---|---|---|---|---|
| Alpha | 2 | base | 2.06 | 5.14 | 145.60 | -43.87 | 0 |
| Alpha | 2 | elevated | 3.02 | 7.55 | 142.60 | -43.99 | 0 |
| Alpha | 2 | double | 3.95 | 9.88 | 139.69 | -44.11 | 0 |
| Alpha | 3 | base | 1.39 | 3.47 | 104.09 | -51.86 | 1 |
| Alpha | 3 | elevated | 2.03 | 5.09 | 100.36 | -51.80 | 1 |
| Alpha | 3 | double | 2.65 | 6.64 | 96.80 | -51.74 | 1 |
| Bravo | 1 | base | 2.71 | 6.79 | 142.84 | -49.75 | 0 |
| Bravo | 1 | elevated | 4.02 | 10.04 | 139.64 | -49.73 | 0 |
| Bravo | 1 | double | 5.28 | 13.21 | 136.55 | -49.71 | 0 |
| Bravo | 2 | base | 3.08 | 7.71 | 112.47 | -45.22 | 0 |
| Bravo | 2 | elevated | 4.55 | 11.37 | 109.78 | -45.27 | 0 |
| Bravo | 2 | double | 5.96 | 14.91 | 107.17 | -45.32 | 0 |
| Bravo | 3 | base | 1.58 | 3.95 | 141.86 | -50.95 | 0 |
| Bravo | 3 | elevated | 2.33 | 5.82 | 138.04 | -50.91 | 0 |
| Bravo | 3 | double | 3.05 | 7.63 | 134.36 | -50.88 | 0 |


Nie sumujemy niezależnych foldów jako reinwestowanego kapitału ani wspólnego DD. Wysokie PF ALFA 2026 wynika tylko z 4 pozycji, nie silnej próby. BRAVO 2024 haltuje przy każdym koszcie; BRAVO 2025 ma PF<1 i ujemny net nawet base. Warmup, cash, pending signals, inventory i risk state są świeże w każdym actual runie.



### Wkład wszystkich symboli w OOS (n / net / fee)

| Flota/fold/symbol | Base | Elevated | Double |
|---|---|---|---|
| Alpha/2/BTC | 3 / 39.90 / 5.64 | 3 / 36.61 / 8.26 | 3 / 33.43 / 10.75 |
| Alpha/2/ETH | 4 / 221.40 / 4.63 | 4 / 215.22 / 6.85 | 4 / 209.21 / 9.01 |
| Alpha/3/BTC | 2 / 170.22 / 4.05 | 2 / 162.79 / 5.92 | 2 / 155.73 / 7.70 |
| Alpha/3/ETH | 2 / 90.18 / 2.89 | 2 / 86.48 / 4.25 | 2 / 82.92 / 5.57 |
| Bravo/1/BTC | 3 / 40.53 / 4.23 | 3 / 36.60 / 6.24 | 3 / 32.83 / 8.17 |
| Bravo/1/ETH | 2 / -99.65 / 1.96 | 2 / -99.61 / 2.90 | 2 / -99.58 / 3.81 |
| Bravo/1/SOL | 2 / -100.16 / 1.42 | 2 / -100.09 / 2.11 | 2 / -100.02 / 2.79 |
| Bravo/1/XRP | 1 / -49.43 / 0.91 | 1 / -49.40 / 1.35 | 1 / -49.37 / 1.78 |
| Bravo/1/DOGE | 3 / 45.51 / 2.65 | 3 / 43.11 / 3.92 | 3 / 40.76 / 5.17 |
| Bravo/1/ADA | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/1/LTC | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/1/LINK | 2 / -99.24 / 1.57 | 2 / -99.20 / 2.33 | 2 / -99.17 / 3.08 |
| Bravo/1/BCH | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/1/AVAX | 1 / -48.92 / 0.83 | 1 / -48.89 / 1.23 | 1 / -48.86 / 1.62 |
| Bravo/2/BTC | 3 / 40.71 / 5.48 | 3 / 37.48 / 8.02 | 3 / 34.35 / 10.44 |
| Bravo/2/ETH | 3 / 117.78 / 3.32 | 3 / 113.45 / 4.91 | 3 / 109.24 / 6.46 |
| Bravo/2/SOL | 2 / -52.87 / 1.45 | 2 / -53.56 / 2.16 | 2 / -54.22 / 2.85 |
| Bravo/2/XRP | 2 / 91.56 / 2.02 | 2 / 88.47 / 2.99 | 2 / 85.49 / 3.93 |
| Bravo/2/DOGE | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/2/ADA | 1 / -49.50 / 0.53 | 1 / -49.50 / 0.79 | 1 / -49.50 / 1.05 |
| Bravo/2/LTC | 2 / -100.70 / 1.76 | 2 / -100.63 / 2.61 | 2 / -100.56 / 3.43 |
| Bravo/2/LINK | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/2/BCH | 1 / -49.32 / 0.85 | 1 / -49.30 / 1.26 | 1 / -49.29 / 1.66 |
| Bravo/2/AVAX | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/3/BTC | 1 / -51.05 / 1.64 | 1 / -51.00 / 2.40 | 1 / -50.96 / 3.13 |
| Bravo/3/ETH | 1 / 142.04 / 1.62 | 1 / 138.28 / 2.38 | 1 / 134.66 / 3.12 |
| Bravo/3/SOL | 3 / 39.79 / 3.70 | 3 / 35.98 / 5.45 | 3 / 32.31 / 7.16 |
| Bravo/3/XRP | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/3/DOGE | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/3/ADA | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/3/LTC | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/3/LINK | 1 / -50.87 / 0.95 | 1 / -50.83 / 1.41 | 1 / -50.80 / 1.86 |
| Bravo/3/BCH | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |
| Bravo/3/AVAX | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 | 0 / 0.00 / 0.00 |




## DD, halt i granice obserwacji

- **Close-sampled liquidation equity DD**: peak/obsunięcie z zapisanej serii close, cash + pozostałe inventory po modeled sell i quote fee. **Closed-balance DD**: 10000 + net całkowicie zamkniętych pozycji. Nie są tym samym; historyczny `cash` przy otwartej pozycji nie jest closed balance.

- Risk gate obserwuje spójne fazy protective gaps/open, channel i kolejne admission/fills, a następnie wspólny close po wszystkich protective fills + marks ocalałych pozycji. Jego riskPeak nie jest ograniczony do close. Daily≤−1.5% lub equity≤95% riskPeak latche halt do końca. Wyjścia nadal działają. Dlatego halted=true nie wymaga close DD≥5%, a próg 5% nie jest gwarantowanym maksymalnym DD: D1/gapy i discrete observation mogą go przekroczyć.

- Przykład actual fixed ALFA base48/36: close DD 4.75%, brak haltu; fixed BRAVO base48/36: close DD 5.50%, halt. Przykłady nie uzasadniają zmiany progów. Eksport ma bool halt/refusals, ale **nie ma dokładnego timestamp/reason/phase pierwszego breach** (zaakceptowane M1). Nie rekonstruujemy go jako obserwowanego faktu z samego close DD.

- `IntrabarLowStress` jest pre-protection inventory wycenionym po jednoczesnych low wszystkich instrumentów. To konserwatywny niesynchronizowany bound, nie rzeczywiście zaobserwowany zsynchronizowany DD i nie tickowy risk trigger. Niezależny audyt odtworzył każdy punkt tej serii z raw-derived normalized low i fills.

- Signal po close, fill najwcześniej następny realny open; open+1 tick to znacznik modelu, nie tick feed. SL-gap po gorszym open, TP-gap konserwatywnie po target, SL-first przy obu hits; intrabar stop/TP mają czas obserwacji close. Boundary liquiduje po znanym ostatnim close z pełnym kosztem. Boundary obecne w cashflow/trade/fee/equity, wykluczone tylko z selection.



## Odzysk, budżet i prawdziwa historia wykonania

| Pozycja | Liczba / stan |
|---|---|
| Przed timeout: oryginalne CLI success / reported runs | 6 / 93 |
| Zweryfikowane dodatkowe complete elevated exports bez run.json | 19 |
| Wszystkie istniejące complete przed recovery | 112 |
| Nowe actual NavalBacktestRunner.Run | 9 (3 brakujące elevated IS + 1 elevated OOS + 5 double OOS) |
| Complete fizyczne account.json po recovery | 121 (112 zachowane bez zmian + 9 nowych) |
| Double IS reuse references | 18; zero nowych calls, zero nowych niezależnych prób statystycznych |
| Base IS powtórzone w elevated (15 sprzed timeoutu + 3 odzyskane) | 18 par identycznych account.json; nie podwajają evidence |
| Unikalne source+config identities po dedupe Python | 103 (nie jest to liczba niezależnych obserwacji rynku) |
| Pokryte logiczne account slots | 139 = 70 fixed + 23 WF base + 23 elevated + 23 double |
| NoSelection zamiast OOS | 1 fold na koszt (ALFA/2024), łącznie 3 bez konta OOS |
| Wasted/in-flight | co najmniej 1; dokładny original CPU invocation count bez journalu nie jest certyfikowalny |
| Account-attempt lower bound | 122 = 121 complete + ≥1 zabity |
| Konserwatywnie obciążony ceiling | 127 = 118 oryginalnych zarezerwowanych + 9 nowych |
| Budżet / pozostały zapas do conservative ceiling | 150 / 23 |
| Nowy CLI success dla przerwanego elevated/double | NIE: odzysk jest osobnym consumerem istniejącego API |


Oryginalny driver zapisywał `status:running` przed komendą, nie przed każdym kontem. Na podstawie sekwencyjnego source kolejnym rozpoczętym, ale bez eksportu był najpewniej Bravo fold3 IS40/30; to **wniosek**, nie nowy log startu. Zachowano niepewność: 112 eksportów i ≥1 wasted są potwierdzone/inferred z przerwania; do safety budget obciążamy wszystkie sześć niewypełnionych miejsc z original reserve118. Nowe dziewięć ma osobny trwały journal started/completed i execution metadata. Nie powtórzono żadnego ukończonego ID w tym samym scenariuszu; brakujący in-flight bez eksportu musiał zostać wykonany od nowa. Trzy brakujące elevated IS wykonano zgodnie z pierwotnym planem na base cost; ich identyczne wcześniejsze base IS są jawnie deduplikowane, nie stanowią nowej niezależnej próby.

Nie zmieniono `campaign-state.json`, logu timeoutowanej komendy ani katalogu `.naval-claim`. Nadal opisują historyczny failure. Recovery nie podszywa się pod pierwotny CLI: `originalCliRun:false`, osobne `recovery-plan.json`, `execution-state.json`, `recovery-execution.json`, selection i reuse map. Oryginalna ósemka CLI nigdy nie była ukończona; pokryto jej kryteria badawcze bez pełnego restartu.



## Niezależne uzgodnienie i kompletność eksportów

**121/121 actual kont uzgodnionych**, w tym wszystkie 19 z przerwanego elevated, plus 9 nowych. Python Decimal prec=65, tolerancja ≤1e−12 USDT. Bez wywołania runnera w audycie: ponownie policzono rzeczywiste buy/sell, fee model, inventory/cash, per-position net/fee/gross/R, wszystkie metryki, roczne i per-symbol stats; cash nigdy ujemny, inventory kończy na zero; start+cashflow=end cash=start+trade net=final liquidation equity. Z OHLC i fills odtworzono każdy close equity/cash/closed balance/DD oraz pre-protection low stress. Sprawdzono config hash, provenance i każdy wiersz/liczbę CSV, z zachowaniem semantycznie równych timestampów 0 vs .0000000.

| CSV | Wiersze danych (bez header; fizyczne actual exports) |
|---|---|
| trades | 2162 |
| cashflows | 4324 |
| equity | 154859 |
| refusals | 6467 |
| decisions | 403782 |


Wszystkie pięć CSV/konto ma nagłówki, także puste. Liczniki obejmują fizyczne eksporty, w tym faktycznie powtórzone IS; nie są deduplikowaną próbą statystyczną. `after-audit.json` indeksuje każde actual ID, hash wszystkich plików, config/range/metrics/counts. `logical-account-map.json` pokrywa dokładnie139 slotów; `reuse-map.json` opisuje18 double refs i18 par duplikatów z config/provenance/source/hash. JSON kont pozostaje pełnoprecyzyjny; tabele w tym raporcie są zaokrągloną prezentacją.



## Artefakty i ograniczenia akceptacji

Root wyników poza Git: `/home/ubuntu/projects/apex-data/naval-research/task3-v1/`. Oryginalne fixed i WF base zachowane; 19 eksportów WF elevated zachowane w oryginalnym katalogu. **Brakujące elevated i nowe double są wyłącznie w `recovery/actual/`**, nie kopiowane do CLI output.

- `recovery/summary.json`: actual/complete/reuse/wasted/budget i precise unmet original CLI acceptance.

- `recovery/before-audit.json`, `after-audit.json`, `before-hashes.json`, `after-hashes.json`, logi: source/data i reconciliation proof.

- `recovery/account-metrics.csv`: wszystkie121 actual kont i pełne required metrics; `annual-metrics.csv`: wszystkie segmenty wszystkich kont (nie tylko floty); `symbol-attribution.csv`: każdy członek każdego actual konta, także0.

- `recovery/selection-metrics.csv`, `selection-base.json`, `selection-elevated.json`: sample/score/selection z actualexports i pełnym source provenance/hash/range/config; `logical-account-map.{json,csv}`, `reuse-map.json`: dokładne mapowanie bez fake runów.

- `recovery/recovery-plan.json`, `execution-state.json`, `harness/Program.cs`, `harness/Recovery.csproj`, `resume.py`, `audit.py`, `artifacts.py`: trwały offline harness; kompilacja cache-only restore, consumer referencjonuje zaakceptowane DLL bez rebuild produktu. Nowe calls mają `recovery-execution.json` i osobne logi.

- Kryteria badawcze fixed/WF/per-symbol/annual/accounting/CSV/source-data/budget: pokryte. **Unmet literal original CLI acceptance**: elevated nie ma oryginalnego success/run.json, double CLI nie wykonano. Nie naprawiamy tej historii wrapperem. Nie ma blokera danych ani brakującego wybranego OOS po recovery.

- Ograniczenia: mała i zależna próba, zachodzące IS oraz te same OOS przy różnych kosztach nie są niezależną replikacją; historyczne okresy nie są certified untouched. Current metadata, TRADE D1, modeled koszty i wspólne dzienne low nie dają dowodu tick fills, real orderbook liquidity ani działania na koncie użytkownika. Brak exact risk-breach event eksportu. Żadnych obietnic zysku, autoryzacji handlu, promocji strategii ani certified execution validation.
