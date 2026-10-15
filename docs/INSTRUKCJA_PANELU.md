# Instrukcja panelu APEX Desktop

Panel otwiera się pod adresem `http://127.0.0.1:5077` (okno Edge w trybie aplikacji). Działa tylko lokalnie, a każde żądanie wymaga tokenu wstrzykniętego w stronę.
Motyw: przełącznik **Ciemny / Jasny** w lewym dolnym rogu. Pasek u góry przełącza konta, gdy podłączonych jest kilka.

Wszystkie polecenia zmieniające stan (zatrzymanie, zamknięcie pozycji, limity, LIVE) wymagają potwierdzenia w oknie dialogowym.

## Pulpit

- **Karta główna:** znacznik konta (**Konto demonstracyjne** / **Konto rzeczywiste**), tryb pracy, kapitał, wynik dnia, saldo.
- **Przyciski:**

| Przycisk | Działanie |
|---|---|
| START | Uruchamia nowe wejścia (niedostępny, gdy strażnik konta blokuje handel). |
| Wstrzymaj nowe | Brak nowych wejść; otwarte pozycje są dalej zarządzane (trailing, BE, wyjścia). |
| Zatrzymaj | Bot nie otwiera i nie zarządza pozycjami; SL/TP pozostają u brokera. |
| Zamknij pozycje bota | Zamyka wszystkie pozycje z etykietą APEX na tym koncie (ręczne i innych botów – nie). |
| ZATRZYMAJ AWARYJNIE | Zamyka pozycje bota i blokuje handel do **Resetuj awarię** (po resecie bot jest ZATRZYMANY – START trzeba nacisnąć osobno). |

- **Wykorzystanie limitów ryzyka:** dzienna strata, obsunięcie, liczba pozycji, dźwignia efektywna – na tle limitów.
- **Kafelki:** wynik dziś / łączny, transakcje, skanowane instrumenty wg klas.
- **Kapitał w czasie** (próbki co 5 min), **Otwarte pozycje bota**, **Ostatnie sygnały** (ranking i decyzje), **Ważne wiadomości** (kalendarz makro).
- Komunikaty ostrzegawcze: most rozłączony, dzienny limit straty, maksymalne obsunięcie, blokada strażnika konta.

## AI Decision Center

Wizualizacja **tylko do odczytu**. Pokazuje rzeczywiste zdarzenia silnika: oceny strategii, filtry, decyzje RiskEngine i wykonanie.
Nie wysyła żadnych poleceń i nie zmienia decyzji. Test automatyczny potwierdza, że silnik z wizualizacją i bez niej wysyła identyczne zlecenia.
Panel nie generuje sygnałów ani „pewności” – pokazuje to, co silnik faktycznie policzył.

- **Sieć modułów analitycznych:** Dane rynkowe → Wskaźniki → Reżim rynku → 6 strategii → Ranking i filtry → RiskEngine → BUY / SELL / WAIT. Pulsy na krawędziach to nowe zdarzenia od chwili otwarcia widoku: dochodzą do etapu, na którym zakończyła się ocena, w kolorze wyniku. Najechanie na moduł pokazuje jego liczniki.
- **KPI:** oceny od startu silnika, sygnały strategii, odrzucone przez filtry, zablokowane przez RiskEngine, wykonane BUY/SELL.
- **Skaner rynku:** kafelki skanowanych instrumentów z ceną, zmianą 24h, mini-wykresem (48 świec H1), reżimem i ostatnią decyzją. Filtry: Wszystkie / Sygnały / BUY / SELL / Z pozycją, wyszukiwarka.
- **Analiza instrumentu** (po kliknięciu kafelka): wykres świecowy H1/H4/D1 z EMA50/EMA200 i znacznikami wejść, wskaźniki reżimu (siła trendu ADX z progami 20/25, kierunek EMA, efektywność ruchu ER, percentyl zmienności ATR).
- **Ocena strategii:** dla każdej z 6 strategii status (zweryfikowana / eksperymentalna – tylko DEMO / odrzucona przez testy / wyłączona przez użytkownika), klasy aktywów i wyniki ocen (brak sygnału, pominięte, odrzucone, zablokowane, wykonane). Strategie odrzucone przez testy nie są oceniane.
- **Ocena okazji (ranking):** rozkład oceny 0–100 na składniki z rzeczywistymi wagami i progiem minimalnym. **Ocena to ranking jakości, nie prawdopodobieństwo zysku.**
- **Uzasadnienie decyzji:** dziennik ocen z listą bramek (dane, reżim, strategia, filtry, ranking, RiskEngine, wykonanie), z wynikiem każdej i powodem decyzji WAIT/BUY/SELL.

## AI & Auto-Updates

Panel modułu APEX AI Lab (osobny proces; szczegóły: [`AI_AUTO_UPDATES.md`](AI_AUTO_UPDATES.md)). Gdy moduł nie działa – przycisk **Uruchom moduł AI**.

