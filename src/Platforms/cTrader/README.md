# Integracja cTrader — ograniczenia i weryfikacja DEMO

## Zasady dostarczanych cBotów

- `ApexBridge` łączy się wyłącznie z lokalnym `ws://127.0.0.1:PORT/bridge` w cTrader **Desktop**. Nie używa OAuth ani Open API. Logowanie pozostaje w platformie brokera.
- Konto REAL jest odrzucane bezwarunkowo. Historyczny parametr `AllowLive` nie odblokowuje tej wersji. Zmiana numeru, brokera, waluty lub trybu konta unieważnia uprawnienie do wykonania poleceń.
- Etykieta jest **dokładna**, nie prefiksowa: `APEX` nie obejmuje `APEX-N` ani `APEX-other`. Etykieta hosta w `welcome` musi zgadzać się z parametrem instancji.
- Każde nowe otwarcie wymaga skończonych dodatnich SL **i TP**, właściwych jednostek i kroku wolumenu, świeżego ticka, otwartego rynku oraz zweryfikowanego kosztu. Oba zabezpieczenia przekazywane są w zleceniu. Brak potwierdzonego zabezpieczenia uruchamia próbę naprawy i, jeśli trzeba, awaryjne zamknięcie; wynik nie jest wówczas fałszywie zgłaszany jako zwykłe `filled`.
- Zmiana SL nie może go usunąć, przenieść na błędną stronę rynku ani zwiększyć dotychczasowego ryzyka. TP pozostaje zachowany. Częściowe zamknięcie nie może niejawnie stać się pełnym zamknięciem ani pozostawić wolumenu poniżej minimum.
- Odbiór WebSocket przekazuje obsługę na główny wątek cTrader. Callback starego transportu nie wykonuje polecenia po reconnect. Otwarcie oczekujące w kolejce głównego wątku ponad 5 s jest odrzucane; brak `welcome` przez 15 s powoduje reconnect. Konto/pozycje zapewniają heartbeat także między kolejnymi porcjami historii.
- Subskrypcja zastępuje poprzedni zestaw; stare handlery świec są odpinane. Wysyłane są wyłącznie zamknięte świece. Niepełna historia jest jawnie logowana.
- Notowania pochodzą z `MarketData.GetTicks(...).LastTick`, razem z czasem ticka brokera. Timer nie odmładza ceny. `Symbol.PipValue` jest według SDK stały od startu, dlatego aktualna wartość pipsa jest wyliczana z rozmiaru pipsa i `AssetConverter`.
- Kursy USD/waluty kwotowania do waluty konta i metadata subskrybowanych symboli są odświeżane. Brak konwersji blokuje instrument zamiast zakładać kurs 1. Typy prowizji zachowują znaczenie protokołu v1. Minimum prowizji SDK Asset/QuoteAsset jest przekazywane przez opcjonalne pola `minCommissionAccount` i `minCommissionKnown` i uwzględniane jako podłoga kosztu za stronę. Nieznany typ/konwersja blokuje instrument; override nie ukrywa podłogi. Aktualizować host i most razem.
- Rejestr poleceń zapisuje i `Flush`uje rezerwację przed mutacją brokera. Powtórny identyfikator odtwarza wynik; zmiana payloadu jest odrzucana. Otwarcie tego samego sygnału z nowym identyfikatorem po reconnect/restart nie wysyła drugiego zlecenia. Awaria zapisu lub nieznany wynik blokują ponowienie. Klucze LocalStorage spełniają ograniczenia cTrader (alfanumeryczne, maks. 50 znaków). Rejestru nie należy ręcznie usuwać bez uzgodnienia pozycji i historii.
- Rejestr jest ograniczony do 20 000 różnych poleceń i nie usuwa starych rezerwacji automatycznie. Uszkodzony, pełny lub niedostępny rejestr wymaga obsługi operatora; open/modify/partial-close nie są wykonywane bez trwałej rezerwacji. Pełne close konkretnego własnego ID pozostaje dostępne jako idempotentna redukcja ryzyka, z kontrolą DEMO i dokładnej etykiety; przy awarii rejestru audyt lokalny nie jest gwarantowany.
- Natywny `ApexTradingAI` używa tych samych reguł danych, walut i ochrony oraz trwałej deduplikacji otwarć. Nie uruchamiać równolegle wielu silników na tym samym koncie. Polityka START/STOP i limity portfela pochodzą ze wspólnego Core.

