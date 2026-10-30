# APEX Trading AI DEMO — instalacja Windows 11

> **Aktualizacja Naval / Task8A:** poniższy Windows autonomy jest dowodem historycznym, nie świeżym wydaniem Naval. Lokalny nowy ZIP Task8A ma target `linux-x64`; nie uruchomi cTrader Desktop. Nowy Windows Naval wymaga osobno zatwierdzonego natywnego CI i odbioru ACL/przeglądarki. Porównaj rzeczywisty SHA dostarczonego ZIP, nie nazwę ani stare wyniki. [Linux/VPS](NAVAL_DELIVERY_VPS.md).

## Gotowy pakiet — bez narzędzi programistycznych

1. Pobierz `APEX Windows DEMO.zip` i odpowiadający mu plik `.zip.sha256` z artefaktu GitHub Actions **APEX Windows DEMO** dla gałęzi `apex-demo/autonomy`. Porównaj `sourceCommit` w pakiecie z SHA przekazanego przebiegu. Stary pakiet `926afaa` jest nieaktualny dla autonomii; nie używaj go zamiast nowego artefaktu.
2. Rozpakuj **cały ZIP** do nowego folderu, np. `C:\APEX DEMO`. Nie uruchamiaj pliku bezpośrednio wewnątrz ZIP i nie kopiuj samego EXE: folder `wwwroot` jest potrzebny do panelu.
3. Opcjonalna weryfikacja integralności: kliknij prawym przyciskiem Start → Terminal/PowerShell i wykonaj `powershell -ExecutionPolicy Bypass -File "C:\APEX DEMO\Sprawdz-Pakiet.ps1"`. Oczekiwane: `PAKIET OK`. SHA-256 nie zastępuje podpisu wydawcy — porównaj sumę ZIP z otrzymaną z zaufanego źródła.
4. Dwuklik `Uruchom APEX.cmd`. Aplikacja jest samodzielna: **nie instalujesz .NET SDK, Node, Pythona ani Visual Studio**.
5. Panel otwiera się w Edge albo domyślnej przeglądarce. Jeżeli okno nie otworzy się automatycznie, wejdź na `http://127.0.0.1:5077`.
6. Pozostaw proces APEX uruchomiony; zamknięcie samej karty panelu nie jest poleceniem STOP. Użyj przycisku STOP lub Emergency Stop w panelu.

Windows 11 x64 oraz lokalny Pepperstone cTrader Desktop z kontem **DEMO** są wymagane. Program nie jest podpisany cyfrowo; nie omijaj SmartScreen dla plików z nieznanego źródła. Nie udostępniaj portów APEX w routerze ani w zaporze.

## Zawartość

