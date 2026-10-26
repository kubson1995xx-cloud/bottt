# Ograniczenia i status APEX DEMO

Kod jest zintegrowany i testowany; aktualne liczby podaje [WYNIKI_TESTOW.md](WYNIKI_TESTOW.md). Pakiet Windows jest przeznaczony do odbioru **Pepperstone cTrader DEMO**, nie REAL. Dopóki nie sprawdzono połączenia i zatwierdzonej transakcji u brokera, nie jest to w pełni odebrany system brokerski.

## Ważne decyzje

- Brak AI Lab, uczenia i automatycznych aktualizacji w produkcie. Archiwalny kod/testy pozostają osobno. Centrum decyzji jest pasywną wizualizacją reguł.
- Restart/reconnect bez nowych wejść do ręcznego START. Stare flagi/frazy nie odblokują REAL.
- MAIN Desktop + ApexBridge wymaga ważnego trwałego certyfikatu testu DEMO (stan v4) i świeżego scoped sync przed START strategii. Certyfikat jest związany z kontem, dokładną etykietą/ID, ścieżką i wersją. Nie wznawia przerwanego testu; nie certyfikuje całej historii brokera. Native nadal tylko odczyt/redukcja. Lokalna próba syntetyczna nie zastępuje zgody i odbioru u brokera; [procedura](PROCEDURA_DEMO_PEPPERSTONE.md).
- Wymagane SL i TP. Strategia bez stałego celu dostaje ochronny **TP 3R**; reguły wejścia zachowane, ale zmieniony limit wyjścia wymaga ponownych badań wyników. Stare własne pozycje bez poprawnego TP są likwidowane, bo interfejs nie ma ModifyTP.
- Dokładna etykieta chroni pozycje obce; nie nadawaj ręcznej pozycji etykiety APEX.
- Niepewny/corrupt zapis nie jest kasowany ani przywracany ze starego backupu. Operator uzgadnia stan z brokerem; nie usuwaj rejestru w celu ominięcia limitów.
- Awaria dysku blokuje nowe ryzyko. Pełne close konkretnego własnego ID jest niezależne od rejestru i idempotentne; partial-close/open nie omijają rejestru. Przy awarii dysku może zabraknąć trwałego audytu zamknięcia.
- Rejestr cTrader nie usuwa niepewnych rezerwacji i ma limit 20 000 wpisów. Zapełnienie wymaga operatora; pełne close nadal może być wysłane.
- Brak pozycji po lokalnym timeout nie jest gwarancją braku opóźnionego wykonania. Sprawdź pozycje i History przed jakimkolwiek ręcznym ponowieniem.
- Nowe pola minimum prowizji są opcjonalnym rozszerzeniem v1. Aktualizuj **host i most razem** — stary host może je zignorować. Nieznana prowizja/FX nie jest zastępowana udawaną poprawną stawką.

## Ograniczenia operacyjne

Windows, APEX, cTrader i lokalny most muszą działać. cTrader Cloud nie zastępuje lokalnego Desktop. Bez sieci/brokera/zasilania nie ma gwarancji wykonania Emergency Stop. SL/TP pozostają u brokera, lecz nie gwarantują ceny przy luce/poślizgu. STOP zatrzymuje także zwykłe zarządzanie; „Wstrzymaj nowe” je zachowuje.

Panel jest tylko lokalny. Nie wystawiaj portów w sieci. Procesy tego samego użytkownika mogą odczytać token/klucz z LocalAppData; nie publikuj konfiguracji. EXE nie ma podpisu certyfikatem — hash potwierdza integralność, nie tożsamość wydawcy.

Klasyfikacja instrumentów jest zachowawcza i może pomijać poprawne symbole. Brak sygnału nie musi oznaczać awarii. Nie luzuj limitów, aby wymusić transakcję.

MT5: opcjonalny, nieskompilowany w MetaEditorze; nie-USD blokowane zamiast udawania poprawnej konwersji. Brak odbioru i pełnej równoważności cTrader. Nie blokuje głównej ścieżki WebSocket.

## Badania

Brak potwierdzonej przewagi. Stare dane 2005–2020 są MID i nie odtwarzają wszystkich kosztów/kalendarza. Backtester nie jest brokerem: ograniczone pokrycie M15, modelowane koszty i historyczny fallback FX=1 przy brakach danych. Nie używaj starego raportu jako dowodu rzeczywistej egzekucji ani walidacji nowego TP 3R. Bieżące FX live są sprawdzane osobno.

## Otwarte warunki końcowe

Windows 11 użytkownika → prawdziwy lokalny cTrader DEMO → porównanie konta, danych, specyfikacji, prowizji i marginu → osobna zgoda na test → pozycja i History potwierdzające open/SL/TP/modify/close → restart/reconnect/Emergency Stop. Procedura w pakiecie. **Nie ma etapu przejścia na REAL.**
