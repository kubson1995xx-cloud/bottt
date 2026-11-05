# APEX — kontroler Windows x64 (kandydat inżynieryjny)

Jeden silnik działa na VPS. Ten EXE wyświetla i steruje jego stanem; nie przenosi rachunku, historii, kapitału ani kluczy OKX na Windows. Widoki Naval pozostają opisaną prezentacją historyczną/syntetyczną. Bieżące obserwacje VPS znajdują się w osobnym panelu „Silnik VPS”.

## Uruchomienie
1. Rozpakuj CAŁĄ paczkę do nowego katalogu. Uruchom `APEX-Desktop\ApexDesktop.exe`. Samodzielny runtime .NET jest w EXE. Panel działa wyłącznie na `127.0.0.1`; zwykły start nie łączy SSH ani brokera i nie wymaga hasła panelu.
2. Jeśli istnieje już poprawny profil kontrolera w prywatnym katalogu danych, zachowaj go i pomiń parowanie. Nie kopiuj klucza prywatnego z VPS ani z innego komputera.
3. Przy pierwszej konfiguracji zamknij uruchomiony EXE i wybierz `Paruj-kontroler.cmd`. To jawna jednorazowa konfiguracja natywna — bez sieci i handlu. Nie wpisuj żadnych danych API OKX. Wymaga istniejącego systemowego klienta OpenSSH (`ssh.exe`, `ssh-keygen.exe`); aplikacja niczego nie instaluje.
4. Podaj otrzymane od administratora VPS: dokładny host, port, użytkownika oraz pełny publiczny klucz hosta `ssh-ed25519 BASE64`. Porównaj wyświetlony odcisk SHA256 przez niezależny zaufany kanał. W razie braku potwierdzenia wybierz Anuluj. Nie używamy automatycznego ssh-keyscan ani ślepego TOFU.
5. Kreator tworzy NOWĄ lokalną tożsamość SSH z prywatnymi ACL bieżącego użytkownika. Do administratora przekaż WYŁĄCZNIE `%LOCALAPPDATA%\APEX Trading AI\controller-public-key.txt` (jeśli używasz innego katalogu `--data` lub `APEX_DATA_DIR`, plik jest właśnie w nim). Nie wysyłaj `controller-ssh\identity`.
6. Administrator musi zainstalować wyeksportowany klucz PUBLICZNY dla istniejącego ograniczonego relay VPS. Ta paczka nie paruje ani nie wdraża VPS. Nie zawiera prywatnego klucza użytkownika, danych brokera ani gotowego profilu endpointu.
7. Uruchom EXE ponownie. Osobno wybierz: „Połącz kanał VPS” → „Połącz OKX DEMO na VPS” → PREPARE → ARM → START. Żaden krok nie wykonuje się automatycznie. ARM nie oznacza START.

## Bezpieczeństwo i stan
- Klucz prywatny SSH jest plikiem chronionym natywnymi ACL, wymaganym przez systemowy OpenSSH; nie twierdzimy, że ten plik jest szyfrowany DPAPI. Obecny profil dostępu prezentacji nadal używa CurrentUser DPAPI. Chronisz całe konto Windows; lokalne procesy/użytkownicy z dostępem do loopback są zaufani zgodnie z dotychczasową polityką.
- Częściowy profil / `controller-pairing.pending`, obce pliki, złe ACL lub uszkodzone metadane są odrzucane bez naprawy i resetowania. Zachowaj dane do sprawdzenia; nie usuwaj plików, aby wymusić ponowienie.
- Zamknięcie kontrolera po potwierdzonym START NIE wysyła STOP do VPS. Aby zatrzymać silnik, wyślij STOP i sprawdź potwierdzenie serwera. STOP nie oznacza sprzedaży pozycji.
- Utracone potwierdzenie polecenia oznacza stan nieznany / dane nieaktualne, nie sukces ani potwierdzony STOP. Nie ponawiaj mutacji w ciemno; połącz nową sesję i odczytaj stan VPS.
- Brak broker-obserwacji to niewiadoma, a nie saldo zero. cTrader/MT5 pozostają zachowane, lecz nie są celem tego kandydata.

## Zakres odbioru
Kandydat służy do natywnego CI z jednorazowym syntetycznym SSH 127.0.0.1. Nie dowodzi instalacji na Windows 11, autentycznego endpointu VPS, prywatnego OKX ani gotowości do rzeczywistego handlu. Wymaga niezależnego odbioru przed przekazaniem użytkownikowi jako finalna wersja.
