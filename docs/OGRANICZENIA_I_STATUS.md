# Ograniczenia i status APEX DEMO

Kod jest zintegrowany i testowany; aktualne liczby podaje [WYNIKI_TESTOW.md](WYNIKI_TESTOW.md). Pakiet Windows jest przeznaczony do odbioru **Pepperstone cTrader DEMO**, nie REAL. Dopóki nie sprawdzono połączenia i zatwierdzonej transakcji u brokera, nie jest to w pełni odebrany system brokerski.

## Ważne decyzje

- Brak AI Lab, uczenia i automatycznych aktualizacji w produkcie. Archiwalny kod/testy pozostają osobno. Centrum decyzji jest pasywną wizualizacją reguł.
- Restart/reconnect bez nowych wejść do ręcznego START. Stare flagi/frazy nie odblokują REAL.
- MAIN Desktop + ApexBridge wymaga ważnego trwałego certyfikatu testu DEMO (stan v5; dowody certyfikatu v4 zachowane) i świeżego scoped sync przed START strategii. Certyfikat jest związany z kontem, dokładną etykietą/ID, ścieżką i wersją. Nie wznawia przerwanego testu; nie certyfikuje całej historii brokera. Native nadal tylko odczyt/redukcja. Lokalna próba syntetyczna nie zastępuje zgody i odbioru u brokera; [procedura](PROCEDURA_DEMO_PEPPERSTONE.md).
- Wymagane SL i TP. Strategia bez stałego celu dostaje ochronny **TP 3R**; reguły wejścia zachowane, ale zmieniony limit wyjścia wymaga ponownych badań wyników. Stare własne pozycje bez poprawnego TP są likwidowane, bo interfejs nie ma ModifyTP.
- Dokładna etykieta chroni pozycje obce; nie nadawaj ręcznej pozycji etykiety APEX.
- Niepewny/corrupt zapis nie jest kasowany ani przywracany ze starego backupu. Operator uzgadnia stan z brokerem; nie usuwaj rejestru w celu ominięcia limitów.
- Awaria dysku blokuje nowe ryzyko. Pełne close konkretnego własnego ID jest niezależne od rejestru i idempotentne; partial-close/open nie omijają rejestru. Przy awarii dysku może zabraknąć trwałego audytu zamknięcia.
- Rejestr cTrader v2 kompaktuje terminalne wyniki: do 256 pełnych rezultatów, osobno do 20 000 niepewnych rezerwacji, łącznie do 100 000 trwałych identyfikatorów/replay barriers oraz 64 MiB pojedynczego snapshotu UTF-8 (pierwszy osiągnięty limit blokuje nowe mutacje). Tombstones zachowują dokładny fingerprint/signal oraz dodatnie opening/reduction witnesses i lookup oryginalnego żądania. Nie znikają z wiekiem; nie ma automatycznego resetu/rotacji. Zapis całego snapshotu ma koszt rosnący z rozmiarem rejestru; brak obietnicy nieskończonego miejsca lub odbioru szybkości LocalStorage.
- Partial nie oznacza sukcesu po samej decyzji strategii. Intencja v5 zapisuje exact ID/signal/wolumen przed wysłaniem; `PartialDone` dopiero po dodatnim świadectwie udanej egzekucji i uzgodnieniu oryginalnego fill + raw closing union + dokładnej pozostałości. Sam spadek wolumenu/SL/manual close nie potwierdza tej akcji. Niepewność/restart = tylko read-only lookup, nigdy ślepy resend. Pewne odrzucenie może dostać nowy action ID po świeżej ocenie i minimum 15 s; `InvalidRequest`/techniczne niepewności nie uprawniają do retry. Zbyt mała redukcja/niedozwolona reszta ma trwały `Skipped`, nie fikcyjny sukces. Legacy partial w starym planie pozostaje `LegacyUnknown`, bez ponowienia.
- Brak recovery witness po crash tuż po egzekucji, ale przed checkpointem SDK może wymagać ręcznego uzgodnienia/pełnego close. Nie wymuszaj potwierdzenia ani nie kasuj stanu. Nowy host i most aktualizuj razem; niezgodny/stary partial protokół może bezpiecznie odmówić. Awaria zapisu/intencji/rejestru nie blokuje pełnej likwidacji własnego ID na DEMO; przy utracie połączenia jej wykonanie nie jest gwarantowane.
- Brak pozycji po lokalnym timeout nie jest gwarancją braku opóźnionego wykonania. Sprawdź pozycje i History przed jakimkolwiek ręcznym ponowieniem.
- Nowe pola minimum prowizji są opcjonalnym rozszerzeniem v1. Aktualizuj **host i most razem** — stary host może je zignorować. Nieznana prowizja/FX nie jest zastępowana udawaną poprawną stawką.

