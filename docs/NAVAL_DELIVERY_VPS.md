# Naval Task8A — lokalna paczka Linux i przyszły VPS

## Co faktycznie dostarczamy

`linux-x64`, glibc, framework-dependent net8: **lokalny pasywny host historycznej publikacji**, nie operacyjny bot OKX, usługa VPS ani broker execution. `NO_BROKER_AUTHORITY / HISTORICAL_OFFLINE_CHECKPOINT / SYNTHETIC_NOT_RESEARCH`. Dane historyczne i DurableMode RUNNING nie są aktualnym saldem, gotowością ani zgodą START. Nie ma kont, konfiguracji, kluczy, publication ani authority w ZIP. NavalRead domyślnie jest absent/null albo Enabled=false. `/` zachowuje stary panel i lokalny legacy token bootstrap; to nie jest odizolowany portal ReadOnly. `/naval`, `/naval/` i `/api/naval/snapshot` domyślnie nie istnieją; jawny URL pliku statycznego może być dostępny przez static middleware.

Runtime wymaga zainstalowanego **Microsoft.AspNetCore.App 8** i dotnet x64. Odbiorca nie potrzebuje Node/npm, SDK ani Pythona. Node26/npm11, SDK8, PowerShell i Python stdlib są narzędziami wykonawcy budowy/testów, nie zależnościami działającej aplikacji. Nie obiecuje się ARM/musl ani identycznej sumy ZIP przy kolejnej kompilacji.

## Integralność i uruchomienie w foreground

`Dokumentacja/THIRD_PARTY_NAVAL.txt` zawiera pełne notice runtime closure. Wyjątek dotyczy wyłącznie genuine `@react-three/fiber@9.6.1`, którego npm artifact nie zawiera LICENSE: tekst pochodzi z oficjalnego repozytorium przy npm `gitHead` `2a528745e9aa7c9e6cca41e404b59d45cf0d0cc7`, jest jawnie oznaczony jako upstream, nie npm-shipped. Builder offline wymaga dokładnego SHA manifestu, provenance oraz 1072-bajtowego LICENSE; inna wersja, zmieniony plik/provenance lub brak notice innej zależności blokują paczkę. To zapis pochodzenia i warunków upstream, nie opinia prawna.

1. Otrzymaj ZIP i `.zip.sha256` z zaufanego kanału. Porównaj `sha256sum "APEX Linux Naval.zip"` z przekazaną sumą; SHA nie jest podpisem wydawcy, uprawnieniem brokera ani dowodem rentowności.
2. Weryfikuj ZIP przed ekstrakcją (CRC/dekompresja, bez duplikatów/case aliases, ścieżek absolutnych/traversal, linków i bomb dekompresyjnych). Rozpakuj do **nowego wersjonowanego** folderu, nie nadpisuj istniejącej wersji ani danych. W katalogu paczki `sha256sum -c manifest.sha256` sprawdza pliki; kompletny verifier PowerShell dodatkowo wymaga dokładnego zbioru, nested `naval-build-v1`, exclusions oraz `apex-build-v2`/real TRX/source map.
3. Sprawdź `apex-build.json`: `target=linux-x64`, `deploymentMode=framework-dependent`, `requiredRuntime=Microsoft.AspNetCore.App 8`, `sourceCommit`, `sourceDirty`, `sourceInputsSha256`, lock/asset hashes. `Weryfikacja/source-inputs.json` zawiera tylko nazwy względne, długości i SHA źródeł. Dirty=true oznacza engineering candidate, nie clean release. Raport zewnętrzny wiąże CRC, extracted package i smoke z SHA ZIP; `windowsRuntimeTested=false`, `brokerTested=false` pozostają w zapieczętowanym ZIP po testach.
4. Administrator przygotowuje osobny prywatny bezwzględny katalog danych poza paczką. Nie resetuj, nie kopiuj testowego authority ani nie usuwaj istniejącego safety state. Przykładowy kształt polecenia (zastąp ścieżki własnymi, bez sekretów):

```sh
dotnet /wersje/apex-v1/APEX-Desktop/ApexDesktop.dll --data /prywatne/apex-dane --no-browser
# Alternatywnie z katalogu paczki; launcher wymaga istniejącego katalogu zewnętrznego:
APEX_DATA_DIR=/prywatne/apex-dane sh "Uruchom APEX.sh"
```

`sh` jest jawne: ZIP może nie zachować executable bit. Host wiąże wyłącznie IPv4 loopback. Dokładny Host to `127.0.0.1:<port>` albo `localhost:<port>`. News wyłączono w testach, ale paczka nie zawiera użytkowego configu. TCP bridge jest osobny, a `BridgeTcpPort=0` wybiera port ephemeral, nie wyłącza TCP. Nie łącz żadnej realnej platformy w lokalnym smoke.

