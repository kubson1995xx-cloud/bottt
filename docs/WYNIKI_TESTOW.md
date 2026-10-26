# Wyniki weryfikacji APEX DEMO — 2026-10-26

## Faktycznie wykonane bramki

Środowisko lokalne: Linux, .NET SDK 8.0.427, Node 26.8.1, PowerShell 7.6.9.

- `dotnet build APEX.Trading.Demo.sln -c Release`: **0 błędów i 0 ostrzeżeń**, oba cBoty `.algo` wygenerowane.
- `dotnet test tests/Apex.Tests -c Release`: **265/265**, failed=0, skipped=0.
- `dotnet test src/Platforms/cTrader/Tests -c Release`: **75/75**, failed=0, skipped=0.
- `dotnet test tests/Apex.AiLab.Tests -c Release`: **181/181**, failed=0, skipped=0. Oddzielne archiwum, nie część produktu.
- `npm test`: **11/11**. `npm run check`: oba pliki JS poprawne.
- Pełny skrypt publikacji Windows/ZIP wykonany na Linux. Manifest SHA-256 zgodny, kontrolowane uszkodzenie i dodatkowy plik poprawnie odrzucane.

Liczniki są oddzielne; testów AI Lab nie należy używać jako dowodu gotowości bota. TRX i logi są zapisane poza Git, finalne testy także w pakiecie/artefaktach Actions.

## Co sprawdzają testy

- Bezwarunkowa odmowa REAL, dokładna etykieta własności pozycji, brak kryptowalut.
- Ryzyko domyślne 0,5% / dzienna strata 1,5% / DD 5% / dźwignia x5 / 3 pozycje; wolumen w dół i uwzględnienie faktycznie wysyłanego SL.
- SL i TP obowiązkowe; ochronny TP 3R przy braku celu strategii. Nieprawidłowe jawne cele odrzucane, stare własne pozycje bez TP zamykane.
- Rezerwacja i flush przed open; brak duplikatów; błędy zapisu blokują nowe ryzyko. Uszkodzony/obcy zapis nie jest nadpisywany ani cofany do starego backupu.
- Restart/reconnect bez autostartu, trwały Emergency Stop, próby likwidacji mimo awarii dysku lub niedodatniego equity na znanym DEMO.
- Anulowanie i wygasanie poleceń bez późnego wykonania, brak utraty już przyjętego closed przy reconnect, świeżość niezależnych migawek.
- Rzeczywisty czas ticka, zamknięte świece, poprawna invalidacja cache i brak przyszłych danych.
- Minimalna prowizja SDK Asset/QuoteAsset przeliczona na walutę konta. Koszt za stronę `max(stawka,minimum)`; stała podłoga uwzględniona osobno w sizingu. Ręczny override nie kasuje minimum. Nieznane koszty/FX blokują wejście.
- Domyślny symbol testu zgodny z uniwersum; naturalne zniknięcie pozycji przed close nie daje PASS; niepotwierdzone polecenia nie certyfikują pełnej ścieżki.
- Panel DEMO-only, potwierdzenie START, brak AI Lab, SL/TP i identyfikatory pozycji, odczyt statusu ograniczony czasowo bez ponawiania POST.

## Testy działania — bez brokera

Prawdziwy proces ApexDesktop.dll + lokalna **syntetyczna platforma**: handshake, klucze, duplikaty, reconnect, blokada REAL, działanie WebSocket mimo awarii opcjonalnego TCP oraz domyślny test open → SL/TP → modify → close. Własna pozycja znika, pozycje obce pozostają nietknięte także po Emergency Stop.

Prawdziwa przeglądarka na działającym serwerze: 8 polskich widoków, stan ZATRZYMANY, limity, brak AI Lab. Dane jawnie oznaczone Symulator/APEX Symulator. Wykonano także offline syntetyczny backtest: 144 cykle i 2 transakcje — to nie dowód przewagi ani wykonania u brokera.

Regresje były obserwowane RED przed poprawkami, potem GREEN. Niezależny przegląd wykazał istotne luki na granicach; poprawiono je i ponowiono pełne zestawy. Wczesne czerwone TRX zachowano, nie przedstawiono jako zaliczonych.

## Windows

Workflow **APEX Windows DEMO** uruchamia Windows Server 2022, PowerShell 5.1, build/test/publish i rzeczywisty EXE smoke (HTTP, token, panel, zero kont i transakcji). Każdy wynik jest związany z konkretnym SHA. Końcowy URL i wynik dla przekazanego pakietu są w HANDOFF/raporcie przekazania. Pierwszy przebieg 40246708589 zbudował wszystko bez ostrzeżeń, ale asercja testowa oczekiwała Unix IOException zamiast Windows UnauthorizedAccessException; przenośną asercję poprawiono, nie osłabiając kontroli stanu.

## Niewykonane — bramki odbioru

Rzeczywisty Pepperstone cTrader DEMO, Windows 11 użytkownika, instalacja i API/LocalStorage cTrader, porównanie specyfikacji/kosztów/marginu oraz transakcja widoczna w History. **Żadnej transakcji u brokera nie wykonano.** Wymaga osobnego zatwierdzenia użytkownika po połączeniu. MetaEditor/MT5 również nie był uruchamiany.

Historycznej rentowności nie walidowano ponownie. Ochronny TP 3R zmienia wyjścia, więc stare raporty nie są walidacją nowych wyników. Procedura: [PROCEDURA_DEMO_PEPPERSTONE.md](PROCEDURA_DEMO_PEPPERSTONE.md).
