# Instalacja APEX Trading AI na Windows 11

## Co jest w pakiecie

| Element | Do czego służy |
|---|---|
| **APEX Desktop** (`ApexDesktop.exe`) | Samodzielna aplikacja: silnik bota, zarządzanie ryzykiem, panel premium w przeglądarce, serwer mostów platform. |
| **Most cTrader** (`ApexBridge.algo`) | cBot łączący cTrader Desktop z APEX Desktop (dane rynkowe, konto, wykonywanie zleceń). |
| **Most MetaTrader 5** (`ApexBridge.mq5`) | Expert Advisor łączący MT5 z APEX Desktop. |
| **APEX natywny dla cTrader** (`ApexTradingAI.algo`) | Alternatywa „wszystko w jednym” – cały silnik w cBocie, bez APEX Desktop (panel na wykresie). |
| **APEX AI Lab** (`ApexAiLab.exe`) | Opcjonalny, osobny moduł AI & Auto-Updates (analizy, agenci Codex/Claude, wydania po akceptacji) – patrz [`AI_AUTO_UPDATES.md`](AI_AUTO_UPDATES.md). |

Zalecany sposób pracy: **APEX Desktop + most cTrader** (pełny panel, kilka kont i platform naraz).

## Wymagania

- Windows 11 (lub 10) 64-bit, przeglądarka Microsoft Edge (wbudowana).
- cTrader Desktop (np. Pepperstone) z kontem **DEMO** i/lub MetaTrader 5.
- Do zbudowania pakietu ze źródeł: **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`). Zbudowany `ApexDesktop.exe` nie wymaga instalowania .NET.

## Krok 1 – zbudowanie pakietu (jednorazowo)

1. Pobierz repozytorium (np. `git clone` lub ZIP z GitHub) do folderu, np. `C:\APEX\zrodla`.
2. Otwórz PowerShell w tym folderze i uruchom:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\windows\Zbuduj-APEX.ps1
   ```

   Skrypt uruchamia testy automatyczne (120 testów, ok. 1–2 min). **Jeśli którykolwiek test nie przejdzie, pakiet nie zostanie zbudowany.**
3. Wynik: folder `dist\` z plikami `Uruchom APEX.cmd`, `APEX-Desktop\`, `APEX-AiLab\`, `cTrader\`, `MT5\`, `Dokumentacja\`.
   Skrót `Uruchom APEX.cmd` uruchamia wydanie zatwierdzone w AI Lab (jeśli istnieje), w przeciwnym razie instalację z pakietu.

Opcje: `-Wyjscie C:\APEX` (inny folder docelowy), `-PominTesty` (niezalecane).

## Krok 2 – pierwsze uruchomienie APEX Desktop

1. Uruchom `dist\Uruchom APEX.cmd` (albo `APEX-Desktop\ApexDesktop.exe`).
2. Windows SmartScreen może pokazać ostrzeżenie „Nieznany wydawca” – plik nie jest podpisany certyfikatem. Kliknij **Więcej informacji → Uruchom mimo to** tylko wtedy, gdy budowałeś pakiet sam z tego repozytorium.
3. Otworzy się okno panelu (Edge w trybie aplikacji) pod adresem `http://127.0.0.1:5077`.
   - Panel i mosty nasłuchują **wyłącznie na 127.0.0.1** – nie są dostępne z sieci, więc zapora Windows nie powinna pytać o zgodę.
4. Dane aplikacji: `%LOCALAPPDATA%\APEX Trading AI\` (konfiguracja `apex.config.json`, stan kont, dziennik transakcji, logi).
5. Druga kopia APEX Desktop na tym samym katalogu danych nie uruchomi się (ochrona przed podwójnym zarządzaniem kontem).

## Krok 3 – podłączenie platformy

Instrukcja: [`PODLACZENIE_PLATFORM.md`](PODLACZENIE_PLATFORM.md). W skrócie dla cTrader:

1. Dwuklik na `dist\cTrader\ApexBridge.algo` → cTrader zainstaluje cBota „ApexBridge”.
2. W panelu APEX: **Połączenia platform** → skopiuj **Klucz parowania**.
3. W cTrader (konto DEMO): dodaj instancję ApexBridge na dowolnym wykresie, wklej klucz, **Start**.
4. Konto pojawi się w APEX w ciągu kilku sekund.

## Krok 4 – procedura DEMO

Postępuj według [`PROCEDURA_DEMO_PEPPERSTONE.md`](PROCEDURA_DEMO_PEPPERSTONE.md): test wykonania, weryfikacja zabezpieczeń, obserwacja.

## Autostart (opcjonalnie)

Aby APEX Desktop uruchamiał się z Windows: `Win+R` → `shell:startup` → wklej skrót do `Uruchom APEX.cmd`.
cTrader musi być uruchomiony i zalogowany, a instancja ApexBridge uruchomiona. Po każdym restarcie komputera sprawdź w cTrader (Automate → cBots), czy instancja działa. Jeśli nie, uruchom ją przyciskiem Start. Most sam łączy się ponownie co 5 s, gdy APEX Desktop jest niedostępny.

## Aktualizacja

1. Zamknij APEX Desktop. Bot nie musi zamykać pozycji, bo SL i TP są u brokera.
2. Pobierz nową wersję źródeł i ponownie uruchom `Zbuduj-APEX.ps1`.
3. Uruchom APEX Desktop. Stan (rejestr sygnałów, plany pozycji, limity dzienne) jest wczytywany z dysku, a pozycje są uzgadniane z brokerem.
4. W cTrader zainstaluj nowy `ApexBridge.algo` (dwuklik) i zrestartuj instancję.

## Odinstalowanie

Usuń folder `dist\` oraz (opcjonalnie) `%LOCALAPPDATA%\APEX Trading AI\` – to usuwa także dziennik transakcji i konfigurację. W cTrader usuń cBoty ApexBridge/ApexTradingAI.

## Rozwiązywanie problemów

| Objaw | Przyczyna / rozwiązanie |
|---|---|
| „APEX Desktop już działa” | Druga kopia jest uruchomiona (sprawdź zasobnik / Menedżer zadań). |
| Port 5077 zajęty | Zmień `HttpPort` w `apex.config.json` (i adres w moście cTrader). |
| Most cTrader: „nieprawidłowy klucz parowania” | Skopiuj klucz ponownie z panelu (przycisk Kopiuj). |
| Most cTrader: brak połączenia | APEX Desktop musi działać **na tym samym komputerze**; most nie działa w chmurze cTrader (Cloud). |
| „Brak informacji o koncie”, „Handel zablokowany” | Konto LIVE – zablokowane domyślnie (patrz [`KONFIGURACJA.md`](KONFIGURACJA.md)). |
| Ostrzeżenie o kalendarzu wiadomości | Brak dostępu do `nfs.faireconomy.media` – filtr wiadomości nieaktywny, działają filtry spreadu i zmienności. |
