# Raport badań strategii APEX Trading AI

Data: 2026-10-15. Metodologia (ustalona przed obejrzeniem wyników): [`docs/METODOLOGIA_TESTOW.md`](../docs/METODOLOGIA_TESTOW.md).
Surowe wyniki: `research_table_r1.md`, `research_table_r2.md`, `research_console_r1.txt`, `research_console_r2.txt`, transakcje w `trades/`.
Kod badań: `tools/Apex.Backtester` – ten sam silnik (`TradingEngine`), te same strategie, ten sam RiskManager co w handlu.

## Najważniejsze w skrócie

> **Żadna z przetestowanych strategii nie wykazała stabilnej, potwierdzonej na danych odłożonych przewagi.**
> Bot nie jest „gotowy do zarabiania”. Jest gotowy do **testów na koncie DEMO**: wykonywania zleceń, zabezpieczeń i obserwacji.

| Wynik | Liczba par strategia × klasa |
|---|---:|
| Przetestowane (runda 1 + runda 2) | 31 |
| Spełniły wszystkie kryteria walk-forward | 1 (Kupno spadków / Indeksy) |
| Potwierdzone na okresie odłożonym (holdout 2018–2020.05) | **0** |

Profil domyślny (`config/profile_default.json`, wbudowany w aplikację):
- **Kupno spadków (IndexDipBuy) na indeksach** – status `Experimental`: działa **wyłącznie na kontach DEMO** (podwójna blokada: `StrategyBook` oraz silnik).
- Wszystkie pozostałe pary – status `Rejected`: kod pozostaje, ale strategia nie otwiera pozycji i nie da się jej włączyć z panelu.

Skutek praktyczny: na koncie DEMO bot handluje rzadko, a przez większość czasu czeka. Sygnały są dzienne i tylko na indeksach. W backteście portfel z limitami ryzyka miał 113 transakcji w 13 lat, czyli ok. 9 rocznie.

## Dane i koszty

- 27 instrumentów: FX, złoto, ropa, gaz, 11 indeksów, 4 obligacje, 4 towary rolne. Dane M1 MID 2005–2020.05 (OANDA/histdata, repozytorium publiczne).
- Koszty konserwatywne: modelowany spread (×3 w rolloverze), prowizja 3,5 USD/lot/stronę dla FX i złota, poślizg wejścia i wyjścia, luki po gorszej cenie, swap 2,5% rocznie płacony przez obie strony, potrójny swap.
- Scenariusze kosztów ×1,5 i ×2.
- Ograniczenia: ceny MID (bez rzeczywistego spreadu historycznego), brak akcji i ETF, brak danych po 2020-05, brak historycznego kalendarza wiadomości.

## Runda 1 – 25 par (5 rodzin strategii × klasy aktywów)

Pełna tabela: `research_table_r1.md`. Wyniki OOS (walk-forward 2009–2017, parametry wybierane wyłącznie na danych wcześniejszych):

| Rodzina | Najlepsza klasa (E[R] OOS) | Najgorsza klasa | Wniosek |
|---|---|---|---|
| Trend–korekta (H1/H4) | Energia −0,033 R | Obligacje −0,217 R | ujemna we wszystkich klasach; 0% kombinacji siatki dodatnich |
| Wybicie Donchiana (H4/D1) | Indeksy −0,004 R | Obligacje −0,213 R | blisko zera przed kosztami, ujemna po kosztach |
| Powrót do średniej (H1/H4) | Forex −0,006 R | Towary −0,509 R | za mało transakcji i ujemne |
| Impuls momentum (H1/H4) | Metale −0,030 R | Indeksy −0,110 R | ujemna, 0/9 zyskownych lat na FX |
| **Kupno spadków (D1, RSI(2))** | **Indeksy +0,051 R** | – | **jedyna para spełniająca kryteria** |

Kupno spadków / Indeksy (OOS): n = 291, E[R] = +0,051 R, t = 1,85, PF = 1,33, 7/9 zyskownych lat, 100% kombinacji siatki dodatnich, koszty ×1,5: +0,063 R, ×2: +0,031 R.

