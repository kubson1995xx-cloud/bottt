# AI & Auto-Updates (moduł APEX AI Lab)

> **Stan (2026-10-18): moduł odłożony decyzją użytkownika.** Nie wchodzi w skład pakietu ani panelu APEX Desktop.
> Opis poniżej dotyczy projektu modułu i obowiązuje dopiero po jego wznowieniu. Zaległości: [`AI_LAB_ZALEGLOSCI.md`](AI_LAB_ZALEGLOSCI.md).

AI Lab automatycznie analizuje skuteczność strategii i warunki rynkowe. Okresowo uruchamia agenta AI (Codex lub Claude), który przygotowuje ulepszenia.
Każdą zmianę AI Lab sam sprawdza: audyt, kompilacja, testy regresyjne, backtest, opcjonalny przegląd drugim modelem.
Historia zmian i postęp pracy agentów są widoczne w panelu APEX (zakładka **AI & Auto-Updates**).

> **Zasada nadrzędna:** analizy i przygotowanie zmian są w pełni automatyczne, ale **wdrożenie do działającego bota wymaga Twojej akceptacji**.
> Zatwierdzenie buduje nowe wydanie, które zaczyna działać dopiero po ponownym uruchomieniu APEX przez Ciebie.
> RiskEngine, limity bezpieczeństwa i aktywne strategie wymagają dodatkowej, wpisanej frazy. Konto REAL pozostaje zablokowane.

## Architektura i izolacja

```
APEX Desktop (handel)  ──tylko odczyt: status, dziennik, reżimy──►  APEX AI Lab (osobny proces, 127.0.0.1:5079)
   zakładka AI & Auto-Updates  ◄──pośredniczy w zapytaniach panelu──┘        │
                                                                             ├─ analiza (bez AI)
                                                                             ├─ agent AI w kopii roboczej git (gałąź ai/<id>)
                                                                             ├─ audyt · kompilacja · testy · backtest · przegląd
                                                                             └─ wydania (po akceptacji) → %LOCALAPPDATA%\APEX Trading AI\wydania
```

- **Osobny proces.** Zatrzymanie, awaria lub zawieszenie AI Lab nie wpływa na handel. APEX Desktop tylko przekazuje zapytania panelu i na żądanie uruchamia AI Lab.
- **Tylko odczyt.** AI Lab czyta z APEX Desktop status kont, dziennik transakcji i reżimy rynku. Nie wysyła żadnych poleceń handlowych.
- **Izolowana kopia.** AI Lab ma własną kopię repozytorium (`ailab\repo`), a każde zadanie – osobną kopię roboczą i gałąź `ai/<id>`. Twoje repozytorium źródłowe nie jest zmieniane.
  Wyjątek: przycisk „Dodaj gałąź do repozytorium źródłowego”, który dodaje tylko gałąź.
- **Piaskownica agenta.**
  - Codex pracuje z `--sandbox workspace-write`: zapis wyłącznie w kopii roboczej, domyślnie bez sieci.
  - Claude Code ma ograniczony zestaw narzędzi: pliki, kompilacja i testy .NET, podgląd git; bez WebFetch/WebSearch.
  - Dostawcy API nie wykonują żadnych poleceń: zwracają pełne pliki, a AI Lab sprawdza ścieżki i sam je zapisuje.
- **AI nie może zmienić własnych zabezpieczeń.** Zmiany w `src/Apex.AiLab/`, `tools/windows/` i `.git/` są zawsze odrzucane.

## Połączenia AI

| Dostawca | Logowanie | Rozliczenie |
|---|---|---|
| **Codex (ChatGPT)** – zalecany | `codex login` – konto ChatGPT (Plus/Pro/Business…), **bez klucza API**; alternatywnie kod urządzenia albo klucz OpenAI API | limity planu ChatGPT (okna godzinowe i tygodniowe) |
| **Claude Code** | `claude auth login` – subskrypcja Claude; albo Anthropic Console | limity subskrypcji albo tokeny API |
| **OpenAI API** | klucz z platform.openai.com | za tokeny |
| **Anthropic API** | klucz z console.anthropic.com | za tokeny |

