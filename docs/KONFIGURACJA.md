# Konfiguracja APEX Desktop

Większość ustawień zmienia się w panelu. Plik `%LOCALAPPDATA%\APEX Trading AI\apex.config.json` zawiera także ustawienia, których **celowo nie da się zmienić z panelu** (np. globalny wyłącznik LIVE).
Edytuj plik **tylko przy zamkniętym APEX Desktop**. Uszkodzony plik jest odkładany jako kopia (`.uszkodzony-…`), a aplikacja startuje z bezpiecznymi wartościami domyślnymi (LIVE wyłączone).

## Najważniejsze pola

| Pole | Domyślnie | Opis |
|---|---|---|
| `HttpPort` | 5077 | Port panelu i mostu WebSocket (cTrader). |
| `BridgeTcpPort` | 5078 | Port mostu TCP (MetaTrader 5). |
| `PairingKey` | losowy | Klucz parowania mostów (zmiana: panel → Nowy klucz parowania). |
| `ApiToken` | losowy (≥ 32 znaki) | Token panelu. Nie udostępniaj go. |
| `Label` | `APEX` | Etykieta pozycji bota – musi zgadzać się z ustawieniem mostów. |
| `LiveTradingFeature` | **false** | Globalny wyłącznik handlu LIVE (patrz niżej). |
| `AllowedBrokers` | [] | Opcjonalna lista dozwolonych brokerów (fragment nazwy), np. `["Pepperstone"]`. |
| `Risk` | 0,5 / 1,5 / 5 / 3 / 5 | Domyślne limity ryzyka dla nowych kont (zmiana per konto w panelu). |
| `MinScore` | 50 | Minimalna ocena okazji (0–100, ranking – nie prawdopodobieństwo). |
| `News` | High, 30/30 min | Filtr wiadomości: `Enabled`, `MinImpact` (Low/Medium/High), `MinutesBefore`, `MinutesAfter`, `RequireCalendar`. |
| `NewsUrl` | kalendarz ForexFactory (JSON) | Źródło kalendarza makro. |
| `Universe` | auto | `Mode` (`auto`/`manual`), `Manual`, `Blacklist`, `MaxTotal`, `MaxPerClass`. |
| `ProfilePath` | null | Ścieżka do własnego profilu strategii (null = profil wbudowany z badań). |
| `Mt5DefaultCommission` | null | Domyślna prowizja dla kont MT5, np. `{"Kind":"UsdPerLot","Value":3.5}`. |
| `Accounts` | {} | Ustawienia per konto (klucz: `Platforma:Broker:Numer`). |
| `OpenBrowserOnStart` | true | Otwieranie panelu przy starcie. |

Ustawienia konta (`Accounts["cTrader:Pepperstone:5123456"]`): `Risk`, `MinScore`, `Commission`, `Universe`, `AutoStart` (DEMO), `AllowLive`, `LiveConfirmation`.

## Instrumenty

- Wybór automatyczny bierze płynne instrumenty z listy preferowanej. Rozpoznaje sufiksy brokerów (`.cash`, `m`, `.r`, `-ECN`…) i stosuje limity na klasę.
- Klasyfikacja jest heurystyczna i działa według zasady **„w razie wątpliwości – nie handluj”**: instrument nierozpoznany nie jest handlowany, a każda oznaka kryptowaluty (ticker, nazwa, opis) wyklucza instrument. Taki filtr czasem odrzuci zwykłą akcję (np. w opisie słowo zawierające „defi”) – to świadomy wybór.
- Instrument jest handlowany tylko wtedy, gdy ma poprawną specyfikację: wartość pipsa, krok i minimum wolumenu oraz **znaną prowizję**.

## Prowizja

- cTrader przekazuje prowizję automatycznie.
- MetaTrader 5 – ustaw w panelu (Połączenia platform → Prowizja konta MetaTrader 5) albo w pliku: `Mt5DefaultCommission` lub `Accounts[...].Commission`.
- cTrader z prowizją „nieznaną” (rzadkie): `Accounts["cTrader:Broker:Numer"].Commission = {"Kind":"UsdPerLot","Value":3.5}`.
  Dostępne typy: `None`, `UsdPerLot`, `UsdPerMillionUsdVolume`, `PercentOfVolume`, `QuoteCurrencyPerLot`. Wartość dotyczy **jednej strony** transakcji.

## Profil strategii

Profil (`config/profile_default.json`, wbudowany) określa, które pary strategia × klasa są:
- `Validated` – handel na DEMO i LIVE (obecnie **brak** takich par),
- `Experimental` – handel **wyłącznie na DEMO**,
- `Rejected` – brak handlu, nie da się włączyć z panelu.

Własny profil (`ProfilePath`) jest walidowany przy starcie. Zmiana statusu na `Validated` powinna wynikać z nowych, uczciwych testów (patrz [`RAPORT_BADAN.md`](../reports/RAPORT_BADAN.md)), a nie z chęci „włączenia” strategii.

## Konto LIVE

> Ta sekcja opisuje procedurę, **nie zalecenie**. Obecnie żadna strategia nie ma potwierdzonej przewagi. Przy wbudowanym profilu bot na koncie LIVE nie otworzy żadnej pozycji.

Handel na koncie rzeczywistym wymaga **wszystkich** poniższych kroków, wykonanych świadomie przez użytkownika:
1. Zamknij APEX Desktop i ustaw w `apex.config.json`: `"LiveTradingFeature": true`.
2. W moście cTrader ustaw parametr **Zezwól na konto LIVE = Tak** (w MT5: `InpAllowLive = true`).
3. Uruchom APEX, w panelu: **Ryzyko i bezpieczeństwo → Odblokuj konto LIVE…** i wpisz dokładnie frazę `LIVE <numer konta> AKCEPTUJE RYZYKO`.
4. Konto LIVE **nigdy nie startuje automatycznie** – trzeba ręcznie nacisnąć START.

Dodatkowo na koncie LIVE strategie eksperymentalne są wyłączone (podwójnie: profil i silnik), a test wykonania jest niedostępny. Wycofanie zgody: **Zablokuj konto LIVE** albo `LiveTradingFeature: false`.

## Zmienne środowiskowe i argumenty

- `APEX_DATA_DIR` – inny katalog danych.
- `ApexDesktop.exe --data <katalog> --port <port> --no-browser`.
