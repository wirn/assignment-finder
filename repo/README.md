# Assignment Finder

Personlig konsultuppdragsbevakning med .NET, Brainville, OpenAI och Google SMTP.
Appen ska köras med PostgreSQL i en egen Linux-container via Docker Compose.
Kodroten är denna katalog (`repo/`). Se [planen](../steps.md) och
[projektinstruktionerna](../AGENTS.md) för arbetsordning och verifieringsstatus.

## Status

Lokal HTML/JSON-import, parsning, filter, manuella AI-anrop med budgetjournal,
syntetiska analys-/notisdemonstrationer samt ASP.NET-värd och databasmodell finns.
136 lokala kontroller och 12 syntetiska Chromium-sessionstester passerar.
Docker/PostgreSQL-start, SMTP-leverans och sammanhängande bevakning återstår.
API-värden importerar bara lokal JSON och filtrerar; AI, SMTP och schema är inte
inkopplade där. Läs [driftinstruktionerna](docs/local-host-and-notifications.md).

Privata data och personlig driftkonfiguration ingår inte i det publika repot.
CV, exporter, sessioner, budgetjournal och verkliga analysresultat ligger under
`data/private/`. Clone/pull överför inte dessa filer. Hemligheter tillförs separat.

## Bygg och testa

.NET SDK 10.0.401 eller senare patch inom samma SDK-band enligt global.json:

```powershell
dotnet restore AssignmentFinder.slnx --locked-mode
dotnet build AssignmentFinder.slnx -c Release --no-restore
dotnet run --project tests/AssignmentFinder.ParserChecks -c Release --no-build
```

Kontroller använder syntetiska underlag/mockad HTTP. API-testet startar endast
ett loopback-API med syntetisk konfiguration. Isolerade PostgreSQL-tester finns
som separat `--postgres`-körning enligt driftinstruktionerna; de är inte körda här.

## Lokal import

Kommandon körs från kodroten. URL:erna nedan använder exempel-ID 123456;
byt till det verkliga ID som hör till din behörigt sparade HTML-fil.

```powershell
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- parse data/private/detail.html "https://www.brainville.com/Market/RequisitionSearchResult/Details/123456" data/private/brainville-123456.json
```

Utdatafilen får inte finnas sedan tidigare. Ingen nätverkskontakt sker vid parse.
Parsern bevarar källa/ID, titel, länk, beskrivning, metadata, krav, datum och varningar.
Saknade fakta lämnas okända. Motstridiga datum/arbetsform kräver granskning.
HTML-filens översiktslänk måste matcha angivet ID och Brainville-domänen.

Generiska rubriker som Skills och Kompetenser och färdigheter ger oklassificerade
behov. Explicita skallkrav och meriterande avsnitt hålls separata. Okända rubriker
kan kräva utökad parsning; beskrivningstexten ska alltid finnas kvar.
Konsultprofiler, sidans AI-sammanfattning, formulärfält och script exkluderas.
Sökmarkeringar ska inte dela ord. Metadata/kravtolkning ingår i databasrevisionens
fingerprint även när beskrivningens texthash är oförändrad.

## Privata filter

Kopiera `config/filter.example.json` till `data/private/filter-settings.json`.
Välj tillåtna orter, distanspolicy och minsta procent. Alla tre fält krävs.
Ortmatchning är exakt; förorter läggs inte till automatiskt. Delvis distans
räknas inte som full distans. Timmar/vecka räknas inte om med antagen heltidsvecka.
Okända uppgifter ger NeedsReview; endast säkra avslag stoppar analysen.

```powershell
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- filter data/private/brainville-123456.json
```

Insamlingens sökord styr vilka annonser som hämtas och är inte obligatoriska
lokala teknikfilter. Sök-URL och användarpreferenser lagras privat.

## Lokalt CV-underlag

`scripts/prepare_cv.py` gör manuell PDF-till-text-förberedelse med pdfplumber,
pypdfium2 och Pillow. Detta är inte en del av .NET-appens drift/image.

```powershell
python scripts/prepare_cv.py "SÖKVÄG_TILL_CV.pdf" data/private/cv --candidate-name "NAMN_ATT_TA_BORT"
```

Skriptet skapar råextraktion, kontaktminimerat matchningsutkast, renderade sidor
för lokal granskning och preparation.json med hash/granskningsstatus. Parametern
`--first-page-split` är valfri och måste verifieras visuellt för dokumentets layout.
PDF-läsordning och sidfötter kan variera; återanvänd inte en annan CV-layouts värden.
Namn, telefon, e-post och annan onödig persondata ska minimeras och granskas.
Arbetsgivare/uppdragshistorik kan finnas kvar: underlaget är inte fullt anonymt.

Bekräfta utkastet genom privat reviewedByUser-status och kontrollera hash efter
varje ändring. Aktivera extern behandling först efter godkänd datahantering.
Ingen käll-PDF, råextraktion eller sessionsdata skickas av analysprovidern.

## Manuella AI-analyser

Kopiera `openai-settings.example.json` till `data/private/openai-settings.json`.
Exemplet är avstängt. Modell, priser, valutamarginal, tokens och budget valideras.
Prototypeguard begränsar total testbudget till högst 10 SEK. Budgeten delas över
körningar utan automatisk återställning; detta är inte en daglig budget.