Instalacja narzędzi (jednorazowo, Windows):

```powershell
winget install OpenJS.NodeJS.LTS      # wymagane dla Codex CLI
npm install -g @openai/codex          # Codex CLI
npm install -g @anthropic-ai/claude-code   # opcjonalnie Claude Code
winget install Git.Git                # git (wymagany)
winget install Microsoft.DotNet.SDK.8 # .NET 8 SDK (kompilacja, testy, wydania)
```

W panelu: **AI & Auto-Updates → Połączenia AI**.
- **Zaloguj kontem ChatGPT** otwiera okno logowania Codex: zaloguj się w przeglądarce i kliknij „Sprawdź ponownie”.
- **Logowanie kodem urządzenia** pokazuje adres i kod w panelu. Ta metoda może wymagać włączenia logowania kodem w ustawieniach zabezpieczeń ChatGPT.
- Klucze OpenAI/Anthropic API są szyfrowane Windows DPAPI (tylko Twoje konto Windows) i nigdy nie są wyświetlane w całości.
- Klucz dla Codex jest przekazywany do Codex CLI, który przechowuje go sam.

**Role:**
- *główny dostawca*;
- *zapasowy*, gdy limit głównego się wyczerpie, a kopia robocza jest jeszcze nietknięta;
- *przegląd* – drugi model ocenia każdą propozycję i zużywa dodatkowy limit.

**Modele:**
- dla Codex i Claude Code puste pole oznacza model domyślny narzędzia (zalecane);
- dla API model jest wymagany;
- podpowiedzi w panelu pochodzą z listy narzędzia; ich dostępność zależy od Twojego planu.

## Konfiguracja (zakładka „Repozytorium i wydania”)

| Ustawienie | Opis |
|---|---|
| Repozytorium źródłowe | Folder z kodem APEX (z katalogiem `.git`), np. `C:\APEX\zrodla`. AI Lab bierze ostatni **commit** wskazanej gałęzi – niezatwierdzone zmiany robocze nie są uwzględniane. |
| Gałąź bazowa | Puste = bieżąca gałąź repozytorium. |
| Dane do backtestu | Folder z plikami `*_H1.csv`, `*_M15.csv` (przygotowanie: `tools/data/prepare_data.py`). **Bez danych propozycje dostają status „Wymaga uwagi”.** |
| Limity czasu | Kompilacja, testy, backtest. |

## Harmonogram, wyzwalacze, limity

- **Harmonogram:** wyłączony / co N godzin / codziennie o HH:MM / co tydzień. Przy każdym terminie wykonywana jest analiza. Agent uruchamia się tylko wtedy, gdy analiza wykryje problem. To ustawienie domyślne, które oszczędza limity; można je zmienić.
- **Wyzwalacze** (sprawdzane co godzinę, przy włączonej automatyzacji):
  - pogorszenie wyników strategii na żywo – górna granica 95% średniego R poniżej zera albo wynik wyraźnie poniżej backtestu;
  - wstrzymanie strategii przez monitor wyników;
  - zmiana warunków rynkowych – ≥ 30% instrumentów zmieniło rodzinę reżimu D1 względem ostatnich 5–30 dni albo mediana percentyla ATR wzrosła o ≥ 30 pp;
  - powtarzające się błędy wykonania.
