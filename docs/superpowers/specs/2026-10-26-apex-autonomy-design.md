# APEX DEMO — stabilność, rzetelne badania i odbiór Windows

## Autorytet i cel
Aktualne polecenie użytkownika jest specyfikacją i zgodą na autonomiczną implementację (bez ponownego pytania o zatwierdzenie każdego kroku). Baza a886a152b2e9bcdf6a6599bfdc1e76dd94681336 z bottt / claude/beautiful-mccarthy-9cibo1. Wykonanie w izolowanym worktree apex-demo/autonomy. Zachować C#/.NET, strategie i polski panel. Celem nie jest obietnica rentowności: wiarygodne wyniki i bezpieczna odmowa są lepsze od pozornego sukcesu.

## Nienaruszalne ograniczenia
- Wyłącznie Pepperstone cTrader DEMO. REAL zablokowany w każdej dostarczonej ścieżce.
- Prawdziwa transakcja DEMO wymaga osobnej przyszłej zgody użytkownika. Nie uruchamiać brokerowego START/testu w ramach implementacji.
- Bez AI Lab, treningu, LLM podejmującego BUY/SELL i automatycznych aktualizacji. Archiwum nie jest produktem.
- Zachować domyślne 0,5% ryzyka, 1,5% limitu dnia, 5% DD, x5, obowiązkowe SL/TP i Emergency Stop.
- Nie aktywować odrzuconych strategii ani oznaczać Validated bez autentycznych dowodów. Nie używać dawnych CSV jako wyników nowej wersji.
- Model/provider kodu i rozumowania: gpt-6.1-sol / openai-codex, bez fallbacku.
- Żadnych sekretów w repo. Commity etapowe i aktualny HANDOFF z dowodami i ograniczeniami.

## P1 — trwały cykl transakcji
1. Closed events i replay historii muszą prowadzić do jednego ekonomicznego wyniku: dedupe po stabilnym tożsamościowym kluczu broker/account/position/deal lub zdefiniowanym zbiorczym zamknięciu pozycji, nie po przybliżonym czasie. Dokładna etykieta własności, poprawne prowizje/swap/net i obsługa partial fills. Powtórka eventu, replay+event i restart nie mogą zwiększyć liczników dwa razy. Najpierw trwały zapis, potem publikacja; dziennik i stan muszą umieć naprawić przerwanie pomiędzy zapisami.
2. Przed nowym ryzykiem po starcie/reconnect odtworzyć i uzgodnić pozycje oraz historię z cTrader. Rozszerzyć wspólny protokół z negocjowaną capability/wersją i kompletnymi stronami; stary most bez wymaganej funkcji ma odmówić nowego ryzyka, nie udawać ukończonego sync. Brak/niekompletna historia = widoczna blokada, dozwolone działania zmniejszające ryzyko. History request read-only; paginacja/zakres i watermark nie mogą gubić identycznych czasów czy częściowych zamknięć. Nie rekonstruować fikcyjnego R, jeśli brak planu.
3. Intencja pełnego close, powód, próby/deadline i stan potwierdzenia przetrwają restart; nie usuwać planu zanim potrzebne dane nie zostaną zaksięgowane. Ponawiać tylko redukcję własnego konkretnego ID, nie niepewne open. Awaria zapisu nadal pozwala na idempotentną pełną likwidację.
4. Partial oznaczać ukończony dopiero po potwierdzonym wykonaniu/redukcji wolumenu. Uncertain bez ślepego powtórzenia, odrzucone retry po ponownej ocenie, restart nie wykonuje dwa razy tej samej redukcji. Brak minimalnego dopuszczalnego wolumenu ma jawny terminalny wynik bez pętli.
5. Journal komend ma bezpieczną kompaktację terminalnych zapisów przy zachowaniu ochrony przed replay starych open oraz wszystkich uncertain/live/pending reservations. Rozwiązanie nie może usuwać na ślepo rezerwacji, bo rezerwacje są barierą ryzyka. Twardy limit nadal fail-closed; testować >20000 terminalnych operacji i awarie kompaktacji. Retencja/tombstones/epoch muszą mieć jawne granice.
6. Pierwszy START na danym koncie/wersji ścieżki wykonania wymaga trwałego PASS pełnego testu DEMO open→SL/TP→modify→close, osobno uruchomionego świadomie przez użytkownika. Status PASS związany z rachunkiem, brokerem, walutą i wersją protokołu/bezpieczeństwa. Stare dane nie mogą zmyślić PASS. Backtest izolowany nie wymaga realnego wykonania (jawny bezpieczny wyjątek). Test techniczny nie może sam wymagać już zaliczonego testu.
7. Zerwanie broker connection, lokalnego transportu i stale account/positions/quotes/history blokuje nowe wejścia. Reconnect nie może niekontrolowanie przywrócić Running; domyślnie STOP + jawne ponowne START po synchronizacji. Przeterminowane sygnały nie odżywają po przerwie. UI pokazuje potrzebny test/sync; Emergency i pełne close nie blokować brakiem PASS.