```powershell
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- analyze data/private/brainville-123456.json
```

Standard är lokal förhandskontroll utan nätverk eller budgetreservation.
Betald körning kräver dessutom `--send`, aktiverad privat AI-konfiguration,
godkänd extern behandling i CV-manifestet och OPENAI_API_KEY i miljön.
API-nyckeln ska aldrig läggas i JSON, Git, image, kommandologg eller chatten.
Konfigurationens pricingModel måste matcha model; kontrollera leverantörspriser
innan modell/taxa ändras. Exempelpriser är beräkningsinställningar, inte ett löfte
om aktuell fakturering. OpenAI API debiteras separat från utvecklingsverktyget.

Före HTTP reserveras konservativ maxkostnad beständigt i analysis-budget.json,
med processlås och atomisk ersättning. Reservationen behålls även vid timeout/fel
eller lyckat anrop. Radera/nollställ inte journalen för att kringgå budgetstopp.
Samma underlag kan inte skickas två gånger; ändrad modell/text delar samma budget.
Inga automatiska betalda återförsök sker.

Providern använder Responses API, Structured Outputs, store:false och inga verktyg.
Kontaktminimerat CV, titel, beskrivning, krav och relevanta fakta skickas.
Källänk, företagsfält och sessions-/SMTP-hemligheter skickas inte. Beskrivningen
kan innehålla identifierande innehåll trots minimering. store:false innebär inte
ett löfte om noll leverantörslagring. Se [OpenAI:s datahantering](https://developers.openai.com/api/docs/guides/your-data).

Validerade resultat sparas privat med versioner och tokenanvändning.
Avvisade kompletta svar sparas separat under rejected-analyses och får inte bli
matchningar eller notiser. Teknisk validering bevisar inte semantisk kravuppfyllelse.
Prompt cv-match-4 skiljer egen erfarenhet från teamets teknik, respekterar
meriterande/”gärna” och får källvarningar, slutdatum och appens granskningsstatus.
Effekten behöver fortsatt manuell kvalitetsutvärdering.

`analysis-recheck <uppdrags-JSON> <avvisat-svar> [citatkorrigeringar]` kontrollerar
sparade svar lokalt utan nytt anrop. Citatkorrigeringar ska matcha originalcitatet
och verifieras mot samma källversion. Ändra inte poäng/erfarenhet för att få ett
resultat godkänt. Original bevaras; omkontroller sparas separat i rechecked-analyses.
En korrigerad Apply sänks till Review. Resultatet kräver semantisk granskning.

## Syntetiska demonstrationer

```powershell
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- analysis-demo
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- notification-demo
```

Mock är en fixture-provider, inte en verklig CV-bedömning. Notisdemonstrationen
skapar svensk HTML/text och beslut under data/private/notification-demo.
Demo-gränsen 70 och IncludeReview=true är testinställningar. Inga utskick görs.

## Playwright-prototyp

Klarlägg Brainvilles tillåtelse för avsedd automatisering och serverlagring före
kontoanrop. Respektera åtkomstbegränsningar, CAPTCHA och MFA. Market API/export
ska utvärderas utifrån det egna kontots tillgång; se [förstudien](docs/brainville-integration.md).

```powershell
dotnet build AssignmentFinder.slnx -c Release
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path (Get-Location) 'data/tools/playwright'
pwsh src/AssignmentFinder.Cli/bin/Release/net10.0/playwright.ps1 install chromium
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- browser-check
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- session-check
```

Playwright 1.58.0 är låst. Lokala browser/session-check använder bara syntetiska
sidor utan sparad session eller kontoanrop. Verklig sessionsutgång är ännu overifierad.
Efter klarlagd tillåtelse, sätt BRAINVILLE_BROWSER_ACCESS_CONFIRMED=true och kör:

```powershell
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- login "https://www.brainville.com/Market/RequisitionSearchResult/Details/123456"
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- fetch "https://www.brainville.com/Market/RequisitionSearchResult/Details/123456"
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- fetch-list data/private/brainville-search-url.txt --pages 2
```

Logga in och hantera MFA/CAPTCHA själv i öppnad Chromium, öppna angivet uppdrag
och tryck Enter i terminalen. Detaljsidan verifieras före sessionslagring.
Sessionen innehåller känsliga cookies/local storage/IndexedDB; dela den inte.
Session storage ingår inte. Verklig återinloggning och serveröverföring återstår.

Fetch tillåter högst tre detaljlänkar; fetch-list tillåter 1–3 sidor och hämtar högst
tre unika detaljer med fem sekunders paus. Nästa sidlänk behåller domän, sidnummer
 och sökfilter. Rekommenderade/liknande uppdrag exkluderas. Begränsad insamling
markerar inte missade uppdrag som avslutade. Tomma resultat är overifierade fel.
Utloggning ger login-meddelande; 403 och strukturfel hålls separata.

Exporter och filterbeslut skrivs privat till nya körningsmappar under imports.
Sessionen ligger i data/private/brainville-session.json. Linux-utdata skapas med
filmode 600/katalogmode 700; på Windows används ärvda ACL:er. Se till att endast
avsedda konton kan läsa privata data. Det publika Git-repot innehåller ingen session.