- **Migawki rynku:** co godzinę (ustawiane) AI Lab zapisuje reżimy D1 skanowanych instrumentów. Wykrywanie zmian działa po zebraniu ≥ 5 dni historii.
- **Limity:**
  - uruchomienia agenta dziennie i tygodniowo;
  - tokeny dziennie;
  - koszt API miesięcznie (wymaga wpisania cen z cennika dostawcy);
  - minimalny odstęp między automatycznymi uruchomieniami.

  Gdy narzędzie zgłosi wyczerpanie limitu subskrypcji („usage limit”), AI Lab wstrzymuje tego dostawcę do czasu odnowienia, jeśli da się go odczytać, a w przeciwnym razie na 1 godzinę. Wtedy może przejść na dostawcę zapasowego.
  Bieżące zużycie, wykres tokenów z 14 dni i aktywne blokady są w zakładce „Harmonogram i limity”.

## Przebieg zadania

| Etap | Co się dzieje |
|---|---|
| Analiza | Raport: strategie na żywo (n, E[R], górna granica, backtest OOS), wnioski, rynek, błędy. |
| Izolowana kopia | Aktualizacja kopii laboratorium, nowa kopia robocza na gałęzi `ai/<id>`, przywrócenie pakietów NuGet. |
| Wersja bazowa | Kompilacja, wszystkie testy i backtest portfela wersji obecnej – liczone raz na commit. |
| Agent AI | Agent dostaje raport i zasady (`.ailab/ZADANIE.md`). Postęp – polecenia, pliki, plan, komunikaty – widać na żywo w panelu. |
| Audyt | Reguły deterministyczne (niżej). |
| Kompilacja | Silnik, APEX Desktop, testy, backtester, oba cBoty cTrader. |
| Testy | Pełny zestaw (jednostkowe, silnika, integracyjne na prawdziwym procesie APEX Desktop). Muszą przejść wszystkie, a liczba testów nie może spaść. |
| Backtest | Portfel (okres badawczy 2005–2017 i odłożony 2018–2020.05, ocena ≥ 50) – propozycja vs wersja bazowa. **Gorszy wynik = odrzucenie.** |
| Przegląd | Opcjonalnie drugi model, tylko do odczytu. |

**Statusy:**
- Gotowa do decyzji;
- Wymaga uwagi – weryfikacja niepełna, np. bez backtestu;
- Odrzucona przez kontrole;
- Bez zmian – agent uznał, że dane nie uzasadniają zmiany;
- Tylko analiza;
- Wstrzymana – limity;
- Wdrożona;
- Odrzucona przez Ciebie;
- Wycofana.

### Reguły audytu

| Waga | Reguła |
|---|---|
| **Blokująca** | zmiana w `src/Apex.AiLab/`, `tools/windows/`, `.git/`, `.github/` |
| **Blokująca** | dodanie lub usunięcie kodu dotyczącego dopuszczenia konta LIVE (`LiveTradingFeature`, `AllowLive`, `LiveConfirmation`, `RequiredLivePhrase`, `AccountGuard.Evaluate`) |
| **Blokująca** | odwołanie do `AssetClass.Crypto`, martingale, grid, uśrednianie, podwajanie wolumenu, `AccessRights.FullAccess` |
| **Blokująca** | sieć lub procesy w silniku (`src/Apex.Core`), `Skip` w teście, usunięcie testu, mniej testów niż w wersji bazowej |
| **Chroniona** (wymaga frazy `ZATWIERDZAM ZMIANY CHRONIONE`) | `Risk/`, `Safety/`, `Execution/`, `TradingEngine.cs`, `EngineConfig.cs`, profil strategii, `StrategyBook`, `HostConfig.cs`, `AccountRuntime.cs`, mosty i cBoty, **pliki strategii aktywnych w profilu** |
| Ostrzeżenie | zmiana kodu bez testów, duża zmiana (> 2000 linii), usunięcie pliku |

Lista chroniona może być w pliku `ailab.config.json` rozszerzona, ale nie zawężona poniżej wartości domyślnych. Panel nie pozwala zmieniać tych zasad.

## Decyzja i wdrożenie

