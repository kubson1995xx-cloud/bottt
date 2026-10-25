# Procedura odbioru: Pepperstone cTrader DEMO

**To test z prawdziwym serwerem brokera. Symulator i zielone testy .NET go nie zastępują. Tylko środki demonstracyjne.**

## 1. Połączenie bez handlu

1. Uruchom gotowy pakiet Windows i połącz lokalny `ApexBridge` zgodnie z [PODLACZENIE_PLATFORM.md](PODLACZENIE_PLATFORM.md).
2. Sprawdź w **obu programach** ten sam numer konta Pepperstone DEMO, walutę i saldo. Nie przesyłaj hasła, tokenów ani pliku konfiguracyjnego.
3. Poczekaj na świeże notowania, instrumenty i wymagane świece. Sprawdź pozycje oraz dziennik. Oczekiwany stan: **ZATRZYMANY**; nie naciskaj jeszcze START.
4. Nie uruchamiaj równolegle wersji natywnej, starego APEX ani drugiej instancji mostu. Zapewnij połączenie internetowe i wyłącz usypianie Windows na czas testu.
5. Zanotuj commit z `apex-build.json`, czas UTC i numer konta DEMO. Na VPS ten etap nie jest potwierdzony samym faktem zbudowania ZIP.

## 2. Osobna zgoda na kontrolowaną transakcję

Po potwierdzeniu połączenia użytkownik zatwierdza **konkretny test otwarcia i zamknięcia na tym koncie DEMO**. W panelu: **Diagnostyka i dziennik → Test wykonania (DEMO)**. Okno potwierdzenia informuje, że powstanie rzeczywista transakcja DEMO; anulowanie nie powinno wysłać zlecenia.

- Test używa minimalnego wolumenu tylko wtedy, gdy przejdzie kontrole konta, danych, kosztów i ryzyka. Może poprawnie odmówić, np. przy zamkniętym rynku lub zbyt małym equity.
- Obserwuj pozycję w cTrader: zgodny symbol, etykieta `APEX`, minimalny wolumen, **SL i TP widoczne u brokera**.
- Test następnie próbuje przesunąć SL i zamknąć pozycję. Oczekuj jawnego potwierdzenia kolejnych kroków w panelu.
- W cTrader → **History** sprawdź identyfikator pozycji, otwarcie i zamknięcie. Zapisz identyfikator i komunikat końcowy; bez nich nie oznaczaj integracji jako zaliczonej.
- Przy błędzie, utracie połączenia lub niepewnej odpowiedzi **nie klikaj ponownie w ciemno**. Najpierw sprawdź faktyczne pozycje i historię. Jeśli pozycja została, zamknij ją w cTrader lub użyj Emergency Stop, pamiętając, że APEX potrzebuje działającego mostu do wysłania zamknięcia.

## 3. Testy odporności — również tylko DEMO

- **Restart APEX:** przy istniejącej własnej pozycji DEMO odtworzenie planu i uzgodnienie bez ponownego zlecenia tego sygnału; nowe wejścia wymagają START.
- **Rozłączenie mostu:** brak nowych operacji na nieaktualnym połączeniu; po powrocie uzgodnienie z brokerem, bez duplikatów.
- **Emergency Stop:** blokada dalszych wejść i próba zamknięcia wyłącznie pozycji APEX. Po restarcie blokada nadal obowiązuje; reset prowadzi do ZATRZYMANY, nie PRACA.
- **Pozycje obce:** istniejąca pozycja ręczna lub innego bota ma pozostać nietknięta. Nie otwieraj zbędnej dodatkowej pozycji wyłącznie dla tego testu bez osobnej zgody.
- **Drugi most:** drugie połączenie tego samego konta zostaje odrzucone.
- **Ryzyko:** porównaj wartości domyślne 0,5% / 1,5% / 5% / x5 / 3 pozycje; nie podnoś ich w celu wymuszenia testu.

## 4. Automatyczna praca

Dopiero po odbiorze bezpieczeństwa i testu wykonania naciśnij **START** i zatwierdź automatyczny handel DEMO. Strategie mają prawo czekać bez zleceń — domyślny profil jest selektywny. Centrum decyzji pokazuje rzeczywiste przyczyny WAIT/BUY/SELL; nie generuje dodatkowych sygnałów AI.

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
- Restart / duplikaty / Emergency Stop sprawdzone:
- Błędy i czynności wymagające powtórzenia:

Brak potwierdzonej przewagi strategii pozostaje ograniczeniem badawczym, nie przeszkodą w odbiorze technicznym DEMO. **Nie ma etapu przejścia na REAL.**
