# Implementationsplan – Assignment Finder

Publik teknisk status. Kontouppgifter, serveradresser, CV-underlag, verkliga
uppdragsexporter och kandidatbedömningar dokumenteras endast privat under
`repo/data/private/`. Kodroten är `repo/`.

## Mål och arkitektur

Personlig uppdragsbevakare med .NET 10, ASP.NET Core, EF Core/PostgreSQL,
OpenAI och Google SMTP. Brainville är första källan; fler källor ansluts via
ett gemensamt uppdragsformat och IAssignmentSource. Drift sker med Docker Compose.

Flöde: hämtning → normalisering/lagring → lokala filter → CV-analys →
notifieringsbeslut → beständig kö → SMTP. Inga automatiska ansökningar.
Okända fakta kräver granskning och ger inte automatiskt avslag.
Filter, intervall, modell, budget och mottagare konfigureras privat.

## Aktuell verifiering

- Release-bygge utan varningar och låst paketrestore har verifierats.
- 136 lokala parser-, filter-, analys-, notis-, modell- och API-kontroller passerar.
- 12 syntetiska Chromium-sessionstester passerar utan kontoanrop eller sparad session.
- Begränsad insamling och manuella AI-anrop har verifierats separat.
  Rådata och kandidatbedömningar ingår inte i detta repo.
- API-värden importerar endast sparade JSON-filer till PostgreSQL och filtrerar.
  AI, SMTP och schema är inte inkopplade där.
- Docker/PostgreSQL-start, faktisk lagring/omstart och SMTP-leverans återstår.
  Docker/PostgreSQL saknas i den lokala utvecklingsmiljön.

Kryssa av först efter verifiering av respektive färdigkriterium. Delar av ett
separat referensprojekt har granskats för analysgränssnitt, databas, SMTP och
Compose; dess domän och privata konfiguration återanvänds inte.

## Steg 1 – Förutsättningar

- [x] Välj .NET 10 och lås SDK/paket för prototypen.
- [x] Dokumentera målserverns förutsättningar i privat driftunderlag.
- [x] Fastställ privat CV-underlag och lokala filter för prototypen.
- [x] Inför total testbudget och villkor för externa AI-anrop.
- [ ] Fastställ notisgräns, Review-policy, mottagare och SMTP-konfiguration.
- [ ] Fastställ intervall, retention, lagringsbehörigheter och resursgränser.

Färdigt när driftförutsättningar och användarens kriterier är dokumenterade privat.

## Steg 2 – Brainville-prototyp

- [ ] Klarlägg åtkomstvillkor för automatisering, extern analys och serverlagring.
- [x] Utvärdera API/export och dokumentera förutsättningar för val av hämtning.
- [x] Implementera begränsad Playwright-prototyp för manuell login och fetch.
- [x] Implementera detaljparser, listparser och begränsad paginering.
- [x] Verifiera lokal parsning och insamling; privata underlag versionshanteras inte.
- [x] Detektera login, HTTP 401/403 och ändrad struktur med syntetiska tester.
- [ ] Verifiera verklig sessionsutgång, eventuell MFA och återinloggning på servern.
- [ ] Verifiera verkliga tomma resultat och serverportabilitet.

Färdigt när tillåten hämtning fungerar och inloggningsfel skiljs från tomma resultat.

## Steg 3 – App och Docker

- [x] Skapa solution med app, integration, analys, data, notiser och testprojekt.
- [x] Inför DI, grundläggande konfigurationsvalidering och loggning utan privat text.
- [x] Implementera live/ready och skydda administrativa endpoints med API-nyckel.
- [x] Bind API till loopback som standard.
- [ ] Verifiera Dockerfile/Compose, privata bind mounts och PostgreSQL-volym.
- [ ] Verifiera healthchecks och image på målservern.
- [ ] Montera CV/session/secrets först när respektive funktion kopplas in.

Dockerfiler finns. Appimagen innehåller inte Chromium och gör inga kontoanrop.
Färdigt när app/databas startar med rätt behörigheter via Compose.

## Steg 4 – Datamodell och lagring

- [x] Modellera uppdrag, revision, kandidatprofil, analys, notis och körningshistorik.
- [x] Generera InitialSchema-migration och verifiera modellöverensstämmelse.
- [ ] Verifiera unik källa/ID och revisionshistorik i faktisk PostgreSQL.
- [ ] Verifiera parallell import och omstart med beständiga data.
- [ ] Koppla analyslagring till revision, CV-, filter-, prompt- och modellversion.
- [ ] Verifiera migrations-, backup- och återställningsrutiner.
- [ ] Fastställ retention innan automatisk radering införs.

Fingerprint inkluderar metadata/kravtolkning men exkluderar hämtningstid.
Importkod, unika index och transaktionsbundet advisory lock finns.
`--postgres` ger isolerade syntetiska integrationstester; de har ännu inte körts.

