# APEX Naval Command — Windows x64

Bieżący format kandydata: `APEX-NAVAL-COMMAND-Windows-x64.zip`, nie stary `APEX Windows DEMO.zip`. Task5 nie jest końcowym odbiorem. Rzeczywiste first-run/modal/default-shell/browser acceptance pozostają Task6, a finalna dostawa Task7. Hosted Windows Server nie zastępuje Windows 11 Pro, sprzętowego GPU ani cTrader użytkownika. Historyczny artefakt autonomy i osobne wydanie Linux nie są tym kandydatem.

## Odbiorca

Pełna instrukcja jest w korzeniu archiwum **PRZECZYTAJ.md** (źródło: [PRZECZYTAJ-WINDOWS](../tools/windows/PRZECZYTAJ-WINDOWS.md)). Porównaj SHA ZIP-a i `sourceCommit` z przekazanym przebiegiem CI. Rozpakuj **całość** do nowego folderu i uruchom `APEX-Desktop\ApexDesktop.exe`. Nie przenoś samego EXE. Samodzielny pakiet nie wymaga SDK, Node, Pythona, Visual Studio ani ręcznej instalacji .NET/VC++. Ostrzeżeń o niepodpisanych plikach nie omijaj w ciemno; nieznane źródło oznacza przerwanie.

Na pustym katalogu Windows profil `windows-local-presentation-v1` tworzy prywatny dostęp webowy (natywne okno → Basic w przeglądarce). `ApexDesktop.exe --show-local-access` pokazuje go ponownie przez CurrentUser DPAPI; nie uruchamia serwera i nie rotuje sekretów. Po bind aplikacja prosi domyślną przeglądarkę o lokalny adres `http://127.0.0.1:5077/?view=naval`. Po uwierzytelnieniu: `/naval/`; `/` pozostaje starym polskim panelem. `--no-browser` i `OpenBrowserOnStart=false` nadal działają.

Dane i backup: `%LOCALAPPDATA%\APEX Trading AI`, poza całym wydaniem. `--data` ma pierwszeństwo przed `APEX_DATA_DIR`. Istniejące konfiguracje/własne profile i Naval-off pozostają bez zmian. Błędy ACL/ścieżki lub przerwanej inicjalizacji nie uprawniają do kasowania danych, regenerowania map ani nadawania Everyone dostępu. Zachowaj komplet plików/ACL i zleć diagnozę. DPAPI wymaga tego samego konta Windows. Aktualizacja: finansowy STOP i weryfikacja pozycji → zakończenie własnego procesu → nowy folder produktu, zachowany prywatny katalog danych. Druga instancja lub zajęty HTTP odmawiają bez otwierania obcego portu. Zamknięcie karty/procesu nie jest finansowym STOP.

Nie wystawiaj portów poza `127.0.0.1`. Nowy profil ma zero kont runtime i statyczną syntetyczną/historyczną prezentację, nie rynek na żywo. `UNKNOWN`/`NOT_CONFIGURED` nie są gotowością brokera. Sześć rodzin strategii, dotychczasowy profil i limity 0,5% / 1,5% / DD5% / x5, SL/TP, Emergency, replay protection i trwały REAL-block są zachowane. Brak AI Lab, trenowania i updatera.

## Platformy — osobny zakres

`cTrader\ApexBridge.algo` jest lokalną ścieżką Desktop + most; `ApexTradingAI.algo` Native pozostaje tylko odczytem/redukcją. Nie uruchamiaj obu na jednym koncie. MT5 jest opcjonalnym źródłem do MetaEditora. [Podłączenie](PODLACZENIE_PLATFORM.md) i [procedura DEMO](PROCEDURA_DEMO_PEPPERSTONE.md) nie są pozwoleniem na handel. Prywatne konto/realna transakcja DEMO wymagają osobnej zgody; LIVE nie jest dostępne. Rentowność nieudowodniona.

## Wykonawca — build i dowody

Builder zachowuje interfejs:

```powershell
powershell -NoProfile -File tools\windows\Zbuduj-APEX.ps1 -Target win-x64 -Wyjscie 'C:\APEX-build\APEX-NAVAL-COMMAND-Windows-x64'
```

Wymaga natywnego Windows x64, SDK8 i Node26/npm z przypiętymi lockami; wybiera faktyczny SDK8 przez scratch `global.json`. Kolejność: czysty frontend/typecheck/test/build/staging → solution i cTrader SDK → rzeczywiste suites → root panel/helper controls → native `Gate=Source` → self-contained single-file publish bez trim/R2R → genuine presentation → dokładny verifier/manifest/ZIP → niezależna ekstrakcja i dotychczasowy gate produkcyjnego hosta. Nie ma flag pomijania testów. Wybierz nowy katalog, bez nadpisywania ZIP-a.

Manualny workflow **APEX Windows DEMO**, tryb `task5-package`, jest odrębnym engineering lane na `windows-latest`; domyślny `full-delivery` pozostaje osobny do Task7. Task5 uruchamia izolowany legacy-config smoke EXE: pusty runtime root, PATH bez dotnet tylko dla dziecka, rzeczywiste moduły/hashe bibliotek i ACL ekstrakcji, HTTP/panel/zero kont. Nie potwierdza natywnego modalu, autolaunch ani renderingu przeglądarki. `Weryfikacja/windows-publish.json` zawiera autentyczne runtimeconfig/packs/argv/provenance, a `windows.trx` rzeczywiste testy Source. `windowsRuntimeTested=false` i `brokerTested=false` pozostają w zapieczętowanym ZIP-ie; wyniki runtime są zewnętrzne i związane z tym samym SHA.
