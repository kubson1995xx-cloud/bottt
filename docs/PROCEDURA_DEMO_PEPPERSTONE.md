# Procedura uruchomienia na koncie DEMO (Pepperstone, cTrader)

Cel: sprawdzić na prawdziwym serwerze brokera (środki demonstracyjne) ścieżkę zleceń, zabezpieczenia i zachowanie bota, zanim w ogóle rozważysz konto rzeczywiste.

> W tej procedurze **nie ma żadnego kroku na koncie LIVE**. APEX domyślnie blokuje LIVE na trzech poziomach: most, strażnik konta i globalny wyłącznik w pliku konfiguracji.

## 0. Konto demo

1. Na stronie Pepperstone otwórz konto **demo cTrader** (np. Razor – prowizja za lot, niski spread; Standard – koszt w spreadzie). Zanotuj numer konta i walutę.
2. Zaloguj się w cTrader Desktop na to konto. W nagłówku cTrader musi być oznaczenie **Demo**.

## 1. Uruchomienie

1. Uruchom APEX Desktop ([`INSTALACJA_WINDOWS.md`](INSTALACJA_WINDOWS.md)).
2. Podłącz most cTrader ([`PODLACZENIE_PLATFORM.md`](PODLACZENIE_PLATFORM.md)).
3. W panelu, na **Pulpicie**, sprawdź:
   - znacznik **„Konto demonstracyjne”** (zielony) i numer konta zgodny z cTrader,
   - brak czerwonego komunikatu „Handel zablokowany przez strażnika konta”,
   - tryb: **PRACA** (konto DEMO startuje automatycznie; LIVE nigdy).
4. **Instrumenty** → lista „Instrumenty u brokera”:
   - kryptowaluty mają powód „kryptowaluta – trwale wykluczona”,
   - skanowane są tylko klasy, dla których profil ma strategię (domyślnie indeksy – strategia eksperymentalna DEMO).
5. **Strategie**: tylko „Kupno spadków / Indeksy” ma status **EKSPERYMENTALNA – TYLKO DEMO**, reszta **ODRZUCONA PRZEZ TESTY**.

## 2. Test wykonania (obowiązkowy)

1. **Diagnostyka i dziennik** → **Test wykonania (DEMO)** → **Uruchom test** → potwierdź.
2. Bot otworzy pozycję o **minimalnym wolumenie** z SL i TP, po ~20 s przesunie SL, po kolejnych ~20 s zamknie pozycję. Każdy krok jest weryfikowany na podstawie stanu u brokera.
3. Oczekiwany wynik (ok. 1 min): **„TEST ZALICZONY – otwarcie, zmiana SL i zamknięcie potwierdzone u brokera”**.
4. W cTrader → History sprawdź pozycję z etykietą `APEX` i komentarzem `APEX|XT|…|v1`.
5. Jeśli test nie przejdzie: zapisz komunikaty z panelu (Komunikaty mostu, Zdarzenia konta) i log `%LOCALAPPDATA%\APEX Trading AI\logi`. **Nie przechodź dalej.**

Na koncie LIVE przycisk testu jest nieaktywny, a silnik dodatkowo odmawia jego wykonania.

## 3. Testy zabezpieczeń (zalecane, ~15 min)

| Test | Jak | Oczekiwany wynik |
|---|---|---|
| Pozycja ręczna | Otwórz ręcznie małą pozycję w cTrader (bez etykiety APEX). Użyj **Zamknij pozycje bota**. | Pozycja ręczna pozostaje otwarta i nietknięta. |
| Rozłączenie | Zatrzymaj instancję ApexBridge w cTrader na 1 min, potem uruchom. | Panel: „Most rozłączony” → po starcie ponowne połączenie i uzgodnienie stanu, bez duplikatów. |
| Restart APEX | Zamknij i uruchom APEX Desktop. | Stan wczytany z dysku; pozycje bota przejęte; brak ponownego wysłania tych samych sygnałów. |
| Awaryjny stop | **ZATRZYMAJ AWARYJNIE** → potwierdź. | Zamknięte wyłącznie pozycje bota; tryb AWARYJNE ZATRZYMANIE; START zablokowany do „Resetuj awarię”. |
| Duplikat mostu | Uruchom drugą instancję ApexBridge na tym samym koncie. | Druga instancja odrzucona: „to konto jest już połączone przez inny most”. |

## 4. Obserwacja (tygodnie–miesiące)

1. Zostaw bota w trybie **PRACA** na DEMO. Komputer i cTrader muszą działać w godzinach sesji.
2. **AI Decision Center** pokazuje na żywo, co silnik ocenia i dlaczego czeka (WAIT), kupuje (BUY) lub sprzedaje (SELL).
3. Co tydzień:
   - **Pozycje i wyniki** → **Pobierz dziennik CSV** (`transakcje.csv`) i porównaj z raportem badań: średni wynik w R, odsetek zysków, czas trwania;
   - sprawdź poślizgi (cena wejścia względem sygnału) i komunikaty błędów;
   - sprawdź, czy monitor wyników nie wstrzymał strategii (karta „Strategie wstrzymane przez monitor wyników”).
4. Kryterium wiarygodności: co najmniej 3–6 miesięcy i kilkadziesiąt transakcji. Przy ~9 transakcjach rocznie w backteście potrwa to długo. To uczciwa konsekwencja braku silnej przewagi (patrz [`RAPORT_BADAN.md`](../reports/RAPORT_BADAN.md)).

## 5. Konto LIVE – poza tą procedurą

Przejście na konto rzeczywiste to **osobna, świadoma decyzja użytkownika**. Wymaga ręcznej edycji pliku konfiguracji, odblokowania konkretnego konta frazą w panelu, włączenia parametru LIVE w moście i ręcznego naciśnięcia START.
Szczegóły i ostrzeżenia: [`KONFIGURACJA.md`](KONFIGURACJA.md) → „Konto LIVE”. Strategia eksperymentalna **nigdy** nie działa na LIVE. Przy obecnym profilu bot na LIVE nie otworzyłby żadnej pozycji, bo nie ma strategii o statusie `Validated`.
