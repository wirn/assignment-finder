# Lokal värd, lagring och notifieringar

## Status och avgränsning

ASP.NET-värden importerar sparade JSON-annonser och lagrar filterbeslut i PostgreSQL.
En valbar webbläsarkälla och syntetisk analys-/förhandsvisningspipeline har tillkommit;
se [nya körinstruktioner](browser-and-analysis-pipeline.md). Betald AI och SMTP är
fortfarande inte inkopplade i appen. De nya databas-/API-vägarna behöver servertest.
Standardkonfigurationen gör inga Brainville-anrop. Aktiverad webbläsarkälla kan
hämta manuellt; appen gör inga betalda AI-anrop eller e-postutskick och har inget schema.
CLI-prototypen för manuell hämtning/analys finns kvar som separata kommandon.
Det är ännu inte ett sammanhängande produktionsflöde.

EF Core 10.0.11 och Npgsql EF-provider 10.0.0 är versionslåsta. En första migration
finns under `src/AssignmentFinder.Data/Migrations`. Unika nycklar för källa/ID,
revisionsnummer, analysversion och notismottagare ingår. En revision baseras på
hela den normaliserade exporten utom hämtningstid: ändrad metadata/kravtolkning
räknas som ändring även om beskrivningens äldre hash är oförändrad. Återgång till
tidigare innehåll efter en ändring ger en ny revision. Senaste filterbeslutet
uppdateras vid upprepad import; historiska filterkörningar behöver utökas senare.

Importen använder ett transaktionsbundet PostgreSQL advisory lock per källa/ID.
Ett in-process-lås och ett sessionsbundet PostgreSQL-lås hindrar överlappande
manuella importer mellan appinstanser.
Körningsstatus sparas; en misslyckad import ger Failed, inte tomt lyckat resultat.
En avbruten process kan lämna Running; nästa låshållare markerar den Interrupted.
Den nya återstartsvägen behöver servertest. Återimport av
samma inbox är avsedd att vara idempotent. Kör en appinstans under prototypfasen.

