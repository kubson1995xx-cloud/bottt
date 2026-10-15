# Podłączanie platform do APEX Desktop

APEX Desktop to „mózg” bota: strategie, skaner okazji, RiskEngine, dziennik i panel.
Platformy (cTrader, MetaTrader 5, kolejne) podłącza się przez **mosty**. Most to mała wtyczka, która przesyła dane i wykonuje zlecenia.
Każde podłączone konto ma własny, niezależny silnik i własne limity ryzyka.

```
 cTrader Desktop ──(WebSocket ws://127.0.0.1:5077/bridge)──┐
                                                            ├──►  APEX Desktop  ──►  panel http://127.0.0.1:5077
 MetaTrader 5   ──(TCP 127.0.0.1:5078, JSON w liniach)─────┘
```

Wspólne zasady wszystkich mostów:
- połączenie tylko z tym samym komputerem (127.0.0.1) i tylko z **kluczem parowania** z panelu;
- konto **LIVE jest blokowane już w moście**, chyba że świadomie włączysz parametr „Zezwól na konto LIVE”. To trzecia warstwa obok strażnika konta w APEX i globalnego wyłącznika w pliku konfiguracji;
- most wykonuje zlecenia **tylko z SL** i dotyka **tylko pozycji z etykietą APEX**. Pozycje ręczne i innych botów są ignorowane;
- jedno konto może być połączone tylko przez jeden most naraz (duplikat jest odrzucany);
- po zerwaniu połączenia most łączy się ponownie co 5 s, a APEX uzgadnia stan pozycji z brokerem.

## cTrader (zalecane)

1. W APEX Desktop otwórz **Połączenia platform** i skopiuj **Klucz parowania**.
2. Zainstaluj most: dwuklik na `ApexBridge.algo` (folder `dist\cTrader`). cTrader doda cBota „ApexBridge” w sekcji **Automate → cBots**.
3. Zaloguj się w cTrader na konto **DEMO** (np. Pepperstone Demo).
4. W Automate wybierz ApexBridge → **Add instance** (dowolny symbol i interwał – most i tak obsługuje wiele instrumentów).
5. Parametry instancji:

| Parametr | Wartość |
|---|---|
| Klucz parowania (z panelu APEX) | wklejony klucz |
| Adres APEX Desktop | `ws://127.0.0.1:5077/bridge` (domyślnie) |
| Zezwól na konto LIVE | **Nie** (domyślnie) |
| Prefiks etykiety pozycji APEX | `APEX` |
| Maks. subskrybowanych instrumentów | 60 |

6. **Start**. Most nie wymaga dodatkowych uprawnień (`AccessRights.None`), więc cTrader nie pyta o zgodę na pełny dostęp.
7. W APEX konto pojawi się w zakładce **Połączenia platform** i na pasku kont u góry.

Ważne:
- Most musi działać w **cTrader Desktop na tym samym komputerze** co APEX Desktop. W chmurze cTrader (Cloud) połączenia sieciowe cBotów są ograniczone, więc most tam nie zadziała.
- Prowizja: most przekazuje typ i wartość prowizji z cTrader (np. Pepperstone Razor). Jeśli broker jej nie udostępnia (typ „nieznany”), APEX **nie otworzy pozycji** na takim instrumencie, bo nie może zweryfikować kosztu. Ręczne ustawienie dla konta cTrader jest możliwe tylko w pliku konfiguracji (patrz [`KONFIGURACJA.md`](KONFIGURACJA.md), sekcja „Prowizja”).

### Alternatywa: APEX natywny w cTrader (bez APEX Desktop)

`ApexTradingAI.algo` zawiera ten sam silnik, strategie i RiskEngine w jednym cBocie z panelem na wykresie.
Przydaje się, gdy nie chcesz uruchamiać APEX Desktop. Działa tylko z cTrader i nie ma panelu premium ani AI Decision Center.