## P2 — backtester
- Equity i position net muszą używać jednego modelu opłat; opłaty raz przy zrealizowanym cashflow, jawna rezerwa wyjścia w equity jeśli net ją zawiera. Testy open→partial→full dla niezerowych prowizji i FX.
- Brakujące/niefinitywne/historycznie niedostępne FX odmawiają wiarygodnego przebiegu i dają listę braków; żadnego kursu1 jako cichego zamiennika (wyjątek rzeczywista identyczna waluta).
- Minimum prowizji w katalogu/scenariuszu i wykonaniu; jawne założenia jeśli brak danych brokera. Ten sam floor musi trafić do sizingu, fill i raportów.
- Walk-forward: selekcja na informacjach znanych do końca IS. Co najmniej purging transakcji zamkniętych po granicy IS; poprawna separacja fold i używanego stanu. OOS wykonywany bez dostępu do przyszłych wyników, niezależne okresy i wersjonowany profil. Raportować zakresy, koszty, brak danych i brak dowodów; nie uwiarygodniać przecieku samą nazwą WF.
- Dedykowany testowy projekt backtestera z rzeczywistymi źródłami narzędzia, w solution i Windows/package gates.

## P3 — strategie i dane
Testy każdej z sześciu rodzin: positive i negative wejścia, BUY/SELL tam gdzie istnieją, SL/TP, wyjścia, brak look-ahead, czas i spread/koszty. IndexDipBuy testować z rzeczywistym DefaultProfile (SMA100/RSI2<5/SL3ATR/10bar/TP3R), a brak SELL ma być świadomą asercją. Nie dopisywać shortów do long-only w celu zaliczenia listy.
Pozyskać autentyczne legalnie dostępne dane z dokumentowanym źródłem, wersją/hash, zakresem, TF, timezone, brakami i kosztami. Mieć niezależne OOS/holdout, adekwatne warmup i sesje, raporty po strategii i klasie z n, net, PF, DD, WR i kosztami. Nie przedstawiać synthetic jako danych rynku. Jeśli danych dla części instrumentów/okresów nie uda się pozyskać, oznaczyć dokładny blocker i nie walidować strategii. Wieloklasowość wyłącznie po dowodach; brak przewagi = zachowanie wyłączenia. Porównać wszystkie sześć rodzin w osiągalnym rzetelnym zakresie zamiast fikcyjnie szerokich wyników.

## P4 — wydanie i odbiór
Nowy spójny ZIP host+most+native, polski panel, prosta instrukcja parowania, manifest i źródłowy SHA, Windows build + real EXE smoke, pełne produktowe testy i niezależny przegląd. Zsyntetyzowana integracja obejmuje historię, pozycje, SL/TP, net, restart/reconnect i zdarzenia zduplikowane, ale nie zastępuje osobno zatwierdzonej brokerskiej transakcji. Dokument końcowy: zmiany, wyniki testów, aktywne strategie, wyniki OOS albo jawny brak, bramki wymagające cTrader użytkownika.

## Kontrakty doprecyzowane po builder check
- SDK1.0.21 posiada History.FindByPositionId/FindByClosingDealId, Count/indexer, lecz nie udokumentowaną paginację/LoadMore ani flagę pełnego zakresu brokerskiego. Zamrożony snapshot+komplet stron dowodzi transmisji dostępnego zbioru, nie całej historii brokera. Jawnie zdefiniować zakres/baseline; brak dowodu wymaganej historii oznacza CoverageUnknown i blokadę nowego ryzyka, nie wymyśloną kompletność.
- Preferowany surowy ledger zamykających dealów: klucz pełnej tożsamości rachunku, dokładnej etykiety, pozycji i ClosingDealId. Partial cashflow raz; jedna finalna transakcja dopiero po pełnym close ze zsumowaniem unikalnych dealów. Broker net zawiera już prowizję/swap; nie naliczać ponownie. Identyczny klucz z inną treścią blokuje sync. Brak historii po evencie = oczekiwanie/replay, nigdy fallback do niepełnego wyniku Position.
- Jeden autorytatywny trwały commit księgowania obejmuje dedupe, cashflow, sfinalizowane ID, plany/intencje i outbox. CSV jest atomową/idempotentną projekcją do odbudowania; nie drugim niezależnym źródłem prawdy. Flush LocalStorage nie jest dokumentowaną transakcją wielokluczową; sprawdzić generacje/odtwarzanie lub fail-closed przy niejednoznaczności.
- Migracja starych agregatów + pełny replay nie może dodawać obu. Archiwizować dawny zakres albo odtworzyć potwierdzony zakres, zachowując emergency/reservations/plans i jawnie blokując nieudowodnione pokrycie. Brak pierwotnego R nie jest wynikiem0R do monitora przewagi.
- PASS związany z platformą/ścieżką wykonania i safety-version, nie wyłącznie numerem. Wyjątek backtestu z zaufanej kompozycji runtime, nie z podatnego EngineConfig.IsBacktest lub komunikatu mostu. Test spoofingu tej flagi w produkcyjnym gatewayu musi odmawiać.
- Kompaktacja journala zachowuje minimalne tombstone komend i sygnałów, także gdy stary signal dostanie nowe commandId po pruning Core. Nie usuwać replay protection tylko z powodu wieku/licznika; osobne limity cache rezultatów i ochrony. Nie obiecywać nieskończonego przyjmowania dowolnych ID przy skończonym trwałym magazynie.
- Dane OANDA2005–May2020 pozyskane ponownie są rzeczywistymi danymi, ale okres był już używany; to historyczna ponowna ocena, nie świeży nietknięty holdout. Nowy okres wymaga nowszego źródła. Nie publikować surowych danych z nieustalonymi prawami redystrybucji.

