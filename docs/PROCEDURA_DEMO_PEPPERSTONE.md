# Procedura odbioru: Pepperstone cTrader DEMO

**To test z prawdziwym serwerem brokera. Symulator i zielone testy .NET go nie zastępują. Tylko środki demonstracyjne.**

## 1. Połączenie bez handlu

1. Uruchom gotowy pakiet Windows i połącz lokalny `ApexBridge` zgodnie z [PODLACZENIE_PLATFORM.md](PODLACZENIE_PLATFORM.md).
2. Sprawdź w **obu programach** ten sam numer konta Pepperstone DEMO, walutę i saldo. Nie przesyłaj hasła, tokenów ani pliku konfiguracyjnego.
3. Poczekaj na świeże notowania, instrumenty i wymagane świece. Sprawdź pozycje oraz dziennik. Oczekiwany stan: **ZATRZYMANY**; nie naciskaj jeszcze START.
4. Nie uruchamiaj równolegle wersji natywnej, starego APEX ani drugiej instancji mostu. Zapewnij połączenie internetowe i wyłącz usypianie Windows na czas testu.
5. Zanotuj commit z `apex-build.json`, czas UTC i numer konta DEMO. Na VPS ten etap nie jest potwierdzony samym faktem zbudowania ZIP.

## 2. Zakres aplikacji — bez transakcji

1. Użyj osobnego konta DEMO **hedging**, bez innych pisarzy (ręcznego handlu, cBotów, Native, innych terminali). Wszystkie pozycje i zlecenia oczekujące muszą być puste przy setupie. Nie zamykaj cudzych pozycji ani nie kasuj starych plików dla wymuszenia gotowości.
2. W **Połączenia platform → Przygotuj zakres DEMO (read-only)** potwierdź wyłączne konto. To tylko trwała rezerwacja etykiety i ID, nie zgoda na zlecenie.
3. Skopiuj **dokładną wygenerowaną etykietę**, bez skracania/zmian, do parametru ApexBridge **„Dokładna etykieta pozycji APEX”**. Zatrzymaj i uruchom most ponownie, połącz z APEX.
4. Poczekaj na świeże **ScopedReady**. **Broker CoverageUnknown pozostaje prawidłowym ograniczeniem**: to uzgodnienie bieżącego zakresu aplikacji, nie całej historii brokera. Brakujące fill/deale, cudze obiekty, awaria zapisu albo konflikt blokują test/START; nie usuwaj dowodów.

## 3. Osobna zgoda na kontrolowaną transakcję

Po potwierdzeniu połączenia użytkownik zatwierdza **konkretny test otwarcia i zamknięcia na tym koncie DEMO**. W panelu: **Diagnostyka i dziennik → Test wykonania (DEMO)**. Okno potwierdzenia informuje, że powstanie rzeczywista transakcja DEMO; anulowanie nie powinno wysłać zlecenia.

- Test używa minimalnego wolumenu tylko wtedy, gdy przejdzie kontrole konta, danych, kosztów i ryzyka. Może poprawnie odmówić, np. przy zamkniętym rynku lub zbyt małym equity.
- Obserwuj pozycję w cTrader: zgodny symbol, **dokładna wygenerowana etykieta zakresu**, minimalny wolumen, **SL i TP widoczne u brokera**.
- Test następnie próbuje przesunąć SL i zamknąć pozycję. Oczekuj jawnego potwierdzenia kolejnych kroków w panelu.
- W cTrader → **History** sprawdź identyfikator pozycji, otwarcie i zamknięcie. Zapisz identyfikator i komunikat końcowy; bez nich nie oznaczaj integracji jako zaliczonej.
- Samo zniknięcie pozycji ani dawny napis „TEST ZALICZONY” nie oznacza PASS. Czekaj na **Certyfikat testu DEMO: PASS** po trwałym rozliczeniu wszystkich closing deals tej samej pozycji. Panel pokazuje konto/brokera/walutę, dokładną etykietę/ID zakresu, ścieżkę i wersje.
- Niepewny open/close, brak potwierdzenia SL/TP/modify, naturalny SL/TP/manual close przed poleceniem close, spóźnione dowody i przerwany test nie certyfikują. Późniejszy replay może naprawić księgowanie, ale nie wskrzesi zakończonego testu. Test nie wznawia się po restarcie/reconnect i nie uruchamia strategii.
- Przy błędzie, utracie połączenia lub niepewnej odpowiedzi **nie klikaj ponownie w ciemno**. Najpierw sprawdź faktyczne pozycje i historię. Jeśli pozycja została, zamknij ją w cTrader lub użyj Emergency Stop, pamiętając, że APEX potrzebuje działającego mostu do wysłania zamknięcia.

