# APEX Naval Command — Windows x64

**Windows desktop-local — bez ręcznego logowania. Kandydat oczekuje niezależnego odbioru.** Wymagane są testy dokładnego EXE: pierwszy start, aktualizacja poprzedniego profilu, przeglądarka bez podawania poświadczeń i restart. Sprawdź osobne wyniki dla dokładnego SHA źródeł i ZIP-a. Hosted Windows Server nie jest fizycznym Windows 11 Pro użytkownika.

## Rozpakowanie i start

1. Porównaj SHA-256 pliku `APEX-NAVAL-COMMAND-Windows-x64.zip` z otrzymanym z zaufanego źródła plikiem `.zip.sha256` (`Get-FileHash -Algorithm SHA256`). Suma i manifest nie są podpisem wydawcy. Pliki nie są podpisane: nie omijaj bezrefleksyjnie SmartScreen ani ostrzeżeń systemu; przy nieznanym źródle przerwij.
2. Rozpakuj **cały ZIP** do nowego lokalnego folderu. Nie uruchamiaj z wnętrza ZIP i nie przenoś samego EXE. Zachowaj `wwwroot`, `presentation`, dokumentację i weryfikator.
3. Główne wejście: dwuklik `APEX-Desktop\ApexDesktop.exe`. `Uruchom APEX.cmd` jest tylko opcjonalnym skrótem. Pakiet self-contained zawiera .NET 8; odbiorca nie instaluje .NET SDK, Node, Pythona, Visual Studio ani osobnego VC++ redistributable. Opcjonalne narzędzia weryfikacji/budowania nie są zależnością aplikacji. MT5 wymaga osobnego MetaEditora wyłącznie do użycia opcjonalnego źródła mostu.
4. Nowy, pusty katalog danych Windows otrzymuje prywatny profil `windows-local-presentation-v1`. **Nie wpisujesz loginu ani hasła i nie zatwierdzasz okna poświadczeń.** Ten sam start bez logowania działa na kompletnym, prawidłowym profilu prezentacyjnym poprzedniego wydania, bez usuwania ani resetowania danych.
5. Po poprawnym związaniu portu aplikacja prosi domyślną przeglądarkę o otwarcie `http://127.0.0.1:5077/?view=naval` (lub ustawionego portu). W trybie desktop-local następuje od razu stałe przekierowanie `/naval/`, bez Basic popup. Jeżeli system nie otworzy przeglądarki, otwórz ręcznie lokalny adres bez sekretu pokazany w komunikacie. `/` bez query pozostaje starym polskim panelem. Reader Bearer ma tylko dwa GET API, nie dostęp do dokumentów/UI.

Opcjonalny podgląd prywatnych poświadczeń do **jawnie chronionego trybu/API**, bez uruchamiania hosta i rotacji (nie jest potrzebny do zwykłego startu):

```powershell
& '.\APEX-Desktop\ApexDesktop.exe' --show-local-access
```

Dla własnego katalogu dodaj `--data 'C:\pełna\prywatna-ścieżka'`. Nie przekazuj hasła w argumentach. Weryfikacja plików: `powershell -NoProfile -File .\Sprawdz-Pakiet.ps1 -Pakiet .` (jeśli lokalna polityka pozwala uruchomić zaufany skrypt; nie obniżaj jej w ciemno).

## Dane, aktualizacje i bezpieczna odmowa

Domyślny katalog danych: `%LOCALAPPDATA%\APEX Trading AI`. Precedencja: `--data`, następnie `APEX_DATA_DIR`, następnie domyślny KnownFolder. Dane są **poza całym pakietem**, chronione natywnymi ACL dla bieżącego użytkownika, SYSTEM i Administratorów. Poświadczenia webowe przechowuje CurrentUser DPAPI oraz mapa skrótów; nie są to klucze brokera. Nie wysyłaj konfiguracji, map, plików DPAPI ani prywatnych logów. Pliki natywne, jeśli są potrzebne, rozpakowuje mechanizm .NET poza ZIP-em. W tym wariancie .NET 8 kod CoreCLR/JIT/hostingu jest statycznie związany z EXE: smoke wiąże sekcje wykonywalne z rzeczywistym szablonem hosta SDK, sprawdza pochodzenie załadowanych modułów i ACL katalogu ekstrakcji oraz raportuje faktyczną liczbę rozpakowanych plików (również zero).

Istniejąca poprawna konfiguracja i własne profile pozostają zachowane, również `NavalRead` wyłączone. Rozpoznany kompletny profil prezentacyjny Windows dostaje desktop-local wyłącznie w pamięci procesu; pliki konfiguracji, mapy i DPAPI nie są migrowane ani nadpisywane. Własne chronione konfiguracje bez znacznika prezentacji pozostają chronione. Jawne `NavalRead.RequireAuthentication=true` zachowuje Basic Operator / Bearer Reader również dla profilu prezentacji. To tryb legacy, a nie portal bezpieczny dla roli Reader. Nie przenoś poświadczeń między użytkownikami: DPAPI wiąże je z kontem Windows. Nie usuwaj ani nie resetuj danych po błędzie.