Zakończenie foreground przez SIGTERM/Ctrl-C jest zamknięciem procesu, **nie Emergency ani awaryjną likwidacją**. Zamknięcie karty przeglądarki również nie jest STOP. STOP, Emergency/full-close i stan pozycji wymagają starego panelu Operatora oraz rzeczywistego uzgodnienia; nie zakładaj, że zamknięcie serwera zamyka pozycje. Restart/reconnect nie nadaje START.

## Opcjonalny prywatny odczyt — wyłącznie ręcznie

Pełny dokładny schemat znajduje się w [NAVAL_READ_PUBLICATION.md](NAVAL_READ_PUBLICATION.md). Administrator poza paczką konfiguruje `HostConfig.NavalRead`: `Enabled`, `PublicationPath`, `CredentialFilePath`, `Scope.{Id,PublicationScopeId,PortfolioId,ConfigDigest,Accounts}`. `PublicationPath=null` oznacza NOT_CONFIGURED z null ekonomią, nie zero środków. Błędny/brakujący wskazany plik daje503 UNKNOWN, bez last-good money. Host nie otwiera ani nie migruje authority.

Osobna prywatna mapa `naval-local-access-v1`: przynajmniej Operator i ReadOnly, unikalne ID/digesty, dokładnie jeden grant do właściwego scope, lowercase SHA256. Sekret to kanoniczny unpadded base64url z32 losowych bajtów (43 ASCII), pozyskany prywatnym managerem/narzędziem — nigdy chat, CLI argv, URL, HTML, JS, trace, log ani przykład wartości w paczce. Linux: katalog0700, credential0600, publication0400/0600, bez linków i słabych ancestor permissions. Windows wymaga realnej kontroli owner/DACL/dziedziczenia dla current-user/SYSTEM/Administrators; instrukcja nie zastępuje natywnego testu.

Operator używa natywnego Basic **najpierw na `/`**, następnie `/naval/`. Brak JS auth injection lub token fallbacku. ReadOnly Bearer ma wyłącznie dokładne GET `/api/naval/snapshot` i `/api/naval/events`; dokumenty/moduły/style/stare API pozostają403, anonymous401. Operator Bearer ani ReadOnly Basic nie eskalują. Scena/SVG/listy są wyłącznie historyczne; tekstowy link AWARYJNY STOP otwiera stary panel, sam nie wydaje polecenia. Awaria H503 nie może wyłączyć starej ścieżki Emergency.

## VPS/systemd — tylko niewdrożone, niezweryfikowane zalecenia

Task8A **nie instaluje usługi**, nie tworzy użytkownika ani nie zmienia konfiguracji hosta. Przyszły projekt systemd wymaga osobno zatwierdzonego wdrożenia: dedykowany non-root user, niezmienna wersjonowana aplikacja, prywatne zewnętrzne katalogi danych/credential/publication, `UMask=0077`, jawne ścieżki ExecStart i --data, osobna retencja safety state oraz test crash/power-loss/restart. Restart supervision restartuje proces, nie daje START i nie dowodzi broker reconciliation. Nie uruchamiaj tu systemctl ani automatycznego setupu.

Telefon/zdalny Windows pozostają **zablokowane** do osobno zaprojektowanego i zatwierdzonego prywatnego tunelu/TLS/gateway/role acceptance. Nie wystawiaj publicznie HTTP5077, TCP5078 ani `/bridge`, nie przesyłaj Basic publicznym HTTP, nie ufaj forwarded user/role headers. Dokładny Host/same-origin/CSRF mogą odrzucić naiwny reverse proxy; zgodność pozostaje nierozwiązana, nie należy luzować kontroli. API reader credential nie daje UI readerowi.

cTrader Desktop + Bridge nadal muszą działać na tym samym lokalnym Windows; cTrader Cloud/Linux VPS ich nie zastępuje. cBoty w Linux ZIP są wyłącznie towarzyszącymi artefaktami dla Windows. Native pozostaje read/reduction only; MT5 source-only, nieodebrany. Limity CFD0,5%/1,5%/DD5%/x5, SL/TP, duplicate barriers, Emergency i REAL/Crypto refusal pozostają. Naval cash SPOT≤1x nie usuwa tych odmów. Prywatny OKX DEMO, WS/history/finality/protection/settlement, prawdziwe credentials, Windows11/ACL/browser/GPU/telefon i zatwierdzona osobno transakcja DEMO nadal wymagają odbioru. Brak LIVE, AI Lab, LLM decisions/updatera lub obietnic zysku.