## 4. Testy odporności — również tylko DEMO

- **Restart APEX:** przy istniejącej własnej pozycji DEMO odtworzenie planu i uzgodnienie bez ponownego zlecenia tego sygnału; nowe wejścia wymagają START.
- **Rozłączenie mostu:** brak nowych operacji na nieaktualnym połączeniu; po powrocie uzgodnienie z brokerem, bez duplikatów.
- **Emergency Stop:** blokada dalszych wejść i próba zamknięcia wyłącznie pozycji APEX. Po restarcie blokada nadal obowiązuje; reset prowadzi do ZATRZYMANY, nie PRACA.
- **Pozycje obce:** istniejąca pozycja ręczna lub innego bota ma pozostać nietknięta. Nie otwieraj zbędnej dodatkowej pozycji wyłącznie dla tego testu bez osobnej zgody.
- **Drugi most:** drugie połączenie tego samego konta zostaje odrzucone.
- **Ryzyko:** porównaj wartości domyślne 0,5% / 1,5% / 5% / x5 / 3 pozycje; nie podnoś ich w celu wymuszenia testu.

## 5. Automatyczna praca

Dopiero przy ważnym trwałym PASS i aktualnej gotowości synchronizacji/ryzyka naciśnij **START** i **osobno zatwierdź** automatyczny handel DEMO. Po restarcie/reconnect zawsze STOP: pasujący certyfikat pozostaje, lecz potrzebne są świeże uzgodnienie i nowe jawne START. Zmiana konta, waluty, brokera, platformy, etykiety, BaselineId, ścieżki lub wersji unieważnia certyfikat. Nie ma przycisku/API „ustaw PASS”. Native nadal tylko odczyt/redukcja; nie jest alternatywną drogą wejścia.

Aktywny jest tylko **IndexDipBuy / indeksy / Experimental / BUY-only**; inne rodziny/klasy nie są dopuszczone. Wszystkie 64 nowe autentyczne próby badań zatrzymał preflight (0 ocenionych kont; n/net/koszty/PF/DD niedostępne). Brak przewagi i brak promocji Validated. Strategie mają prawo czekać bez zleceń — domyślny profil jest selektywny. Centrum decyzji pokazuje rzeczywiste przyczyny WAIT/BUY/SELL; nie generuje dodatkowych sygnałów AI. Emergency i pełne zamknięcie własnej pozycji nie wymagają certyfikatu, ale nadal potrzebują bezpiecznej tożsamości DEMO i działającego mostu.

W Windows muszą działać APEX, cTrader i most. Po wyłączeniu pozostają tylko SL/TP u brokera; trailing i decyzje strategii nie działają. STOP wstrzymuje także zwykłe zarządzanie, natomiast „Wstrzymaj nowe” pozostawia je aktywne.

## Protokół wyniku

- Pakiet/commit:
- Konto oznaczone DEMO w cTrader i APEX:
- Rzeczywiste saldo, waluta i czas notowań zgodne:
- Zgoda użytkownika na test i czas:
- Symbol / wolumen / identyfikator pozycji:
- SL oraz TP widoczne w cTrader:
- Zmiana SL potwierdzona:
- Zamknięcie i wpis w History potwierdzone:
- Trwały PASS, dokładna etykieta/BaselineId, ścieżka i wersje z panelu:
- Restart / duplikaty / Emergency Stop sprawdzone:
- Błędy i czynności wymagające powtórzenia:

Brak potwierdzonej przewagi strategii pozostaje ograniczeniem badawczym, nie przeszkodą w odbiorze technicznym DEMO. **Nie ma etapu przejścia na REAL.**
