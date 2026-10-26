# Przegląd bezpieczeństwa APEX DEMO — 2026-10-26

Wykonano niezależny przegląd Core/Desktop/platform/pakowania, a istotne ustalenia poprawiono regresjami RED→GREEN. Szczegóły wykonanych testów: [WYNIKI_TESTOW.md](WYNIKI_TESTOW.md). Nie jest to certyfikat bezpieczeństwa ani odbiór brokerski.

## Kontrole

- REAL zablokowany bezwarunkowo pomimo dawnych flag/fraz; brak odblokowania w panelu.
- Domyślne 0,5% ryzyka, 1,5% straty dziennej, DD 5%, x5 i 3 pozycje. Wolumen w dół; koszt obejmuje poprawny FX, prowizję i minimum brokera. SL i TP obowiązkowe.
- Trwała rezerwacja przed open, dziennik idempotencji cTrader, niepewna odpowiedź bez ślepego ponawiania.
- Brak autostartu/reconnect-startu, trwały Emergency Stop, uszkodzony zapis zachowany bez cofania do starego backupu.
- Pełne zamknięcie własnego ID na znanym DEMO nie zależy od dodatniego equity lub sprawnego dysku. Nadal wymaga połączenia i dokładnej etykiety; partial-close/open nie dostają wyjątku.
- Pozycje obce nietykane; brak kryptowalut/martingale/gridu/uśredniania.
- Czas ticka z SDK, zamknięte świece, brak przyszłych danych, przypięta tożsamość konta.
- Kolejka/transport: anulowanie i deadline przed wykonaniem, brak późnego open, zachowanie ważnego odebranego closed podczas reconnect.
- Test wykonania nie certyfikuje close, jeżeli pozycja zniknęła wcześniej naturalnie; wymagane potwierdzenia poleceń.

## Aplikacja

Loopback HTTP/WebSocket/TCP, lista Host przeciw DNS rebinding, token API w stałym czasie, POST JSON, CSP, bez CDN. Most wymaga klucza i odrzuca Origin przeglądarki; jeden most na konto. Nie uruchamiaj równocześnie natywnego bota i mostu na tym samym koncie.

Atomowe pliki z flush, widoczne błędy; jedna instancja na katalog danych. Flush nie zastępuje testów utraty zasilania konkretnego dysku. Brak sekretów brokera w Git; lokalną konfigurację chroni użytkownik Windows.

Pakiet po testach, nowy/pusty folder docelowy, manifest SHA-256 sprawdzający zmiany/dodatkowe pliki; bez danych użytkownika i AI Lab. Moduł uczenia/auto-updates nie uruchamia się i nie ma endpointów w produkcie. Aktualizacje ręczne.

## Ryzyka pozostające

Brak sieci/platformy/zasilania uniemożliwia gwarancję zamknięcia; operator musi mieć cTrader. Luki i poślizgi mogą przekroczyć planowane ryzyko. Pełne close przy awarii rejestru może nie mieć trwałego lokalnego audytu — sprawdź broker History. Niepewnego opóźnionego wykonania nie rozstrzyga sam timeout.

Procesy tego samego użytkownika mogą odczytać token/klucz. Nie wystawiaj panelu w sieci i nie publikuj apex.config.json. EXE nie ma podpisu cyfrowego; hash nie potwierdza wydawcy.

MT5 nie był kompilowany ani odbierany; klasyfikacja instrumentów jest heurystyczna. Rzeczywiste API/LocalStorage cTrader, koszty Pepperstone i Windows 11 wymagają odbioru docelowego. **Nie wykonano żadnej transakcji brokerskiej.** Rentowność nie jest gwarantowana; ochronny TP 3R zmienia wyjścia względem starych badań.
