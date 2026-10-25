# APEX Trading AI — ukończenie techniczne wersji DEMO

## Autoryzowany zakres
Użytkownik polecił autonomiczną implementację na bazie `claude/beautiful-mccarthy-9cibo1` w repozytorium `kubson1995xx-cloud/bottt`, z regularnymi commitami i aktualizacją HANDOFF. Zachować architekturę C#/.NET i istniejące strategie. Nie oczekiwać zatwierdzania drobnych decyzji. Nie aktywować handlu REAL. Prawdziwa transakcja DEMO wymaga osobnego zatwierdzenia użytkownika po połączeniu.

## Produkt
Samodzielny pakiet Windows 11 x64: lokalny polski panel APEX Desktop i most do Pepperstone cTrader Desktop DEMO na tym samym komputerze. MT5 jest opcjonalny i jego awaria nie może blokować cTrader. Nie dodawać OAuth/Open API, jeżeli istniejący most Desktop wystarcza; logowanie do brokera pozostaje w cTrader użytkownika.

## Wymagane zachowanie
Rzeczywiste dane konta DEMO, analiza instrumentów i sygnałów istniejącymi strategiami, otwieranie/zamykanie pozycji DEMO, obowiązkowe poprawne SL i TP, wielkość pozycji z budżetu ryzyka. Domyślnie ryzyko 0,5% na transakcję, strata dzienna 1,5%, drawdown 5%, efektywna dźwignia x5 i istniejący limit 3 pozycji. Emergency Stop, ochrona przed duplikatami, trwały zapis i uzgadnianie po restarcie. Zachować wykluczenie kryptowalut, brak martingale/gridu/uśredniania. Domyślny start zatrzymany; bez automatycznego wznowienia nowych wejść po restarcie. Nie dotykać pozycji ręcznych ani innych botów.

Panel: połączenie, saldo/equity, pozycje, historia, wyniki, START, STOP, wstrzymanie wejść, Emergency Stop i jawny test wykonania. Brak AI Lab, uczenia i automatycznych aktualizacji w UI, procesach i pakiecie. Pasywny rejestr decyzji może pozostać; nie jest modelem uczącym się.

## Sposób realizacji
Nie przebudowywać projektu. Poprawiać potwierdzone defekty z regresją RED→GREEN. Jeżeli strategia nie wyznacza TP, zapewnić jawnie udokumentowany ochronny TP zamiast dopuszczać niezabezpieczone zlecenie. Blokada LIVE ma obejmować wszystkie dostarczane ścieżki, nie tylko domyślną konfigurację. Nie usuwać archiwalnego kodu AI Lab użytkownika; testować go oddzielnie, nie dołączać do produktu.

## Dowody i granice
Wykonać bazowe i końcowe build/test wszystkich projektów, kompilację cBotów, integrację z prawdziwym procesem Desktop i symulatorem wyraźnie opisaną jako symulator. Przygotować pakiet win-x64 oraz automatyczny build i weryfikację na Windows. Nie przedstawiać kompilacji krzyżowej jako uruchomienia na Windows. Nie przedstawiać symulatora jako rzeczywistego połączenia Pepperstone. Rzeczywista integracja i transakcja końcowa wymagają cTrader Windows użytkownika; przekazać wyłącznie niezbędne czynności, bez przesyłania haseł w czacie i bez sekretów w Git.

## Poza zakresem
Gwarantowana rentowność, nowe modele AI, automatyczne aktualizacje, przebudowa UI/architektury i handel LIVE. Historyczny raport o braku potwierdzonej przewagi pozostaje uczciwie dostępny.