| Parametr | Domyślnie |
|---|---|
| Ryzyko na transakcję (%) | 0,5 (maks. 2) |
| Maks. dzienna strata (%) | 1,5 |
| Maks. obsunięcie (%) | 5 |
| Maks. liczba pozycji | 3 |
| Maks. dźwignia efektywna (x) | 5 |
| Minimalna ocena okazji (0–100) | 50 |
| Instrumenty (puste = automatycznie) / Wykluczone / Maks. liczba | – / – / 25 |
| Filtr wiadomości makro / Adres kalendarza | Tak / ForexFactory JSON |
| Etykieta pozycji bota | `APEX-N` |
| Zezwól na konto LIVE / Potwierdzenie LIVE / Dozwolone konta / Dozwoleni brokerzy | Nie / – / – / – |

Nie uruchamiaj jednocześnie wersji natywnej i mostu na tym samym koncie z tą samą etykietą. Domyślne etykiety są różne (`APEX-N` i `APEX`), więc obie wersje nie dotkną nawzajem swoich pozycji, ale limity ryzyka liczą się osobno.

## MetaTrader 5

> Most MT5 **nie był kompilowany** w środowisku, w którym powstał (brak MetaEditora). Skompiluj go i przetestuj wyłącznie na DEMO.

1. W MT5: **Plik → Otwórz folder danych** → `MQL5\Experts` → skopiuj `ApexBridge.mq5`.
2. Otwórz plik w MetaEditorze (F4) i skompiluj (**F7**). Kompilacja musi zakończyć się bez błędów.
3. **Narzędzia → Opcje → Doradcy**:
   - zaznacz „Zezwalaj na handel algorytmiczny”,
   - zaznacz „Zezwalaj na WebRequest dla wymienionych URL” i dodaj `127.0.0.1`. MT5 pozwala łączyć się gniazdom tylko z adresami z tej listy; bez tego w dzienniku pojawi się błąd 4014.
4. Przeciągnij „ApexBridge” z Nawigatora na dowolny wykres. Parametry wejściowe:

| Parametr | Wartość |
|---|---|
| Klucz parowania | z panelu APEX |
| Adres APEX Desktop / Port TCP | `127.0.0.1` / `5078` |
| Zezwól na konto LIVE | **false** |
| Numer magic | 770077 |
| Etykieta APEX | `APEX` |
| Maks. subskrybowanych instrumentów | 60 |
| Maks. poślizg w punktach | 20 |

5. **Prowizja:** MT5 nie udostępnia prowizji przez API. W panelu APEX (Połączenia platform → konto MT5 → **Prowizja konta MetaTrader 5**) wybierz typ prowizji. Dla Pepperstone Razor zwykle jest to USD za lot za stronę (sprawdź u brokera). Konto Standard ma koszt w spreadzie, więc prowizja jest zerowa. **Do czasu ustawienia prowizji handel na koncie MT5 jest zablokowany.**

## Inne platformy

Każda platforma, która potrafi otworzyć WebSocket albo gniazdo TCP do 127.0.0.1, może zostać podłączona.
Wystarczy zaimplementować [protokół mostu](PROTOKOL_MOSTU.md): około 10 typów komunikatów JSON. Wzorcowe implementacje to most cTrader (C#) i most MT5 (MQL5).
Do testów bez platformy służy symulator: `dotnet run --project tools/Apex.PlatformSimulator -- --key KLUCZ` (konto DEMO z danymi syntetycznymi).

## Klucz parowania

- Generowany przy pierwszym uruchomieniu i zapisany w `apex.config.json`.
- **Połączenia platform → Nowy klucz parowania** unieważnia stary klucz. Już połączone mosty działają do rozłączenia, a nowe połączenia wymagają nowego klucza.
- Klucz chroni przed przypadkowym podłączeniem obcego procesu z tego samego komputera. Nie zastępuje ochrony komputera (antywirus, konto użytkownika).