## Ograniczenia operacyjne

Windows, APEX, cTrader i lokalny most muszą działać. cTrader Cloud nie zastępuje lokalnego Desktop. Bez sieci/brokera/zasilania nie ma gwarancji wykonania Emergency Stop. SL/TP pozostają u brokera, lecz nie gwarantują ceny przy luce/poślizgu. STOP zatrzymuje także zwykłe zarządzanie; „Wstrzymaj nowe” je zachowuje.

Panel jest tylko lokalny. Nie wystawiaj portów w sieci. Procesy tego samego użytkownika mogą odczytać token/klucz z LocalAppData; nie publikuj konfiguracji. EXE nie ma podpisu certyfikatem — hash potwierdza integralność, nie tożsamość wydawcy.

Klasyfikacja instrumentów jest zachowawcza i może pomijać poprawne symbole. Brak sygnału nie musi oznaczać awarii. Nie luzuj limitów, aby wymusić transakcję.

MT5: opcjonalny, nieskompilowany w MetaEditorze; nie-USD blokowane zamiast udawania poprawnej konwersji. Brak odbioru i pełnej równoważności cTrader. Nie blokuje głównej ścieżki WebSocket.

## Strategie i badania

Aktywna jest wyłącznie **IndexDipBuy / Index / Experimental**, BUY-only: D1, SMA100, RSI2<5, wyjście SMA5 / 10 barów, SL3ATR oraz ochronny TP3R z Core. Pozostałe pięć rodzin istnieje i ma lokalne testy BUY/SELL, ale jest nieaktywne/Rejected. Brak strategii Validated i brak automatycznej promocji.

Nowe badania na autentycznych BID H1 EURUSD/XAUUSD/US500: **64/64 próby exit2 / success:false w preflight; 0 ocenionych kont i kombinacji**. Brakuje zweryfikowanych historycznych sesji, a US500 ma także wielotygodniowe braki. Nie wypełniano świec ani nie uznawano każdej luki za święto. n/net/WR/PF/opłaty/DD/OOS są **niedostępne**, nie zerowe; nie ma wyników rentowności ani udanych zero-trade runs. BID→modelowany MID, prowizje/spready/finansowanie i wolumen dostawcy nie są dowodem kosztów ani egzekucji Pepperstone. Lata 2024–2025 nie są certyfikowanym nietkniętym holdoutem.

Backtester v2 rozlicza pełne entry/partial/final cashflows, koszty i equity; izoluje/purguje WF oraz zachowuje wymuszone zamknięcia w pełnych wynikach kont, choć nie używa ich do naturalnej selekcji IS. Brakujące/stare FX odmawiają zamiast fallback1. Testy syntetyczne tych reguł nie są wynikami rynku. Stary raport 2005–2020 i jego fallback FX/koszty to wyłącznie historia, nie walidacja obecnego produktu ani TP3R. Surowe dane nie są redystrybuowane w pakiecie. Limity produkcji bez zmian: 0,5% ryzyka / 1,5% dziennie / DD5% / x5.

## Zakres dowodów technicznych

Niezależny final integration review przyjął Task4, MAIN PhaseC w zakresie źródeł/zachowanych testów lokalnych i zgodność schema5/certyfikatu/partial. Dwie zależne od separatora ścieżek pomyłki harnessu ScopedHostProcessTests naprawiono bez usuwania przypadków/asercji ani budowania Debug jako obejścia. Rzeczywisty Windows Release/PowerShell5.1, pełne bramki produktu, opublikowany EXE i ZIP wymagają dowodu konkretnego SHA z Actions oraz manifestu; same Linux/YAML/stare wyniki nie wystarczają. `windowsRuntimeTested=false` jest celowo zapisane przed późniejszym smoke — wynik smoke odczytaj w przebiegu, nie zmieniaj po fakcie bajtów ZIP. `brokerTested=false` pozostaje prawdziwe.

## Otwarte warunki końcowe

Windows 11 użytkownika → prawdziwy lokalny cTrader DEMO → porównanie konta, danych, specyfikacji, prowizji i marginu → osobna zgoda na test → pozycja i History potwierdzające open/SL/TP/modify/close → restart/reconnect/Emergency Stop. Procedura w pakiecie. **Nie ma etapu przejścia na REAL.**