- `APEX-Desktop\ApexDesktop.exe` i `wwwroot\` — silnik, panel i serwer lokalny.
- `cTrader\ApexBridge.algo` — zalecany most do pełnego panelu APEX.
- `cTrader\ApexTradingAI.algo` — Native produkcyjnie tylko odczyt/redukcja; START/TEST nowych wejść odmawiają. Nie jest zamiennikiem głównej ścieżki. **Nie uruchamiaj obu na tym samym koncie**.
- `MT5-opcjonalny\ApexBridge.mq5` — źródło dodatku MT5, wymaga MetaEditora; nie jest potrzebne do cTrader.
- `Dokumentacja\` — instrukcje i ograniczenia; `Weryfikacja\bot.trx`, `platform.trx`, `backtester.trx` — rzeczywiste wyniki bramek produktu.
- `apex-build.json`, `manifest.sha256`, `Sprawdz-Pakiet.ps1` — pochodzenie i integralność plików. `sourceDirty: true` oznacza lokalne modyfikacje względem wskazanego commita.

Nie ma pakietu AI Lab, uczenia modeli ani automatycznych aktualizacji.

## Opcjonalny Naval (ręczna czynność administratora)

Paczka nie zawiera kont, kluczy OKX, konfiguracji ani poświadczeń. NavalRead domyślnie off. Prywatne pliki i dokładny schemat opisuje [NAVAL_READ_PUBLICATION.md](NAVAL_READ_PUBLICATION.md). Operator loguje się natywnym Basic najpierw na `/`, dopiero potem `/naval/`; ReadOnly Bearer uprawnia tylko do dwóch GET API, nie do UI. Nie wklejaj sekretów do URL, JS, chatu lub argumentów. Awaryjny stary panel pozostaje dostępny dla Operatora również przy H503.

## Podłączenie konta

Przejdź do [PODLACZENIE_PLATFORM.md](PODLACZENIE_PLATFORM.md), a następnie [PROCEDURA_DEMO_PEPPERSTONE.md](PROCEDURA_DEMO_PEPPERSTONE.md). Start aplikacji i mostu nie jest zgodą na handel. Read-only setup wymaga dedykowanego pustego DEMO hedging, rezerwuje ID/etykietę i wymaga skopiowania dokładnej etykiety do mostu oraz ponownego parowania. CoverageUnknown nie jest certyfikatem całej historii. Test wykonania wysyła rzeczywistą transakcję DEMO i wymaga osobnej zgody. Dopiero trwały PASS + świeże uzgodnienie pozwala zatwierdzić START; restart/reconnect pozostawia STOP.

Aktywny profil: tylko **IndexDipBuy / indeksy / Experimental**, long-only (D1, SMA100, RSI2<5, wyjście SMA5 lub 10 barów, SL3ATR i ochronny TP3R). Testy nie dowodzą rentowności: wszystkie 64 autentyczne próby badawcze zatrzymał preflight, bez wyników finansowych. Limity pozostają 0,5% / 1,5% / DD5% / x5, obowiązkowe SL/TP, Emergency i pełne close. Nie ma odblokowania REAL.

Dane i logi znajdują się w `%LOCALAPPDATA%\APEX Trading AI\`. Nie usuwaj tego katalogu podczas aktualizacji: przechowuje rejestr sygnałów, limity i plany pozycji. Nie przesyłaj `apex.config.json`, tokenu ani klucza parowania do publicznego GitHub.

## Budowanie ze źródeł — tylko dla wykonawcy

Gotowy pakiet nie wymaga tych czynności. Do budowy potrzebne są Git, .NET 8 SDK, przetestowany Node.js 26 / npm11 (Vite wymaga co najmniej 20.19 lub 22.12, samo „22+” nie jest wystarczające) i dostęp do NuGet/npm:

```powershell
winget install Microsoft.DotNet.SDK.8
# Przygotuj Node26 / npm11; bez Node na maszynie odbiorcy.
powershell -ExecutionPolicy Bypass -File tools\windows\Zbuduj-APEX.ps1 -Wyjscie "C:\APEX-build\wersja-1"
```

Skrypt buduje pełne `APEX.Trading.Demo.sln` w Release, uruchamia testy bota, platformy, backtestera, fixtures bramki pakietu i panelu, publikuje samodzielny EXE i oba zgodne cBoty, sprawdza manifest oraz tworzy ZIP. Nie pozwala pominąć testów ani nadpisać niepustego katalogu. Przy ponownej budowie wybierz nowy folder. MT5 nie jest kompilowany i nie blokuje pakietu cTrader. Archiwalne testy AI Lab uruchamia się oddzielnie, nie dołączając modułu do aplikacji.

GitHub → Actions → **APEX Windows DEMO** → Run workflow na właściwej gałęzi automatyzuje budowę na `windows-2022`, w tym Windows PowerShell 5.1 i test uruchomienia prawdziwego EXE. Zielony workflow nie dowodzi połączenia z brokerem.

## Aktualizacja i problemy

- Aktualizuj ręcznie do **nowego folderu**, po zatrzymaniu handlu i sprawdzeniu pozycji. Dane konta zostają w LocalAppData. Zainstaluj nowy most i uruchom jedną instancję.
- Brak panelu: sprawdź proces APEX i port 5077; zajęty port wymaga świadomej zmiany `HttpPort` przy zamkniętej aplikacji i zgodnej zmiany adresu mostu.
- Druga instancja APEX na tym samym katalogu danych jest blokowana.
- Brak połączenia: APEX i cTrader muszą być na **tym samym Windows**. cTrader Cloud ani sam VPS nie wystarczą.
- Brak sygnałów może być poprawnym zachowaniem strategii; nie luzuj limitów, aby wymusić transakcję. Do kontrolowanego sprawdzenia służy zatwierdzany test wykonania.
- Błąd kalendarza, prowizji albo specyfikacji: sprawdź diagnostykę i nie wyłączaj zabezpieczeń w ciemno.