- **Nagłówek:** stan modułu i automatyzacji, następna zaplanowana analiza, **Analizuj teraz**, **Uruchom agenta teraz** (z wyborem dostawcy), przełącznik **Automatyzacja**.
- **Przegląd i postęp:** etapy zadania (Analiza → Izolowana kopia → Wersja bazowa → Agent AI → Audyt → Kompilacja → Testy → Backtest → Przegląd) z bieżącym stanem, dziennik pracy agenta na żywo (polecenia, zmienione pliki, plan, komunikaty, zużycie), ostatnia analiza, zużycie limitów, ostatnie propozycje.
- **Propozycje i historia zmian:** lista zadań ze statusem; szczegóły propozycji – podsumowanie agenta, audyt, kompilacja, testy (vs wersja bazowa), porównanie backtestu, przegląd drugim modelem, pełny diff. Przyciski: **Zatwierdź i przygotuj wdrożenie** (fraza dla zmian chronionych / niepełnej weryfikacji), **Odrzuć**, **Eksportuj łatkę**, **Dodaj gałąź do repozytorium źródłowego**, **Przerwij**.
- **Połączenia AI:** wymagania (Git, .NET 8 SDK, Node.js), role dostawców (główny, zapasowy, przegląd), karty: Codex (logowanie kontem ChatGPT lub kodem urządzenia, opcjonalnie klucz API), Claude Code (subskrypcja Claude lub Anthropic Console), OpenAI API i Anthropic API (klucz, model, ceny, długość odpowiedzi, rundy poprawek).
- **Harmonogram i limity:** częstotliwość analiz, wyzwalacze, minimalny odstęp, migawki rynku; limity uruchomień, tokenów i kosztów; bieżące zużycie i wykres tokenów z 14 dni.
- **Repozytorium i wydania:** repozytorium źródłowe, gałąź bazowa, dane do backtestu, limity czasu, autostart AI Lab; zasady bezpieczeństwa (tylko podgląd); lista wydań i **Przywróć poprzednią wersję**.

## Pozycje i wyniki

Otwarte pozycje bota (wejście, SL, TP, wynik, strategia), ostatnie zamknięte transakcje (wynik netto, R, powód wyjścia), statystyki (win rate, profit factor, expectancy w R, obsunięcie, Sharpe, średni zysk/strata).
**Pobierz dziennik CSV** – pełny dziennik transakcji (`transakcje.csv`).

## Strategie

Lista par strategia × klasa aktywów z profilu, status walidacji i dowody z backtestu. Przełącznik **Włącz/wyłącz nowe wejścia** działa tylko dla strategii nieodrzuconych; strategii odrzuconej przez testy nie da się włączyć.
**Strategie wstrzymane przez monitor wyników:** strategia, która na żywo wyraźnie odbiega od backtestu, jest automatycznie wstrzymywana. **Przywróć wstrzymane** – świadome wznowienie.

## Instrumenty

- **Kategorie instrumentów:** włączanie i wyłączanie klas aktywów. Kryptowaluty są trwale wykluczone.
- **Instrumenty u brokera:** wszystkie symbole z platformy z klasyfikacją i powodem wyboru lub odrzucenia. **Tryb wyboru:** Automatyczny (zalecany – płynne instrumenty z limitami na klasę) albo Ręczny (zaznacz instrumenty → **Zapisz wybór**). Nawet w trybie ręcznym kryptowaluty i instrumenty nierozpoznane są odrzucane.

## Ryzyko i bezpieczeństwo

- **Limity ryzyka konta:** ryzyko na transakcję (domyślnie 0,5%, maks. 2%), maks. dzienna strata (1,5%), maks. obsunięcie (5%), maks. liczba pozycji (3), maks. dźwignia efektywna (5). Zapis jest walidowany, a wartości absurdalne są odrzucane.
- **Tryb konta i ochrona LIVE:** stan strażnika konta. Dla konta LIVE: **Odblokuj konto LIVE…** (działa tylko po świadomej zmianie pliku konfiguracji; wymaga wpisania frazy `LIVE <numer> AKCEPTUJE RYZYKO`) i **Zablokuj konto LIVE**.

## Połączenia platform

- **Połącz platformę** – kreator: cTrader / MetaTrader 5 / Inna platforma, z adresami (WebSocket, TCP), kluczem parowania (**Kopiuj**) i krokami.
- **Nowy klucz parowania** – unieważnia stary klucz dla nowych połączeń.
- **Połączone konta:** platforma, transport, symbole, notowania, ostatni komunikat.
- **Prowizja konta MetaTrader 5** – wymagana dla MT5 (bez niej handel na koncie MT5 jest zablokowany).
- **Odrzucone połączenia** – ostatnie próby z błędnym kluczem, duplikaty itp.

## Diagnostyka i dziennik

- **Test wykonania (DEMO)** – test otwarcia, zmiany SL i zamknięcia na minimalnym wolumenie (niedostępny na LIVE).
- **Zdarzenia konta**, **Komunikaty mostu**, **Komunikaty i błędy**, **Dziennik aplikacji** (z lokalizacją katalogu danych).

## Pliki na dysku (`%LOCALAPPDATA%\APEX Trading AI\`)

| Plik | Zawartość |
|---|---|
| `apex.config.json` | konfiguracja (porty, klucz, token, limity, LIVE) |
| `konta\<konto>\stan\` | trwały stan silnika (rejestr sygnałów, plany pozycji, limity dnia) – zapis atomowy z kopią `.bak` |
| `konta\<konto>\dziennik\transakcje.csv` | dziennik transakcji |
| `konta\<konto>\dziennik\sygnaly.log`, `zdarzenia.log` | sygnały i zdarzenia |
| `konta\<konto>\kapital.json` | próbki kapitału do wykresu |
| `logi\` | dziennik aplikacji |
