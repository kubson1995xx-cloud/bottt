# Znane ograniczenia i status gotowości

## Status gotowości (2026-10-15)

| Obszar | Status |
|---|---|
| Silnik, RiskEngine, zabezpieczenia wykonania | **Gotowe do testów DEMO.** 154 testy automatyczne, w tym integracyjne na prawdziwym procesie. |
| APEX Desktop (panel, mosty, AI Decision Center) | **Gotowe do testów DEMO.** Zbudowany pakiet win-x64; uruchomienia na Windows nie sprawdzono w tym środowisku. |
| Most cTrader | **Gotowe do testów DEMO.** Kompilacja 0 ostrzeżeń; protokół sprawdzony symulatorem; brak testu z prawdziwym serwerem cTrader. |
| Most MetaTrader 5 | **Eksperymentalny** – nieskompilowany; wymaga kompilacji i testu na DEMO. |
| Strategie | **Brak strategii o potwierdzonej przewadze.** Jedna eksperymentalna (tylko DEMO), reszta odrzucona przez testy. |
| Konto LIVE | **Zablokowane** i niezalecane. Odblokowanie wymaga osobnej, świadomej decyzji (3 kroki + START). |
| AI & Auto-Updates (AI Lab) | **Odłożone** decyzją użytkownika – moduł nie wchodzi w skład pakietu ani panelu. Kod i testy zostają w repozytorium do późniejszego wznowienia ([`HANDOFF.md`](../HANDOFF.md), [`AI_LAB_ZALEGLOSCI.md`](AI_LAB_ZALEGLOSCI.md)). |

**Wniosek: system jest gotowy do uruchomienia i obserwacji na koncie DEMO, a nie do handlu na koncie rzeczywistym.**

## Ograniczenia strategii i badań

- Na danych 2005–2020 nie znaleziono strategii z przewagą potwierdzoną na okresie odłożonym (szczegóły: [`RAPORT_BADAN.md`](../reports/RAPORT_BADAN.md)).
- Dane historyczne: ceny MID bez rzeczywistego spreadu, brak akcji i ETF, brak danych po 2020-05, brak historycznego kalendarza wiadomości. Wyniki na DEMO mogą się różnić od backtestu.
- Ocena okazji 0–100 to ranking, nie prawdopodobieństwo. Na tych danych nie wykazano, że wyższa ocena daje lepszy wynik.
- Filtr wiadomości obejmuje zaplanowane publikacje makro (kalendarz). Wydarzeń nieplanowanych nie przewiduje – przed nimi chronią filtry spreadu i zmienności oraz SL u brokera.

## Ograniczenia techniczne

- Most cTrader działa tylko w **cTrader Desktop na tym samym komputerze**. W cTrader Cloud połączenia sieciowe cBotów są ograniczone.
- Komputer, cTrader i APEX Desktop muszą działać, aby bot zarządzał pozycjami (trailing, break-even, wyjścia). Gdy nie działają, pozycje chroni wyłącznie SL/TP po stronie brokera.
- Klasyfikacja instrumentów jest heurystyczna (API cTrader nie udostępnia kategorii symbolu). Działa według zasady „w razie wątpliwości – nie handluj”, więc czasem pominie poprawny instrument.
- MT5 nie udostępnia prowizji przez API – trzeba ją ustawić ręcznie.
- Pliki wykonywalne nie są podpisane cyfrowo (ostrzeżenie SmartScreen).
- Wersja natywna cTrader (`ApexTradingAI.algo`) nie ma panelu premium ani AI Decision Center.
- Panel jest jednoużytkownikowy i lokalny. Nie jest przeznaczony do wystawiania w sieci.

## Co trzeba zrobić na Twoim komputerze

1. Zbudować pakiet (`tools\windows\Zbuduj-APEX.ps1`) – testy uruchomią się automatycznie.
2. Przeprowadzić [procedurę DEMO](PROCEDURA_DEMO_PEPPERSTONE.md), zaczynając od testu wykonania.
3. (Opcjonalnie) skompilować most MT5 i powtórzyć test wykonania na koncie DEMO MT5.
4. Obserwować wyniki na DEMO przez co najmniej kilka miesięcy przed jakąkolwiek decyzją o LIVE.