Referensprojektets EF-index, Compose, SMTP-adapter och HTML-mall har granskats.
Mönstren har anpassats utan börsdomän, nätverksadresser eller loggning av e-posttext.
Källor: [Npgsql EF 10](https://www.npgsql.org/efcore/release-notes/10.0.html),
[ASP.NET healthchecks](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0).

## Docker Compose – verifieras i testmiljö

Docker saknas i utvecklingsmiljön 2026-10-01. Imagebygge, initial migration på
PostgreSQL, app-/databasstart och readiness HTTP 200 har verifierats på målservern.
Fem isolerade PostgreSQL-kontroller passerar. Privata bind mounts, syntetisk
appimport, återimport utan ny revision och app-/databasomstart med bevarat
uppdrag och körningshistorik är verifierade. Sparat filterbeslut för syntetisk
serverimport är verifierat med korrekta plats-/omfattningsskäl.
Appimagen innehåller inte Playwright/Chromium; kontohämtning sker inte i denna image.
CV, sessionsdata och SMTP-hemligheter behövs inte för den nuvarande importvärden
och monteras inte. Privat importerad text ligger i databasen och behöver skyddad backup.

Kör kommandon från `repo/`. Vid Git-kloning ligger kodroten i klonens `repo/`,
med `compose.yaml` där. Välj serverns projektkatalog enligt dess befintliga upplägg
och dokumentera den privat. Kör `cd <projektkatalog>/repo` före Docker-kommandon.
Överför kod och exempelkonfiguration separat; befintligt privat CV, budgetjournal,
sessionsdata och analyser ska inte följa med i ett allmänt kodarkiv.
Katalogen och appdriften har ännu inte skapats/verifierats på servern.
Kopiera `.env.example` till `.env`, fyll i slumpmässigt DB-lösenord
och ADMIN_API_KEY (minst 32 tecken). Lösenordet får inte innehålla semikolon eftersom
Compose bygger anslutningssträngen. Sätt restriktiva filbehörigheter för `.env`.
Skapa `data/private/import-inbox` och kopiera önskade exportfiler dit, med namn
`brainville-*.json`. Högst 100 filer, högst 2 MB per fil, inga undermappar.
Filterfilen måste finnas som `data/private/filter-settings.json`.

Sätt APP_USER_ID och APP_GROUP_ID i lokal .env till ägarens UID/GID
(`id -u` respektive `id -g` på Linux). Containern körs då som den användaren.
Containerns användare måste kunna läsa dessa skrivskyddade bind mounts. Ordna
behörigheter för den avsedda containeranvändaren utan att öppna privata filer för
alla användare. Anpassa detta till serverns UID/GID innan verkliga data monteras.

```sh
docker compose build
docker compose up -d postgres
docker compose run --rm app --migrate
docker compose up -d app
docker compose ps
```

Migration sker uttryckligen, inte automatiskt vid vanlig appstart. Ta backup
före framtida schemaändringar. Ta inte bort `postgres_data` för att lösa migrationsfel.
Databasen exponerar ingen hostport. API binds till 127.0.0.1:8091, konfigurerbart
med APP_PORT. Exponera det inte via publikt nät utan separat åtkomstskydd.

Health: GET `/health/live` (process), GET `/health/ready` (databas och grundtabell).
Skyddade routes kräver header `X-Admin-Key` från lokal hemlighetshantering:
GET `/api/status`, POST `/api/import/run`. Ingen nyckel i URL eller kommandologg.
Felmeddelanden visar inte anslutningssträngar, annonsunderlag eller CV.
Status anger uttryckligen LocalImportOnly, AI/e-post/schema avstängda.

För lokalt .NET-start krävs `ConnectionStrings__Main` och `ADMIN_API_KEY` i miljön.
Kör `dotnet run --project src/AssignmentFinder.App -c Release --no-build` från repo.
Standardadress är 127.0.0.1:8091; Docker sätter ASPNETCORE_URLS separat.

## Isolerade PostgreSQL-kontroller

Efter Release-bygge, sätt `ASSIGNMENT_TEST_POSTGRES` via privat miljöhantering till
en disponibel testdatabas med rätt att skapa schema och kör:

```powershell
dotnet run --project tests/AssignmentFinder.ParserChecks -c Release --no-build -- --postgres
```

Testet använder bara syntetiska data, skapar ett slumpmässigt `af_test_*`-schema,
migrerar, kontrollerar upprepad/samtidig import och revisionshistorik och tar bort
just det skapade schemat. Vid processavbrott kan testschemat bli kvar. Alla fem
kontroller passerar på målservern via Compose. Containeromstart testas separat.
Vanliga lokala kontroller startar endast ett loopback-API med syntetisk konfiguration.

På en redan konfigurerad Compose-installation kan samma kontroll köras med:

```sh
docker compose --profile checks run --build --rm postgres-checks
```

Detta bygger en separat testimage med SDK och testprogrammet. Vanlig appimage
innehåller fortfarande bara runtime/app. Testet använder den konfigurerade
PostgreSQL-instansen men bara sitt slumpmässiga schema, inklusive separat
migrationshistorik. Appens schema, data och budgetjournal ändras inte.
Testcontainern tas bort när körningen avslutas. Ingen AI, SMTP eller kontohämtning.

## Notifieringsförhandsvisning

```powershell
dotnet run --project src/AssignmentFinder.Cli -c Release --no-build -- notification-demo
```

Tre syntetiska testfall ger privat HTML, text och JSON-beslut under
`data/private/notification-demo/<körning>/`. Poänggränsen 70 och IncludeReview=true
är enbart fixtureinställningar, inte användarens fastställda bevakningsgräns.
Skip, säkra filteravslag, låg poäng och passerad deadline/slutdatum stoppar notis.
Okända/motsägande datum, luckor, källvarningar och lokalt korrigerade analyser
kräver granskning. Resultatets text HTML-escapas; textalternativ och Stockholmstid
finns. Ingen adress hämtas från AI-resultatet eller annonsen.

LogOnly returnerar DryRun utan att skriva kropp eller mottagare till loggar.
GoogleSmtpEmailSender kräver separat Enabled och ConfigurationApproved, fast
godkänd mottagare, smtp.gmail.com:587 och obligatorisk STARTTLS med normal
certifikatvalidering. Adapter finns men är inte kopplad till CLI/API. Inga riktiga
utskick har gjorts. SMTP-secrets ska tillföras via miljö/secrethantering när kön
kopplas in; aldrig i appsettings, Git, Dockerfile eller CV-underlaget.

Fel sedan sändning påbörjats ger Uncertain och får inte automatiskt skickas om.
SMTP-acceptans ger Accepted med Message-ID och UTC-tid, inte garanterad leverans.
Databastabellen har mottagarhash och unik analys/mottagare samt statusfält, men
köhantering, atomisk övergång till Sending, återstart och faktisk lagring av
SMTP-resultat återstår. Aktivera inte adaptern innan detta och ett godkänt testutskick
har verifierats. Poänggräns, Review-policy och e-postkonfiguration behöver fastställas.

## Analysrättelser

Prompt `cv-match-4` skiljer kandidatens egen erfarenhet från teamets teknik,
respekterar meriterande/”gärna”, får slutdatum och kontaktminimerade källvarningar.
Appens booleska granskningsstatus skickas separat efter hashkontroll i CLI;
CV-utkastet ändras inte och behöver därför inte godkännas på nytt.
Kravrubriker för explicita svenska skallkrav och utvärderingskriterier stöds;
”Kompetenser och färdigheter” bevaras som oklassificerat. Sökmarkeringar delar
inte längre ord. Ändrad kravtolkning och källvarningar ska versionshanteras
som nytt analysunderlag. Historiska analyser gäller sina ursprungliga underlag.
Semantisk bedömning kan inte säkert ersättas med generell citatmatchning;
promptens effekt behöver utvärderas med fler manuellt märkta resultat.

## Återstående driftarbete

Målserverns distribution, arkitektur, Compose-version, resurser och projektkatalog
verifieras och dokumenteras privat. Privata importfiler/filter ligger under
kodrotens `data/private/`, databasen i egen volym. Verifiera lediga resurser
med hänsyn till andra tjänster som delar servern.
Intervall och retention behöver bestämmas.
Inga rader tas bort automatiskt innan retention är fastställd.
Compose har loggrotation. Backup/återställning, resursgränser, versionslåsta
image-digests och rollback ska verifieras i målmiljön innan bevakning aktiveras.