## Testy wykonywalne bez brokera

```text
dotnet test src/Platforms/cTrader/Tests -c Release
dotnet test tests/Apex.Tests -c Release
dotnet build src/Platforms/cTrader/ApexBridge/ApexBridge/ApexBridge.csproj -c Release
dotnet build src/Platforms/cTrader/ApexTradingAI/ApexTradingAI/ApexTradingAI.csproj -c Release
```

Projekt `Tests` wykonuje produkcyjny kod reguł, rejestru, mapowania prowizji i notowań. Dane testowe i interfejsy SDK są jawnie kontrolowane — to **nie** test serwera Pepperstone ani wykonania zlecenia w cTrader. `PlatformContractTests` w głównej suite to wyłącznie dodatkowe kontrole podłączenia reguł w źródłach.

## Bramka Windows 11 / Pepperstone cTrader DEMO

Przed dopuszczeniem transakcji wymagane są osobne dowody z prawdziwej platformy:

1. Instalacja obu plików `.algo`; działanie wybranego cBota lokalnie z `AccessRights.None`, brak wyjątków API/wątku i prawidłowy zapis/odczyt LocalStorage po restarcie. Nie uruchamiać obu wariantów handlowych naraz.
2. Porównanie numeru/typu/waluty konta, symboli, jednostek, kroków/minimum wolumenu, godzin sesji, przeliczników i prowizji z rzeczywistą specyfikacją Pepperstone DEMO. Szczególnie sprawdzić `MinCommission`, jego walutę i odpowiadającą podłogę kosztu w walucie konta; testy jednostkowe nie dowodzą poprawności metadanych brokera.
3. Pełna historia wybranych interwałów, brak świecy otwartej, zgodne czasy UTC i granice H4/D1; niezmienny czas starego ticka przy braku nowych notowań, zamknięta sesja i awaria danych.
4. Restart Desktop/cBota, zerwanie transportu, powrót subskrypcji i uzgodnienie istniejących pozycji; brak opóźnionego otwarcia, duplikatu lub automatycznego START. Pozostawienie pozycji ręcznych i innego bota nietkniętych.
5. **Wyłącznie po odrębnej zgodzie użytkownika**: kontrolowane otwarcie DEMO w limicie ryzyka, potwierdzenie obu SL/TP, zmiana SL z zachowaniem TP, częściowe/pełne zamknięcie zgodne z jednostkami i historią, Emergency Stop i ponowne uruchomienie bez duplikatu. Zarejestrować rzeczywiste identyfikatory pozycji i historię; kompilacja lub symulator nie zastępują tego testu.

Kompilacja na Linuksie dowodzi zgodności z zainstalowanym SDK, nie uruchomienia na Windows ani poprawnej konfiguracji brokera.

## MT5 (opcjonalny, bez parytetu dowodów)

Most MT5 zachowuje osobny transport TCP i nie jest zależnością cTrader. REAL jest zawsze blokowane; wymagane SL/TP, dokładna etykieta otwarcia i kontrola wolumenu są zaostrzone. Nieznana prowizja nadal wymaga ustawienia po stronie Desktop. Konta nie-USD są świadomie zablokowane do weryfikacji pełnej konwersji walutowej. Kod MQL5 wymaga kompilacji w MetaEditorze i osobnego testu DEMO; nie ma tu dowodu wykonania ani trwałej deduplikacji poleceń równoważnej cTrader.

Źródło ograniczeń LocalStorage: [oficjalna dokumentacja SetString](https://help.ctrader.com/ctrader-algo/references/Localstorage/LocalStorage/SetString/). Kontrakty `Tick.Time`, `Ticks.LastTick`, `Symbol.PipValue`, `AssetConverter` i ochrony pozycji sprawdzono także w XML z zainstalowanego pakietu `cTrader.Automate 1.0.21`.
