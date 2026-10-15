# Przegląd bezpieczeństwa i jakości (2026-10-15)

Zakres: APEX Desktop (serwer HTTP, mosty), silnik i RiskEngine, mosty cTrader/MT5, konfiguracja, skrypt budowy. Metoda: przegląd kodu i testy automatyczne (w tym integracyjne na prawdziwym procesie).

## Bezpieczeństwo handlu

| Wymaganie | Realizacja | Weryfikacja |
|---|---|---|
| Brak handlu na REAL podczas tworzenia i testów | Testy wyłącznie na atrapie brokera, symulatorze i koncie DEMO; LIVE blokowane na 3 poziomach (most, strażnik konta, globalny wyłącznik w pliku) + brak autostartu LIVE | `LiveAccount_*`, `Live_*` (testy), integracyjny `LiveAccount_IsBlocked_NoOrdersPossible` |
| Każda pozycja z SL | Silnik odrzuca zlecenie bez SL; most odrzuca `slPips ≤ 0`; pozycja bota bez SL jest natychmiast zabezpieczana lub zamykana; nowe wejścia blokowane, dopóki istnieje pozycja bez SL | `PositionOpenedWithoutStopLoss_IsProtectedImmediately`, `PositionWithoutStopLoss_BlocksNewTrades` |
| Brak martingale, gridu, uśredniania, zwiększania po stracie | Jedna pozycja na instrument; wielkość wyłącznie z budżetu ryzyka (stały % kapitału, zaokrąglanie w dół); brak logiki dokładania pozycji | przegląd kodu `PositionSizer`, `RiskManager`, `TradingEngine.Prequalify`; testy limitów |
| Brak ukrytej dźwigni | Limit dźwigni efektywnej (suma wartości nominalnych / kapitał ≤ 5), limit depozytu i poziomu marginu | `Leverage_Enforced`, `Margin_Enforced` |
| Duplikaty zleceń | Deterministyczny identyfikator sygnału; rejestr zapisywany **przed** wysłaniem; niepewna odpowiedź = weryfikacja, nigdy ponowienie | `RepeatedCycles_*`, `Restart_*`, `Uncertain*`, `RejectedOrder_*` |
| Pozycje ręczne i innych botów | Silnik zarządza wyłącznie pozycjami z dokładnie swoją etykietą; mosty modyfikują/zamykają tylko pozycje APEX | `ManualAndForeignPositions_AreNeverTouched`, integracyjny test z awaryjnym stopem |
| Kryptowaluty wykluczone | Wielowarstwowa klasyfikacja „fail-closed” + kontrola przed każdym wejściem (także gdy broker błędnie oznaczy symbol) | `Crypto_*`, `CryptoInstrument_MisclassifiedByBroker_IsNeverTraded`, integracyjny `Connect_ExcludesCrypto_*` |
| AI Decision Center nie wpływa na handel | Rejestrator pasywny (wyjątki przechwytywane), punkty końcowe tylko GET | `Recorder_DoesNotChangeTradingDecisions` (5 scenariuszy) |

## Bezpieczeństwo aplikacji

| Obszar | Stan |
|---|---|
| Ekspozycja sieciowa | Kestrel i serwer TCP nasłuchują tylko na 127.0.0.1; połączenia spoza pętli zwrotnej odrzucane. |
| DNS rebinding | Lista dozwolonych nagłówków `Host` (127.0.0.1/localhost z portem) – inne → 403. |
| API panelu | Token 256-bit (`RandomNumberGenerator`), porównanie w stałym czasie, wymagany dla `/api/*`; POST tylko z `application/json` (utrudnia CSRF). |
| XSS | Brak `innerHTML`/`eval`; DOM budowany przez `textContent`; CSP `default-src 'self'`, bez skryptów i stylów inline. Dane z mostów (nazwy platform) są dodatkowo przycinane i oczyszczane. |
| Mosty | Klucz parowania (porównanie w stałym czasie); limit 8 MB na komunikat; 15 s na `hello`; odrzucanie WebSocket z nagłówkiem `Origin` (strony WWW w przeglądarce); jeden most na konto. |
| Ścieżki plików | Klucz konta zamieniany na bezpieczną nazwę katalogu (tylko litery/cyfry/`_`). |
| Pliki stanu i konfiguracji | Zapis atomowy (plik tymczasowy + zamiana) z kopią `.bak`; uszkodzony stan → start z wstrzymanymi wejściami. |
| Sekrety w repozytorium | Brak kluczy API, haseł i tokenów (przeszukanie kodu). Klucz i token generowane lokalnie przy pierwszym starcie. |
| Uprawnienia cBotów | `AccessRights.None` (bez pełnego dostępu do systemu). |
| Jedna instancja | Blokada pliku `apex.lock` na katalog danych. |

## Ryzyka rezydualne (świadomie zaakceptowane lub do decyzji użytkownika)

1. **Lokalne procesy** tego samego użytkownika Windows mogą odczytać `apex.config.json` (token, klucz parowania). Ochrona przed złośliwym oprogramowaniem na komputerze jest poza zakresem aplikacji.
2. **Brak podpisu cyfrowego** plików wykonywalnych – zalecane budowanie pakietu samodzielnie ze źródeł.
3. **Most MT5 nieskompilowany** – przed użyciem kompilacja i test wykonania na DEMO.
4. **Heurystyczna klasyfikacja instrumentów** – nowa egzotyczna kryptowaluta o nieznanym tickerze i neutralnym opisie mogłaby nie zostać rozpoznana. Ograniczenie: tylko klasy z ważną strategią są skanowane (domyślnie indeksy), a symbol musi przejść klasyfikację jako indeks.
5. **Brak przewagi strategii** – największe ryzyko finansowe nie jest techniczne. Patrz [`RAPORT_BADAN.md`](../reports/RAPORT_BADAN.md).

## Jakość kodu

- Silnik niezależny od platformy (te same klasy w handlu, backteście i testach) – brak rozbieżności „backtest vs. live” w logice.
- Kompilacja cBotów bez ostrzeżeń; zastąpione przestarzałe wywołania API cTrader.
- Wyjątki w cyklu silnika są przechwytywane; 5 kolejnych błędów → wstrzymanie nowych wejść.
- 120 testów automatycznych uruchamianych przy każdej budowie pakietu (`Zbuduj-APEX.ps1` przerywa budowę przy błędzie).
