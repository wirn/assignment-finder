# Manuell hämtning och syntetisk analyspipeline

Webbläsarkällan är separat från lagring och filter. Ett framtida API kan implementera
IAssignmentSource utan att ersätta resten av flödet. Kontohämtning är avstängd i
exempelkonfigurationen. Ingen schemaläggning eller automatisk analys är inkopplad.

## Verifiera databas och API

Från kodroten på en konfigurerad server:

```sh
git pull --ff-only
docker compose --profile checks run --build --rm postgres-checks
```

Kontrollerna använder ett tillfälligt testschema. De testar nu även samtidig
analyslagring, deduplicerade förhandsvisningar, ersatta revisioner och appens
syntetiska demoflöde via ett tillfälligt loopback-API. Inga konto-, AI- eller SMTP-anrop.
De tidigare fem lagringskontrollerna är serververifierade; tilläggen behöver
verifieras på målservern innan checklistans nya lagringsdelar kryssas av.

## Docker med Chromium

Den vanliga appimagen innehåller Playwright-biblioteket men saknar Chromium.
En valbar browser-app-target installerar Chromium och dess Linux-beroenden
via den låsta Playwright-versionens installationsprogram.

```sh
umask 077
mkdir -p data/private/browser-session
test -f data/private/browser-settings.json || cp browser-settings.example.json data/private/browser-settings.json
docker compose -f compose.yaml -f compose.browser.yaml build app
docker compose -f compose.yaml -f compose.browser.yaml run --rm --no-deps app --browser-check
```

Det sista kommandot läser bara lokal HTML och behöver ingen inloggning.
Docker-varianten behöver verifieras på målservern. Lokal Chromium-start i appen
är verifierad på utvecklingsdatorn med absolut PLAYWRIGHT_BROWSERS_PATH.
Se [Playwrights installation](https://playwright.dev/dotnet/docs/intro) och
[Docker-dokumentation](https://playwright.dev/dotnet/docs/docker).

## Privat session och sökningar

Använd CLI:s manuella login på en dator med grafisk webbläsare. Programmet
hanterar ingen lösenordsfil och kringgår inga åtkomstkontroller. Klarlägg
kontots åtkomstförutsättningar och sätt BRAINVILLE_BROWSER_ACCESS_CONFIRMED
enligt den privata driftskonfigurationen. Sessionen sparas under data/private.
Överför endast sessionsfilen genom en privat kanal till
data/private/browser-session/brainville-session.json; sessionsdata är hemligheter.
Sessionsportabilitet måste kontrolleras vid första verkliga serverhämtningen.

I browser-settings.json ställs sök-URL:er, enabled och hämtningsgränser in.
Sök-URL:erna bör komma från kontots verkliga sökningar. Den publika frontend-URL:en
är ett exempel och ersätter inte användarens fastställda privata sökord.
Tillåtna gränser: 1–5 sökningar, 1–3 sidor per sökning, högst 60 detaljer och
minst fem sekunder mellan sidbesök. Standard är 20 detaljer och en sida.
En körning avbryts efter tio minuter. Uppdrag dedupliceras mellan sökningar.
En begränsad hämtning markerar aldrig ej observerade uppdrag som avslutade.
Tomma listor behandlas tills vidare som strukturfel; verkliga tomresultat återstår.

När konfiguration och session finns och källan uttryckligen aktiverats:

```sh
docker compose -f compose.yaml -f compose.browser.yaml up -d app
awk -F= '$1=="ADMIN_API_KEY" {print "X-Admin-Key: "$2}' .env |
  curl --fail-with-body --silent --show-error --header @- --request POST http://127.0.0.1:8091/api/brainville/run
```

Alla uppdrag lagras genom samma AssignmentStore som lokal import. Källstatus
NeedsLogin, AccessDenied, BrowserUnavailable, Disabled, TimedOut eller Failed
kräver kontroll; programmet gör inga blinda återförsök. Alla importvägar delar
ett processlås och ett PostgreSQL-lås. Vid nästa körning markerar låshållaren
övergivna Running-körningar Interrupted och återimport kan göras idempotent.

## Syntetiskt flöde till förhandsvisning

POST /api/pipeline/demo importerar endast det hårdkodade syntetiska demo-1,
validerar en Mock-fixture, lagrar analysen och skapar en Preview-notis. Det är
inte en faktisk CV-bedömning. Mottagaren synthetic@example.invalid och gränsen
70 hör endast till demonstrationen. Inga nätverksanrop eller utskick sker.

```sh
awk -F= '$1=="ADMIN_API_KEY" {print "X-Admin-Key: "$2}' .env |
  curl --fail-with-body --silent --show-error --header @- --request POST http://127.0.0.1:8091/api/pipeline/demo
```

GET /api/previews/{previewId} returnerar svensk text/HTML och granskningsbehov.
GET /api/assignments returnerar högst 100 senaste uppdrag med filterbeslut.
GET /api/analyses returnerar högst 100 analysers versionsmetadata.
Alla dessa API-anrop kräver X-Admin-Key. HTML-källtext escapes vid rendering.
Uppdrag och förhandsvisningar kan innehålla privata uppgifter; håll dem på
loopback och använd vid behov SSH-tunnel. Ingen publik dashboard finns ännu.

AnalysisStore validerar CV-/uppdragsversion och belägg före lagring, samt kräver
granskat CV-underlag. Mock-resultat tillåts endast för Synthetic. Databasen
lagrar profilhash och analysbelägg, inte hela CV-filen. Analysnyckeln binder
revision, profil, filterhash, promptversion och modell. Förhandsvisningar har
unik analys/mottagarhash och stabilt Message-ID. Ersatta revisioner eller
ändrade filter stoppas; befintliga notisstatusar återställs aldrig till Preview.

Verkliga AI-anrop, personlig notispolicy, beständig sändkö, SMTP och schema
är ännu inte inkopplade i appen. Befintliga CLI-kommandon och budgetkrav gäller
fortfarande separat. Budgetjournalen får inte nollställas eller ersättas vid flytt.
