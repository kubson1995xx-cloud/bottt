# Task6B — pięć podłączonych widoków NAVAL COMMAND, kandydat

## Status
Kod `6c0ff8c` plus integrated test update `e87e87b`, baza `5262aa2`. **UI jest zaimplementowane; NIEODEBRANE do niezależnego review.** Pełny affected gate po authorized oczekiwaniu302 jest **845/845 PASS,0fail/skip**; parent wykonał retained verifier exit0 i potwierdził niezmienione źródła/artefakty frontendu. Poprzednie844/845 i obsolete404 pozostają wyłącznie historią. Root-firstBasic workflow i odrębne gate Windows/broker/R3F nie zostały zmienione.

## Dostawa
- React/TS/Vite, pięć widoków Command Center, Tactical Radar, Fleet Operations, Trade Animations (inspektor historycznego lifecycle), Analytics Center.
- Wspólny odczyt H snapshot/events: exact string money/counters, generation/epoch/cursor fences, checkpoint-only economics, atomic batches i resync.
- Lokalny SVG/CSS Naval2D, responsive/reduced/minimal/noaudio, oryginalny panel jako awaryjny link. Brak WebGL/R3F w tym etapie.
- Operator-only served read application; ReadOnly role nadal API-only. Żadnych nowych uprawnień/writerów lub broker commands. Sprawdzony workflow logowania zaczyna się od oryginalnego `/`: bezpośredni Basic challenge na `/naval/` nie obejmował Chrome protection space `/api/`, więc nie jest deklarowany jako działające logowanie.
- Dane jawnie historyczne/synthetic/NO_BROKER_AUTHORITY; brakujące currentquotes/broker/P&L/protection/readiness/statistics UNKNOWN. Nie wyliczać z cashdelta rentowności.

## Rzeczywiste dowody
Parent dopasował36/36 pozycji `final-proof.json` do source, lock hash i9screenshotów. Final manifest obejmuje JS/CSS/index identyczne w dist, stagedwebroot, Desktopoutput i publish. Zbudowany ekran wizualnie sprawdzony przez parenta: jawne synthetic/offline/noaccount labels, oryginalny fallback link.
- Frontend units32/32; artifactbuildtests3/3; typecheck/build PASS.
- Nowe produkcyjne hosttests5/5; rootpanel17/17; check/solutionSDKbuild0warnings/errors; publishPASS.
- FullApexTests844/845: jedyny remaining fail to stare oczekiwanie `/naval`404. Nie zastępować go twierdzeniem fullPASS; osobny authorizedtestupdate ma wykazać302+location+securityheaders i zachować Reader403/anonymous401/malformedAPI404.
- Chrome145:254 named check records przez4oddzielne runs,0failed,9screenshots finalbytes. To nie254unit tests i nie jeden uninterruptedrun. Pokazano prawdziwy producerpartial/finality/uncertain, gap/restart/conflict/hiddenpause, nativeBasiccache/rootfirst,5routes/responsive360/390/768/1440, no-JS/SVGfallback i oryginalny emergency underNaval503. Poprzednie nieudane/interruptedprobe pozostają jawnie zachowane.

## Naprawiona blokada zależności
Zwykłe offline npm zawiodło przez brak packument. Rzeczywiste cached tgz/SRI/manifests i istniejący trusted lock zostały rozpoznane, a realne npm offline lock-only pruned/reconciled graph. Clean `npm ci --offline --ignore-scripts` z exact7pins zainstalowało24packages; importReact/TS/Vitebuild potwierdziły toolchain. Brak fikcyjnych upstreammetadanych, registrynetwork/lifecyclescripts/globalinstalls. Linuxx64glibc prebuilt transitives; nie nativeWindowsproof. LockSHA256 `d0c62b763c7952bcf01491e0b66028a6a5581a70e4866da7262092594cdc5018`.

## Artefakty i bramki
- `.superpowers/sdd/2026-10-29-apex-naval/task-6b-react-command-{brief,report,review}.md`.
- `/home/ubuntu/.hermes/cache/scratch/apex-naval-task6b/dependency-recovery/` — exact commands/source maps/browser/build/publish/SRI (scratch może wygasnąć).
- Fullsuitegreen po korekcie starego testu i niezależnyreview nadal wymagane. Bez końcowego odbioru UI, R3F, Windows/native, ReadOnly UI/credentialdelivery, realuserprovisioning, privateOKX/broker/WS, remoteTLS/mobile i rentowności. Realaccount ani handlu nie podłączono.