## Rozstrzygnięcie: użyteczny zakres cTrader zamiast trwałej blokady
- Nowa instalacja może ustanowić własny przyszły zakres księgowania: wygenerowany nieprzewidywalny exact label (do20znaków), BaselineId i pełna tożsamość rachunku/ścieżki. Nie jest to deklaracja całej historii brokera. Wymagane świeże migawki pozycji i pending orders oraz zdrowy trwały stan.
- Minimalny wspierany tryb to osobne, wyłączne konto DEMO typu hedging bez innych otwartych pozycji/zleceń; obcych obiektów nie zamykać dla uzyskania gotowości. Brak gwarancji wykrywania dowolnych ukrytych zewnętrznych zmian wolumenu przy niepełnych dowodach SDK musi być ujawniony.
- Każde nowe open ma przed wysłaniem trwały manifest żądania, a po wykonaniu pozytywny dowód pierwotnego rzeczywiście wykonanego wolumenu/opening-deal. Manifest pozostaje po finalizacji; remaining volume nie może udawać initial fill. Zachować niepewne open nawet po timeout+braku widocznej pozycji.
- Gotowość Core wyprowadza z zachowania wolumenu: pierwotne potwierdzone V0 = unikalne trwale zaksięgowane zamknięcia C + obecne remaining O, także gdy pozycja nadal otwarta. Brak choćby jednej prawidłowej jednostki blokuje; nowe opening impacts/growth/kolizje blokują bez dopasowywaniaV0. Brakujące oryginalne dowody/legacy wymagają audytowanego uzgodnienia, nie resetu etykiety.
- BrokerConnected/Disconnected events z SDK zmieniają niezależną generację brokera i blokują permit również przy działającym lokalnym WebSocket. Routine refresh nie zatrzymuje zdrowej sesji, ale disconnect/restart/hardconflict wymaga ponownego świadomego START.
- Scope bootstrap jest operacją konfiguracyjną/read-only u brokera, nigdy ukrytym testem transakcyjnym. Późniejszy PASS wymaga pełnych dowodów i trwale rozliczonego terminal close. Produkcyjny gateway nie może zyskać wyjątku poprzez flagę IsBacktest lub nazwęSymulator.
- Szczegółowy kontrakt i testy w task-1-scope-brief.md w ledger workspace oraz scratch/apex-autonomy/scoped-baseline-design.md. Wdrożenie nastąpi po naprawie/review przypadków2–8; pierwsze zadanie nie jest ukończone przy stałej blokadzie każdego prawidłowego cTrader.

## Decyzje wykonawcy
- Lokalny bridge pozostaje główną ścieżką zamiast nowego OAuth/API.
- Implementacja kolejno w zależnych zadaniach, po każdym testy i przegląd; niezależne pozyskanie danych może działać równolegle tylko poza kodem produktu.
- Historia ma być broker-authoritative; pełny replay może być bezpieczniejszy niż optymistyczny watermark. Implementer wybierze minimalną poprawną wersję po sprawdzeniu SDK i ujawni koszt wydajnościowy.
- Wyrażona zgoda na autonomiczną implementację zastępuje ponowne zatwierdzanie spec/plan/worktree. Publikacja nowego artefaktu na roboczej gałęzi/repo w ramach dotychczasowego sposobu pracy; bez merge do main i bez zmian innych projektów.