- `HOST_DATA_PATH_INVALID`, `HOST_DATA_PERMISSIONS_INVALID`, `HOST_DATA_UNAVAILABLE`: wybierz nowy prywatny lokalny katalog poza wydaniem albo zleć administratorowi analizę ścieżki/ACL, linków i miejsca na dysku. **Nie** nadawaj Everyone/Users pełnego dostępu i nie obchodź kontroli. Program nie naprawia niebezpiecznych istniejących ACL.
- `HOST_LOCAL_SETUP_INCOMPLETE` lub problem DPAPI: zachowaj wszystkie pliki i kopię zapasową. Przerwanej inicjalizacji nie leczy się kasowaniem map/konfiguracji. Użyj tego samego konta Windows i zweryfikowanego kompletu danych; odzyskanie wymaga świadomej diagnozy.
- `HOST_ALREADY_RUNNING`: korzystaj z istniejącej instancji; nie usuwaj blokady uruchomionego procesu.
- `HOST_HTTP_BIND_FAILED`: sprawdź własny proces/port, bez przechodzenia na obcy serwer. Program nie szuka innego portu automatycznie. Opcjonalny zajęty TCP mostu daje degradację, nie oznacza poprawnego połączenia z platformą.

Aktualizuj ręcznie do **nowego folderu**, po finansowym STOP, sprawdzeniu pozycji i zakończeniu własnego procesu. Zachowaj cały prywatny katalog danych z ACL; backup również musi być prywatny. Nie usuwaj rejestrów sygnałów, limitów, planów ani latch STOP. Nie kopiuj prywatnych danych do wydania.

## Granica zaufania desktop-local

Brak hasła oznacza świadome zaufanie do **procesów/użytkowników tego samego komputera mających dostęp do loopback**. To nie izolacja między użytkownikami Windows ani tryb dla współdzielonego/niezaufanego komputera. Program nadal wiąże tylko `127.0.0.1`, kontroluje Host (DNS rebinding), Origin i Fetch Metadata. Obce strony, formularze cross-origin i preflight nie mogą sterować API; mutacje wymagają JSON oraz niestandardowego nagłówka anty-CSRF `X-Apex-Local: 1`. Nie jest on hasłem ani prawem do START. Nie publikuj portu przez proxy/LAN/Internet. Dla wymaganej izolacji użyj jawnie chronionego trybu, nie wyłączaj ACL/DPAPI. Rola Reader pozostaje API-only; podane Bearer credentials nie są podnoszone do Operatora.

## Ograniczenia i handel

Pięć widoków Naval i opcjonalne 3D przedstawiają dołączony **historyczny syntetyczny checkpoint**, wyprodukowany podczas budowy. `UNKNOWN`, `NOT_CONFIGURED`, brak brokerskiego P&L i historyczna świeżość są celowe. To nie rachunek, rynek na żywo, wynik inwestycyjny, sygnał ani zgoda na START. Nowy profil ma **zero kont runtime**, bez automatycznego START i bez automatycznego pobierania wiadomości. Wszystkie sześć rodzin strategii i dotychczasowy profil są zachowane; nie ma promocji odrzuconych strategii. Limity pozostają 0,5% / 1,5% / DD 5% / x5, obowiązkowe SL/TP, Emergency i ochrona przed duplikatami. REAL jest trwale blokowany; brak AI Lab/uczenia/updatera.

Zamknięcie karty **nie jest STOP**. Finansowy STOP/pauza, AWARYJNY STOP i zamknięcie procesu mają różne skutki. Przed wyłączeniem aplikacji użyj właściwego polecenia i sprawdź faktyczne pozycje. Stary tekstowy panel/Emergency jest dostępny Operatorowi również przy błędzie źródła Naval; samo zamknięcie procesu nie potwierdza zamknięcia pozycji.

Wszystkie listenery aplikacji są tylko `127.0.0.1`. Nie wystawiaj portów na LAN/Internet, nie zmieniaj zapory/routera w celu ich publikacji. cTrader Desktop + ApexBridge pozostają osobną lokalną ścieżką; nie uruchamiaj dwóch mostów na jednym koncie. Żadne rzeczywiste konto, prywatny broker, transakcja DEMO/LIVE, sprzętowy GPU ani rentowność nie są zaakceptowane przez ten kandydat. Rzeczywista transakcja DEMO wymaga osobnej wyraźnej zgody.

W `apex-build.json` pozostają `windowsRuntimeTested=false`, `brokerTested=false`, bo ZIP jest zapieczętowany **przed** smoke. Rzeczywiste wyniki są zewnętrzne, związane z SHA tego samego ZIP-a; nie modyfikuj archiwum, by zmieniać flagi. Zobacz `Dokumentacja\INSTALACJA_WINDOWS.md` i `NAVAL_READ_PUBLICATION.md`.
