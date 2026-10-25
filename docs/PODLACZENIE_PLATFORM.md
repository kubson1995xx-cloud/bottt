# Połączenie Pepperstone cTrader DEMO

## Zalecana ścieżka

`Pepperstone cTrader Desktop DEMO → ApexBridge.algo → ws://127.0.0.1:5077/bridge → APEX Desktop`

Oba programy działają na **tym samym komputerze Windows**. Most pobiera dane i wykonuje zatwierdzone polecenia; strategie i RiskEngine pracują w APEX Desktop. Nie trzeba rejestrować aplikacji OAuth ani przekazywać hasła brokerowi przez APEX — zaloguj się samodzielnie wewnątrz cTrader.

1. W cTrader Desktop wybierz konto Pepperstone **DEMO**. Sprawdź numer i oznaczenie typu konta. Nie wybieraj konta rzeczywistego.
2. Uruchom APEX z gotowego pakietu. Otwórz **Połączenia platform** i skopiuj lokalny **Klucz parowania**.
3. Otwórz `cTrader\ApexBridge.algo` dwuklikiem. W cTrader → **Algo / Automate → cBots** wybierz `ApexBridge` i dodaj instancję na dowolnym wykresie, np. EURUSD H1.
4. W parametrach wklej klucz parowania. Adres APEX pozostaw `ws://127.0.0.1:5077/bridge`, etykietę pozycji `APEX`. Parametr LIVE, jeżeli widoczny dla zgodności, nie daje zgody na REAL w tej wersji.
5. Uruchom instancję **lokalnie**, nie w Cloud. W APEX musi pojawić się zgodny numer konta, platforma cTrader i oznaczenie DEMO. Poczekaj na pobranie symboli i świec.
6. Pozostaw silnik zatrzymany do [kontrolowanej procedury DEMO](PROCEDURA_DEMO_PEPPERSTONE.md).

Nie uruchamiaj dwóch mostów, starego pakietu APEX ani natywnego `ApexTradingAI` na tym samym koncie. Jedno połączenie jest przyjmowane na konto. Native `ApexTradingAI.algo` jest alternatywą bez polskiego panelu Desktop, a nie dodatkiem, który należy uruchomić równocześnie.

## Dane i koszty

Most przekazuje konto, pozycje, świeże notowania i świece dostępne z cTrader. Nieaktualne dane i nieznane koszty blokują nowe zlecenia. Typ/kwota prowizji i specyfikacja kontraktu muszą odpowiadać Twojemu rachunkowi; nie zakładaj jednej stałej stawki dla wszystkich walut, symboli i typów konta.

Strategie wybierają instrumenty zgodnie z istniejącym profilem. Domyślnie aktywna jest eksperymentalna strategia indeksowa; brak sygnału na EURUSD nie oznacza uszkodzonego połączenia. Test wykonania może wybrać inny poprawnie sklasyfikowany instrument spełniający ograniczenia.

## Bezpieczeństwo i ponowne połączenie

- Ta wersja jest **DEMO-only**; nie ma instrukcji ani przycisku odblokowania REAL.
- Każde nowe zlecenie musi zawierać SL i TP. Bot nie dotyka pozycji ręcznych/innych botów; nie nadawaj im etykiety `APEX`.
- Lokalny klucz parowania chroni przed przypadkowym podłączeniem. Nie publikuj go, nie umieszczaj w repozytorium. Nowy klucz dotyczy nowych połączeń; istniejące działają do rozłączenia.
- Po awarii sprawdź historię, pozycje i stan silnika przed ponownym START. Nie ponawiaj testu tylko dlatego, że panel czeka na odpowiedź.
- SL/TP pozostają u brokera przy wyłączonym komputerze; trailing i pozostałe decyzje wymagają działającego Windows, APEX i cTrader.

## MT5 — opcjonalne, poza główną ścieżką

Plik `MT5-opcjonalny\ApexBridge.mq5` wymaga kompilacji F7 w MetaEditorze i osobnej weryfikacji DEMO. Ustaw lokalny adres `127.0.0.1`, port `5078` i klucz parowania. Prowizję MT5 trzeba skonfigurować jawnie. Nie ma tu potwierdzenia kompilacji ani działania z brokerem MT5. Brak terminala MT5 i awaria portu TCP nie mogą uniemożliwiać użycia cTrader przez WebSocket.

## Dowód połączenia

Zapisz w prywatnym protokole: wersję/commit pakietu, zgodny numer konta DEMO, czas notowań, saldo i widoczny status połączenia. Do dowodu transakcji potrzebne są dodatkowo pozycja i wpis w historii **prawdziwego cTrader**, nie sam zielony napis symulatora lub testów jednostkowych. Zasłoń dane osobowe przed udostępnieniem zrzutu.
