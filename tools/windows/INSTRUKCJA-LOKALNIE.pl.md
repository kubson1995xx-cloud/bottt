# APEX — bot lokalnie na Twoim PC (OKX DEMO)

Ta wersja handluje **wyłącznie na OKX DEMO** (cash SPOT: BTC-EUR, ETH-EUR, SOL-EUR) bezpośrednio z Twojego komputera — bez VPS i bez SSH. Strategia, limity ryzyka, SL/TP i STOP są takie same jak w bocie na VPS. LIVE, pożyczki, margin, wypłaty i transfery są zablokowane.

## Zanim zaczniesz
- **Bot na VPS musi pozostać zatrzymany (STOP)**, jeśli używasz tego samego konta OKX DEMO. Dwa boty na jednym koncie są niedozwolone.
- **Kapitał bota = całe saldo EUR na tym koncie DEMO.** Inne waluty (np. BTC, ETH, SOL już leżące na koncie) są wykluczone: bot ich nie liczy jako swojego kapitału i **nigdy ich nie sprzedaje** — sprzedaje tylko to, co sam kupił.
- To jest **nowa lokalna historia** na tym komputerze, oddzielna od historii bota na VPS (historia VPS nie jest przenoszona ani zmieniana).
- Komputer musi być włączony i mieć Internet przez cały czas działania bota.

## 1. Klucz API OKX DEMO
1. Zaloguj się na OKX → **Demo Trading (Handel demo)** → **API** → utwórz nowy klucz API.
2. Uprawnienia: **Read + Trade**. **NIE zaznaczaj Withdraw (wypłaty).**
3. Zapisz: **API Key**, **Secret Key**, **Passphrase**. Konto DEMO musi mieć saldo **EUR**.
4. Klucza nie wysyłaj nikomu i nie wklejaj go do czatu — wpisujesz go tylko w okno APEX na swoim PC.

## 2. Uruchomienie
1. Rozpakuj **cały** plik ZIP do zwykłego folderu (np. `C:\APEX`). Nie uruchamiaj programu z wnętrza ZIP.
2. Uruchom **`Uruchom-APEX.cmd`** (otwiera klasyczne okno konsoli APEX). Możesz też uruchomić `APEX-Desktop\ApexDesktop.exe`.
3. Otworzy się panel w przeglądarce (adres `127.0.0.1`). **Okno konsoli APEX musi pozostać otwarte** — to ono jest botem.

## 3. Pierwsze uruchomienie: nowy lokalny bot (tylko raz)
1. W panelu widzisz „Tryb: nie wybrano”. Kliknij **„Nowy lokalny bot na tym PC (DEMO, pierwsze uruchomienie)”**.
2. W oknie Windows **„APEX — lokalny bot: klucz OKX DEMO”** wklej API Key, Secret Key i Passphrase (pola są maskowane) → **Połącz**. Klucz LIVE zostanie odrzucony.
3. W oknie **„APEX — potwierdź nowego lokalnego bota”** przeczytaj cały tekst (UID konta, kwota EUR = kapitał bota, lista wykluczonych walut, ostrzeżenie o VPS). Przepisz dokładnie oba zdania oświadczenia i słowa **TWORZĘ LOKALNEGO BOTA** → Zatwierdź.
4. Pojawi się **chroniona zgoda Windows** (ekran zabezpieczeń). Wpisz **hasło swojego konta Windows** — nie PIN i nie hasło OKX. Jeśli logujesz się tylko PIN-em, użyj hasła konta Microsoft.
5. Wynik `LOCAL_OWNER_CREATED_STOP_ATTACH_NEXT` = bot utworzony i zatrzymany (STOP). Nic jeszcze nie handluje. Klucz jest zapisany zaszyfrowany (Windows DPAPI) w prywatnym katalogu Twojego profilu, nie w folderze ZIP.

## 4. START (po każdym uruchomieniu programu)
Kliknij kolejno (przy niektórych krokach Windows ponownie poprosi o hasło konta Windows):
1. **„Dołącz istniejący lokalny owner DEMO (nie START)”**
2. **„Chronione Observe — połącz istniejący zakres”**
3. **„Przygotuj istniejący DEMO (nie START)”**
4. **„Uzbrój Execute (nie START)”**
5. **„START — osobne polecenie”**

Panel pokazuje **„Tryb: Lokalnie (ten PC)”**, a podczas pracy także „uśpienie komputera zablokowane” i czerwone ostrzeżenie. Program sam blokuje usypianie systemu tylko wtedy, gdy bot DZIAŁA.

**Zabezpieczenie zapasowe:** w Windows ustaw *Ustawienia → System → Zasilanie i bateria → Ekran i uśpienie → „Uśpij urządzenie po” = Nigdy* (przy zasilaniu sieciowym).

## 5. Zatrzymanie
1. W panelu kliknij **„STOP / cofnij zgody”** i poczekaj na wynik `OPERATOR_STOP`.
2. Dopiero potem zamknij okno konsoli APEX.
- STOP **nie sprzedaje** otwartych pozycji i nie jest wyjściem z rynku.
- **Nie zamykaj okna APEX przyciskiem X, gdy bot działa.** W klasycznym oknie (z `Uruchom-APEX.cmd`) przycisk X jest wtedy wyszarzony. Jeśli program otworzył się w **Windows Terminal**, X nie jest blokowany — używaj STOP w panelu.
- Zamknięcie samej karty przeglądarki nie zatrzymuje bota.

## 6. Po restarcie komputera lub programu
Bot zawsze startuje w **STOP** — nie ma automatycznego START. Powtórz tylko kroki z punktu 4 (Dołącz → Observe → Przygotuj → Uzbrój → START). Tworzenia bota (punkt 3) nie powtarzasz.

## 7. Ważne
- Nie handluj ręcznie na tym koncie DEMO, gdy bot działa — niezgodność sald zatrzyma bota (STOP) do wyjaśnienia.
- Jeśli na koncie nie ma EUR albo konto ma włączone pożyczki/margin, utworzenie bota zostanie odrzucone z kodem w panelu; nic nie zostanie zapisane.

## Co zostało przetestowane, a co nie
- Przetestowano automatycznie na **Windows Server 2025 (GitHub Actions)** z **syntetyczną giełdą OKX DEMO**: tworzenie bota z konta wielowalutowego (EUR + BTC + SOL wykluczone), PREPARE, Execute, START, wejścia BTC/ETH z ochroną, wyjście, rozliczenie, blokada uśpienia włączana/wyłączana, STOP, ponowne otwarcie w STOP; oraz sam rozpakowany plik EXE: panel, etykieta trybu, okna klucza (odrzucenie LIVE) i potwierdzenia, STOP/status przez HTTP.
- **Nie przetestowano:** Twojego Windows 11, prawdziwego konta i kluczy OKX, ani przejścia przez chroniony ekran zgody Windows w rozpakowanym EXE (ten ekran nie daje się zautomatyzować; ten etap był testowany w teście natywnym z adapterem testowym). Pierwsze prawdziwe uruchomienie obserwuj i w razie wątpliwości naciśnij STOP.
