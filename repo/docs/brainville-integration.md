# Brainville – integrationsförstudie

Publik teknisk sammanställning. Kontots faktiska API-åtkomst, inloggningsunderlag,
privata HTML-/JSON-exporter och avtalsgodkännanden dokumenteras utanför Git.

## API före webbläsare

Brainville dokumenterar Market API / Assignment Export för marknadsuppdrag.
Det skiljer sig från API för företagets egna publicerade uppdrag. Utvärdera
kontots API Access, abonnemang och exportlicens innan hämtning väljs. Kvoter,
behörigheter och rätt till extern behandling måste verifieras för det aktuella kontot.

API-autentisering är separat från vanlig webbinloggning. API User Key och API
Sender Key är hemligheter och får inte publiceras. Dokumenterade Market-routes
omfattar sökning och detaljhämtning; verifiera aktuell basadress och kontrakt före
implementation. Ett testanrop kan förbruka exportkvot.

## Playwright-prototyp

CLI har login, fetch, fetch-list, browser-check och session-check med Playwright
för .NET. Hämtningen är begränsad till högst tre detaljer och 1–3 listresultatsidor
med paus. Kontots tillåtelse för automatisering/serverlagring måste vara klarlagd;
BRAINVILLE_BROWSER_ACCESS_CONFIRMED dokumenterar detta, men ger ingen avtalsrätt.
Inga CAPTCHA, MFA eller andra åtkomstkontroller kringgås.

Manuell inloggning sker i användarens Chromium. Detaljsidan verifieras före
sessionslagring. Cookies, local storage och IndexedDB sparas privat; session storage
stöds inte av den nuvarande prototypen. Verklig sessionsutgång, eventuell MFA
 och serverportabilitet behöver fortsatt verifiering.

## Parser och listavgränsning

Detaljparsern avgränsar uppdragets rubrik och beskrivning med MainContent,
MainTarget, c_product_header och l_tinymce_formatting. Kontrollerad översiktslänk
ska matcha ID och Brainville-domän. Script, formulär, konsultprofiler och sidans
AI-sammanfattning används inte som uppdragsbeskrivning.

Listparsern använder RequisitionStream, data-card, data-requisition-id och
rubriklänkens data-font-type. Rekommendationer och liknande uppdrag exkluderas.
Länkar normaliseras och dedupliceras. Nästa sidlänk måste behålla domän, filter,
sortering och förväntat sidnummer. Begränsad insamling markerar inte missade
uppdrag som avslutade. Verkliga tomma resultat är ännu inte verifierade och
behandlas som fel i stället för ett tomt lyckat resultat.

Kravrubriker, nästlade div/span, textbullets och sökmarkeringar har syntetiska
regressionstester. Generiska kompetensrubriker ger oklassificerade behov.
Datum som saknas eller är tvetydiga gissas inte; konflikter ger källvarningar.
Privata verifieringsunderlag och annonsidentifierare ingår inte i det publika repot.

## Sessionsfel och källfel

Tolv syntetiska Chromium-tester använder nya kontexter och lokalt besvarade
anrop utan sparad användarsession. HTTP 401, login-omdirigering och synligt
lösenordsfält ger behov av återinloggning. HTTP 403 klassas som åtkomstfel;
ändrad struktur eller uteblivet innehåll hålls separata. Ett dolt lösenordsfält
ska inte felaktigt ge sessionsfel.

## Extern behandling

Klargör hur aktuellt avtal tillåter insamling, lagring och eventuell analys via
OpenAI. Ett personligt AI-godkännande ersätter inte källans avtalsvillkor.
Syntetiska uppdrag används under vanlig utveckling. Privat källtext behandlas
som opålitlig data och får aldrig styra verktyg, mottagare eller hemlighetsåtkomst.

## Källor att kontrollera inför integration

- [Market API](https://developer.brainville.com/api/v2/market/overview/)
- [Assignment Export](https://developer.brainville.com/assignment-export/introduction/)
- [API-autentisering](https://developer.brainville.com/api/v2/authentication/)
- [Sökning](https://developer.brainville.com/api/v2/market/search/)
- [Detaljhämtning](https://developer.brainville.com/api/v2/market/get/)
- [Villkor](https://www.brainville.com/PublicPage/Terms?lang=en)