1. **Propozycje i historia zmian** → wybierz propozycję. Masz do dyspozycji podsumowanie agenta, audyt, testy, porównanie backtestu, przegląd i pełny diff.
2. **Zatwierdź i przygotuj wdrożenie.**
   - Dla zmian chronionych wpisz `ZATWIERDZAM ZMIANY CHRONIONE`.
   - Dla niepełnej weryfikacji wpisz `ZATWIERDZAM MIMO OSTRZEŻEŃ`.
3. AI Lab łączy zmianę z gałęzią `apex-wdrozone` (w swojej kopii) i buduje nowe wydanie w `%LOCALAPPDATA%\APEX Trading AI\wydania\<wersja>`.
   Gdy zmiana dotyczy silnika lub mostów, w wydaniu znajdą się też nowe pliki `.algo`.
4. **Działający APEX nie jest zmieniany.** Zamknij APEX Desktop i uruchom go ponownie skrótem **Uruchom APEX.cmd** – skrót uruchamia aktywne wydanie.
   - Pozycje są chronione przez SL u brokera.
   - Stan silnika (rejestr sygnałów, plany pozycji, limity dnia) jest wczytywany z dysku.
5. **Przywróć poprzednią wersję** (zakładka „Repozytorium i wydania”) wskazuje poprzednie wydanie albo instalację pierwotną. Również działa po ponownym uruchomieniu.

Inne opcje:
- **Eksportuj łatkę** zapisuje plik `.patch`;
- **Dodaj gałąź do repozytorium źródłowego** pozwala przejrzeć zmianę w swoim repozytorium.

## Uczciwość statystyczna

- Każda zmiana oceniana na tych samych danych historycznych zwiększa ryzyko **dopasowania do historii**.
  AI Lab liczy, ile razy okres odłożony (2018–2020.05) był użyty do oceny zmian, i pokazuje ten licznik przy każdej propozycji.
- Lepszy backtest **nie jest dowodem** przyszłej skuteczności. Agent ma zakaz nadawania statusu `Validated` i dopasowywania parametrów do okresu odłożonego.
- Zalecane: zmiany strategii akceptuj rzadko, obserwuj je na DEMO i porównuj z dziennikiem na żywo.

## Rozwiązywanie problemów

| Objaw | Rozwiązanie |
|---|---|
| „Moduł AI Lab nie działa” | Kliknij **Uruchom moduł AI**. Gdy brak `ApexAiLab.exe` – zbuduj pakiet `Zbuduj-APEX.ps1`. |
| Codex „nie zainstalowano” | `npm install -g @openai/codex`, potem „Sprawdź ponownie”. Ewentualnie wpisz ścieżkę programu. |
| „Wersja bazowa się nie kompiluje” | Napraw repozytorium źródłowe (AI Lab weryfikuje względem ostatniego commitu). |
| Status „Wymaga uwagi” – backtest pominięty | Ustaw folder danych do backtestu. |
| „Wstrzymana – limity” | Sprawdź „Harmonogram i limity”: limit dzienny, tygodniowy, tokenów, kosztów albo limit subskrypcji. |
| Konflikt przy wdrożeniu | Odrzuć propozycję i uruchom agenta ponownie na aktualnej bazie. |

## Pliki (`%LOCALAPPDATA%\APEX Trading AI\ailab\`)

| Plik / folder | Zawartość |
|---|---|
| `ailab.config.json` | ustawienia (token, dostawcy, harmonogram, limity, zasady; klucze API zaszyfrowane) |
| `repo\`, `kopie\`, `wdrozenie\` | kopia laboratorium, kopie robocze zadań (najstarsze usuwane), kopia wdrożeń |
| `zadania\<id>\` | zadanie: `zadanie.json`, `zdarzenia.jsonl`, `zadanie.md`, `zmiana.diff`, wyniki testów i backtestu |
| `analizy\`, `rynek.json`, `zuzycie.json`, `bazowe\` | raporty analiz, migawki rynku, zużycie i limity, wyniki wersji bazowych |
| `..\wydania\` | wydania, `historia.json`, `aktualne.txt` (aktywne wydanie dla skrótu startowego) |