## Steg 5 – Insamling och filterpipeline

- [x] Definiera IAssignmentSource och lokal JSON-källa.
- [x] Implementera privata filter med motivering och okända fakta som granskningsbehov.
- [x] Inför begränsad hämtning, timeout, paus och validering av nästa sidlänk.
- [ ] Gör Brainville-prototypen till en källa i den sammanhängande appen.
- [ ] Verifiera import → lagring → filter i målmiljön.
- [ ] Inför källisolering, körningsstatus och begränsade återförsök vid tillfälliga fel.
- [ ] Hantera avslutade uppdrag utan att begränsad insamling tolkas som avslut.

Skyddad manuell import finns. Misslyckad import markeras som fel.
Återstartshantering och historiska filterbeslut behöver utökas.

## Steg 6 – CV och AI

- [x] Inför lokal CV-förberedelse, kontaktminimering och användargranskning med hash.
- [x] Implementera Responses-provider med Structured Outputs och store:false.
- [x] Definiera poäng, rekommendation, belägg, kravstatus, luckor och osäkerheter.
- [x] Validera struktur/citat och hantera refusal, timeout och ofullständiga svar.
- [x] Reservera konservativ kostnad beständigt före HTTP och stoppa dubbletter.
- [x] Spara avvisade svar separat och stöd lokal källförankrad citatomkontroll.
- [ ] Utvärdera semantisk kvalitet med fler manuellt bedömda positiva/negativa fall.
- [ ] Kalibrera notisgräns och verifiera promptens effekt före automatisk analys.

Prompt cv-match-4 skiljer egen erfarenhet från teamets teknik och får slutdatum,
kontaktminimerade källvarningar och appens granskningsstatus. Tom klassificerad
kravlista betyder inte att annonsen saknar krav. Generiska kompetensrubriker
bevaras som oklassificerade; explicita skallkrav klassificeras separat.
Verkliga resultat och citatkorrigeringar sparas endast privat.
`analysis-demo` är syntetiska fixtures, inte CV-bedömningar.

## Steg 7 – Notifieringar

- [x] Implementera/testa beslutspolicy och svensk HTML-/textförhandsvisning.
- [x] Implementera LogOnly utan privata uppgifter i loggar.
- [ ] Verifiera Google SMTP-adapter med godkänd konfiguration och testutskick.
- [ ] Koppla beständig kö med unik analys/mottagare och stabilt Message-ID.
- [ ] Verifiera Sending/Accepted/Uncertain och återstart utan blinda omutskick.
- [ ] Fastställ verklig poänggräns och Review-policy.

SMTP kräver STARTTLS och normal certifikatvalidering. Fel efter påbörjad sändning
markeras Uncertain. Adaptern är inte inkopplad i CLI/API. Demo-gränsen 70 är bara
en fixtureinställning. Verklig SMTP-leverans har inte verifierats.

## Steg 8 – Schema och sammanhängande flöde

- [ ] Koppla hämtning → filter → analys → notifiering i BackgroundService.
- [ ] Inför privat intervall, paus och skyddad manuell körning.
- [ ] Håll torrkörning av e-post och betalda AI-anrop som separata kontroller.
- [ ] Förhindra överlappning och återuppta väntande arbete efter omstart.
- [ ] Hantera cancellation, källfel och begränsade återförsök.
- [ ] Visa senaste lyckade körning och behov av manuell inloggning.

UTC används i lagring; användartider visas i Europe/Stockholm.

## Steg 9 – Översikt

- [ ] Granska referens-UI före val av teknik.
- [ ] Visa uppdrag, filterorsaker, analys, körningshistorik och notisstatus.
- [ ] Lägg till manuell körning, paus och e-postförhandsvisning.
- [ ] Skydda UI och administrativa åtgärder.

UI följer när kärnflödet fungerar. Ingen full dashboard krävs för prototypen.

## Steg 10 – Drift

- [ ] Kör PostgreSQL- och köintegrationstester med syntetiska data.
- [ ] Verifiera imagebygge, Compose, lagringsbehörigheter och omstart på målservern.
- [ ] Verifiera sessionsportabilitet, Chromium-beroenden och minnesbehov.
- [ ] Fastställ privata portar, resursgränser och versionslåsta images.
- [ ] Verifiera backup/restore, uppdatering och rollback.
- [ ] Börja med LogOnly och aktivera riktiga notiser först efter granskning.

Compose har loggrotation; övrig faktisk driftverifiering återstår.

## Referenser

- [Körinstruktioner](repo/README.md)
- [Lokal värd och notifieringar](repo/docs/local-host-and-notifications.md)
- [Brainville-förstudie](repo/docs/brainville-integration.md)
- [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)
- [Google SMTP](https://support.google.com/mail/answer/7104828)
- [Google applösenord](https://support.google.com/mail/answer/185833)
