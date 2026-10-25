# Konfiguracja APEX Trading AI DEMO

Plik danych: `%LOCALAPPDATA%\APEX Trading AI\apex.config.json`. Edytuj wyłącznie przy zatrzymanym APEX i po zrobieniu kopii danych. Zwykle wystarcza polski panel. Nie publikuj pliku: zawiera token lokalnego API i klucz parowania. Nigdy nie wpisuj do niego hasła cTrader.

## Domyślne limity

- Ryzyko na transakcję: **0,5%** equity.
- Dzienna strata: **1,5%**.
- Maksymalny drawdown: **5%**.
- Dźwignia efektywna: **x5** (ekspozycja nominalna/equity, nie deklarowana dźwignia rachunku).
- Maksymalnie **3 pozycje** bota.
- Każde nowe zlecenie wymaga SL i TP. Brak martingale, gridu i uśredniania straty. Minimum wolumenu większe od dozwolonego budżetu oznacza pominięcie transakcji, także testowej.

Luki cenowe i poślizgi mogą spowodować stratę większą niż planowane ryzyko mimo SL. Limity są kontrolami programu, nie gwarancją ceny wykonania. Ryzyko i wyniki odnoszą się do konta zgodnie z danymi brokera; nie mieszaj ręcznej aktywności z testem bez analizy wpływu na equity.

## Ustawienia techniczne

- `HttpPort`: `5077` — lokalny panel i WebSocket; `BridgeTcpPort`: `5078` — opcjonalny MT5.
- `PairingKey`, `ApiToken`: generowane lokalnie. Zmiana parowania przez panel nie jest zmianą hasła cTrader.
- `Label`: `APEX`, musi być zgodne z mostem; nie zmieniaj przy otwartych pozycjach.
- `AllowedBrokers`: opcjonalna lista nazw brokerów. Sama nazwa nie uwierzytelnia zewnętrznej platformy — lokalny komputer musi być zaufany.
- `Risk`: domyślne limity nowych kont; ustawienia per konto są w `Accounts`.
- `MinScore`: `50`. To próg rankingu, **nie procent szans na zysk**.
- `News`: kalendarz makro, okno przed/po publikacji i `RequireCalendar`. Brak wymaganego aktualnego kalendarza blokuje wejście. Jeżeli `RequireCalendar=false`, program może działać bez kalendarza, ale nie chroni wtedy filtrem przed publikacją; nie zmieniaj tego ustawienia bez świadomości skutków.
- `Universe`: tryb automatyczny/ręczny, limity symboli i wykluczenia; krypto oraz nierozpoznane instrumenty są odrzucane także w trybie ręcznym.
- `ProfilePath`: własny profil tylko po walidacji. Wbudowany profil i reguły wejścia pozostają bazą; nie zmieniaj statusów, aby ominąć odrzucenie strategii.
- `Commission` / `Mt5DefaultCommission`: jawna prowizja jednej strony transakcji, gdy platforma nie udostępnia poprawnej informacji. Dopuszczalne typy zgodnie z modelem: `None`, `UsdPerLot`, `UsdPerMillionUsdVolume`, `PercentOfVolume`, `QuoteCurrencyPerLot`. Nie ustawiaj `None` na koncie prowizyjnym, aby odblokować handel.
- `OpenBrowserOnStart`: otwieranie panelu. Argumenty: `ApexDesktop.exe --data <katalog> --port <port> --no-browser`; `APEX_DATA_DIR` zmienia domyślny katalog danych.

## Tylko DEMO i restart

Ta wersja nie obsługuje handlu na REAL. Dawne pola `LiveTradingFeature`, `AllowLive`, `LiveConfirmation` ani fraza z historycznej dokumentacji nie stanowią zgody na odblokowanie. Panel nie udostępnia odblokowania konta rzeczywistego.

Start aplikacji/mostu i odtworzenie stanu nie powinny samodzielnie uruchamiać nowych wejść. Po restarcie sprawdź uzgodnienie pozycji, logi i limity, a następnie świadomie naciśnij START. Awaryjnej blokady nie resetuje zwykły restart. Nie usuwaj katalogu stanu w celu skasowania limitu lub ponowienia sygnału.

AI Lab nie jest częścią konfiguracji produktu: nie uruchamia się, nie uczy strategii i nie aktualizuje plików. Aktualizacje aplikacji wykonuje się ręcznie do nowego folderu.