Obserwacja: straty malały wraz z interwałem (M15 → H1 → H4). Koszty transakcyjne niszczą przewagi intraday na danych tej jakości.

## Runda 2 – trend dzienny (zarejestrowana przed uruchomieniem)

Hipoteza z literatury (time-series momentum, zasady „Turtle”): wybicie kanału Donchiana na D1. 24 kombinacje × 6 klas.

| Klasa | n OOS | E[R] OOS | t | lata+ | decyzja |
|---|---:|---:|---:|---:|---|
| Forex | 160 | −0,050 | −0,39 | 4/9 | odrzucona |
| Metale | 60 | −0,040 | −0,22 | 3/9 | odrzucona |
| Energia | 80 | +0,005 | 0,03 | 5/9 | odrzucona |
| Indeksy | 138 | −0,188 | −1,40 | 3/9 | odrzucona |
| Obligacje | 40 | −0,777 | −5,77 | 0/9 | odrzucona |
| Towary | 125 | −0,223 | −1,84 | 2/9 | odrzucona |

Trend dzienny nie dał przewagi w żadnej klasie w latach 2009–2017 po kosztach. Obligacje przegrywały także przed kosztami (sprawdzone w plikach transakcji). Przyczyną była niska zmienność względem SL i koszt finansowania.

## Portfel z pełnym zarządzaniem ryzykiem

Ustawienia: 0,5% na transakcję, limit dzienny 1,5%, maks. DD 5%, maks. 3 pozycje, dźwignia ≤ 5, limity korelacji i klas.

| Okres | Transakcje | Wynik | E[R] | t | PF | Maks. DD |
|---|---:|---:|---:|---:|---:|---:|
| Badawczy 2005–2017 (ocena ≥ 50) | 113 | 10 000 → 10 204 USD (+2,0% w 13 lat) | +0,036 | 0,90 | 1,30 | 1,60% |
| **Holdout 2018–2020.05** | 21 | 10 000 → 9 944 USD (−0,6%) | **−0,115** | −0,93 | 0,71 | 0,93% |

Monte Carlo (10 000 losowań transakcji, 0,5% na R):
- okres badawczy: maks. DD – mediana 1,8%, 99. percentyl 5,0%; P(DD ≥ 5%) = 0,9%;
- holdout: wynik 5./50./95. percentyl −3,4% / −1,2% / +0,8%.

**Wniosek:** dodatni wynik okresu badawczego nie potwierdził się na danych odłożonych. Przy ~31 testowanych hipotezach jedno „przejście” progu t ≥ 1,65 jest zgodne z przypadkiem. Dlatego strategia ma status `Experimental` (tylko DEMO), a nie `Validated`.

## Ocena okazji (0–100) – czy wyższa ocena oznacza lepszy wynik?

Ocena to **ranking jakości setupu, nie prawdopodobieństwo**. W okresie badawczym przedziały 70–80 i 80+ miały podobne E[R] (+0,053 R i +0,055 R), a przedział 50–60 był ujemny (n = 11). W holdout wszystkie przedziały były ujemne. Na tych danych nie ma dowodu, że ocena przewiduje wynik. Próg 50 pozostaje filtrem jakości danych i kosztów, nie obietnicą skuteczności.

## Co jest potrzebne, aby uznać strategię za zweryfikowaną

1. Co najmniej 3–6 miesięcy pracy na DEMO z wynikami zgodnymi z backtestem (rozkład R, częstość, poślizgi). Dziennik `transakcje.csv` pozwala to porównać.
2. Test na świeżych danych po 2020-05 (np. eksport historii z cTrader) z **niezmienionymi** parametrami.
3. Dopiero wtedy zmiana statusu na `Validated` w profilu – świadomie, przez użytkownika.

## Rejestr hipotez

Wszystkie testowane hipotezy (także odrzucone) są w raportach. Nic nie zostało pominięte ani „dostrojone” po obejrzeniu holdoutu. Holdout użyto dwa razy (runda 1 i runda 2), co raport jawnie odnotowuje.
