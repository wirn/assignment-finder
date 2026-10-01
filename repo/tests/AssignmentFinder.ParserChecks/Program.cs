using AssignmentFinder.Brainville;
using AssignmentFinder.Analysis;
using System.Text.Json;

if (args is ["--postgres"])
{
    try { await PostgresChecks.RunAsync((valid, name) => { if (!valid) throw new InvalidDataException(name); Console.WriteLine("PASS: " + name); }); return; }
    catch { Console.Error.WriteLine("PostgreSQL-kontrollerna misslyckades. Kontrollera testdatabas och konfiguration. Anslutningsuppgifter loggas inte."); Environment.ExitCode = 1; return; }
}
if (args.Length != 0) { Console.Error.WriteLine("Använd utan argument för lokala kontroller eller --postgres för testdatabas."); Environment.ExitCode = 2; return; }

const string html = """
<main id="MainContent">
  <div class="c_product_header"><h1>Utvecklare &amp; arkitekt</h1>
    <a href="/Network/PublicProfile/Index/1">Testbolag</a></div>
  <a href="/Market/RequisitionSearchResult/Details/42">Översikt</a>
  <div id="MainTarget"><div>AI-SAMMANFATTNING SKA INTE MED</div>
    <div class="l_tinymce_formatting">
      <p>SVAR SENAST 2026-10-05</p><p><b>Fakta</b></p>
      <ul><li>Start: 2026-10-12</li><li>Slut: 2027-12-31</li>
        <li>Plats: Stockholm</li><li>Omfattning: 40 timmar/vecka</li><li>Arbetsform: Hybrid</li></ul>
      <p>Om uppdraget</p><p>Bygg API:er.<br>Behåll radbrytning.</p>
      <p><b>Obligatoriska krav</b></p><ul><li>Erfarenhet av <span>C#</span></li><li>.NET &amp; SQL</li></ul>
      <p><b>Meriterande</b></p><ul><li>Docker</li></ul>
      <script>HEMLIG_SCRIPT</script><input value="HEMLIG_TOKEN">
    </div>
    <div class="c_chip">31 december 2029</div>
  </div>
  <aside>KONSULTPROFIL SKA INTE MED</aside>
</main>
""";
var parser = new BrainvilleDetailParser();
var url = new Uri("https://www.brainville.com/Market/RequisitionSearchResult/Details/42?returnLink=test");
var result = parser.Parse(html, url, DateTimeOffset.UtcNow);
void Check(bool valid, string name)
{
    if (!valid) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}
Check(result.ExternalId == "42" && result.Url.Query == "", "Identifierare och kanonisk URL");
var absoluteLinkHtml = html.Replace("href=\"/Market/RequisitionSearchResult/Details/42\"",
    "href=\"https://www.brainville.com/Market/RequisitionSearchResult/Details/42?isLocal=False&amp;returnLink=test#\"");
Check(parser.Parse(absoluteLinkHtml, url, DateTimeOffset.UtcNow).ExternalId == "42",
    "Absolut översiktslänk med query och fragment");
Check(result.Title == "Utvecklare & arkitekt", "HTML-entiteter");
Check(result.Description.Contains("Bygg API:er.\nBehåll radbrytning."), "Radbrytningar i beskrivning");
Check(!result.Description.Contains("SKA INTE MED") && !result.Description.Contains("HEMLIG"), "Avgränsning av uppdragsdata");
Check(result.MandatoryRequirements.SequenceEqual(new[] { "Erfarenhet av C#", ".NET & SQL" })
    && result.PreferredQualifications.SequenceEqual(new[] { "Docker" }), "Krav skiljs från meriterande");
Check(result.StartDate == new DateOnly(2026, 10, 12) && result.EndDate == new DateOnly(2027, 12, 31)
    && result.ApplicationDeadline == new DateOnly(2026, 10, 5), "Explicita datum");
Check(result.Warnings.Count == 1, "Motstridigt metadatadatum flaggas");
var missing = parser.Parse(html.Replace("Start: 2026-10-12", "Start: Snarast"), url, DateTimeOffset.UtcNow);
Check(missing.StartDate is null, "Okänt datum lämnas okänt");
Check(result.ContentHash == parser.Parse(html, url, DateTimeOffset.UtcNow.AddDays(1)).ContentHash, "Stabil innehållshash");
void Reject(string input, Uri inputUrl, string name)
{
    try { parser.Parse(input, inputUrl, DateTimeOffset.UtcNow); }
    catch (Exception error) when (error is InvalidDataException or ArgumentException) { Check(true, name); return; }
    throw new Exception("FAILED: " + name);
}
Reject("<h1>Logga in</h1>", url, "Utloggad sida avvisas");
Reject(html.Replace("l_tinymce_formatting", "changed_structure"), url, "Ändrad struktur avvisas");
Reject(html, new Uri("https://www.brainville.com/Market/RequisitionSearchResult/Details/43"), "Fel uppdrags-ID avvisas");
Reject(html, new Uri("https://example.com/Market/RequisitionSearchResult/Details/42"), "Fel källdomän avvisas");
Reject(absoluteLinkHtml, new Uri("https://www.brainville.com/Market/RequisitionSearchResult/Details/43"),
    "Fel uppdrags-ID i absolut översiktslänk avvisas");
Reject(absoluteLinkHtml.Replace("https://www.brainville.com", "https://example.com"), url,
    "Extern översiktslänk avvisas");

string WithBody(string content) => html.Replace(html[(html.IndexOf("      <p>SVAR", StringComparison.Ordinal))..
    html.IndexOf("    </div>", html.IndexOf("      <p>SVAR", StringComparison.Ordinal), StringComparison.Ordinal)], content);
var english = parser.Parse(WithBody("""
<div><div><b>Skills</b></div><br><ul><li>Six years of C#</li></ul><br>
<ul><li>Two years of CSS</li></ul><div><b>Good to have</b></div>
<div>Energy sector experience</div></div>
<p><span>Start</span><br><span>2026-10-12</span><span>Extension</span><br><span>N/A</span>
<span>End</span><br><span>2027-03-31</span><span>Remote</span><br><span>N/A</span></p>
"""), url, DateTimeOffset.UtcNow);
Check(english.UnclassifiedRequirements.SequenceEqual(new[] { "Six years of C#", "Two years of CSS" })
    && english.MandatoryRequirements.Count == 0, "Skills har okänd kravstatus och flera listor bevaras");
Check(english.PreferredQualifications.SequenceEqual(new[] { "Energy sector experience" }), "Meriterande löptext i nästlat div");
Check(english.StartDate == new DateOnly(2026, 10, 12) && english.EndDate == new DateOnly(2027, 3, 31),
    "Engelska datum i separata span-fält");
var spans = parser.Parse(WithBody("""
<span><span>Required Skills &amp; Experience</span><span>
<span><span>Six years</span> as a developer.</span>
<span><span>Six years with:</span><span><span>C#</span><span>SQL</span></span></span></span>
<span>Personal skills</span><span>Teamwork</span>
<span>Nice to have</span><span><span>Finance</span><span>Distributed systems</span></span>
<span>Education</span><span>Degree or equivalent</span></span>
"""), url, DateTimeOffset.UtcNow);
Check(spans.MandatoryRequirements.SequenceEqual(new[] { "Six years as a developer.", "Six years with: C# SQL" }),
    "Span-krav bevarar erfarenhetsvillkor och avgränsas vid nästa rubrik");
Check(spans.PreferredQualifications.SequenceEqual(new[] { "Finance", "Distributed systems" }), "Nice to have avgränsas från utbildning");
Check(spans.Description.Contains("C# SQL"), "Intilliggande span-texter klistras inte ihop");
var chipsHtml = html.Replace("<div class=\"c_chip\">31 december 2029</div>", """
<div class="c_chip"><span class="fa-location-dot"></span><span>Teststad</span></div>
<div class="c_chip"><span class="fa-house-user"></span><span>Hybrid</span></div>
<div class="c_chip"><span class="fa-watch"></span><span>Heltid</span></div>
""").Replace("Plats: Stockholm", "Okänt: Stockholm").Replace("Arbetsform: Hybrid", "Okänt: Hybrid")
    .Replace("Omfattning: 40 timmar/vecka", "Okänt: 40 timmar/vecka");
var chips = parser.Parse(chipsHtml, url, DateTimeOffset.UtcNow);
Check(chips.Location == "Teststad" && chips.WorkArrangement == "Hybrid" && chips.Extent == "Heltid",
    "Metadatafakta identifieras av rätt ikon");
var ambiguous = parser.Parse(WithBody("<p><span>Start</span><br><span>N/A</span><span>End</span><br><span>ASAP</span></p>"), url, DateTimeOffset.UtcNow);
Check(ambiguous.StartDate is null && ambiguous.EndDate is null, "N/A och ASAP ger inga påhittade datum");
var listParser = new BrainvilleListParser();
var swedishSections = parser.Parse(WithBody("""
<p><b>Särskilda skallkrav/särskild kravprofil:</b></p>
<ul><li>Fem års Swift</li><li>Svenska</li></ul>
<p><b>Utvärderingskriterier:</b></p><ul><li>CI/CD</li></ul>
<p>Placering: Stockholm</p><p>Tidsperiod: november–augusti</p>
"""), url, DateTimeOffset.UtcNow);
Check(swedishSections.MandatoryRequirements.SequenceEqual(new[] { "Fem års Swift", "Svenska" })
    && swedishSections.PreferredQualifications.SequenceEqual(new[] { "CI/CD" }),
    "Svenska skallkrav och utvärderingskriterier avgränsas från placeringsfakta");
var inlineSections = parser.Parse(WithBody("<p>Särskilda skallkrav/särskild kravprofil:<br>• Fem års Swift<br>• Svenska<br>Utvärderingskriterier:<br>- CI/CD<br>Placering: Stockholm</p>"), url, DateTimeOffset.UtcNow);
Check(inlineSections.MandatoryRequirements.Count == 2 && inlineSections.PreferredQualifications.SequenceEqual(new[] { "CI/CD" }),
    "Skallkrav med br och textbullets i ett stycke bevaras");
var genericSkills = parser.Parse(WithBody("""
<div><b>Kompetenser och färdigheter</b></div><ul><li>React och Redux</li></ul>
<div><b>Meriterande</b></div><ul><li>webb<mark>tillgänglighet</mark></li></ul>
"""), url, DateTimeOffset.UtcNow);
Check(genericSkills.UnclassifiedRequirements.SequenceEqual(new[] { "React och Redux" })
    && genericSkills.MandatoryRequirements.Count == 0, "Allmän kompetensrubrik bevaras utan antagen skallkravsstatus");
Check(genericSkills.PreferredQualifications.Single() == "webbtillgänglighet"
    && genericSkills.Description.Contains("webbtillgänglighet"), "Sökmarkeringar delar inte ord i krav eller beskrivning");
Check(parser.Parse(WithBody("<p>Hybrid</p>").Replace("31 december 2029", "Ej distansarbete"), url, DateTimeOffset.UtcNow)
    .Warnings.Any(w => w.Contains("arbetsformen")), "Hybridkonflikt med metadata flaggas");
var searchUrl = BrainvilleListParser.ValidateSearchUrl("https://www.brainville.com/Market/RequisitionSearchResult?Filter.Text=test");
const string listHtml = """
<main id="MainContent"><a href="/Market/RequisitionSearchResult/Details/99">Rekommenderat</a>
<div id="RequisitionStream"><div data-requisition-id="97" class="c_card">Rekommenderat</div><div data-card="card lg white" data-requisition-id="42">
<a data-font-type="title" href="/Market/RequisitionSearchResult/Details/42?text=test&amp;isLocal=False">Test</a>
<a href="/Market/RequisitionSearchResult/Details/98">Liknande</a></div>
<div data-card="card lg white" data-requisition-id="43"><a data-font-type="title" href="https://www.brainville.com/Market/RequisitionSearchResult/Details/43">Test 2</a></div>
<div data-card="card lg white" data-requisition-id="42"><a data-font-type="title" href="/Market/RequisitionSearchResult/Details/42">Dubblett</a></div>
</div></main>
""";
var listLinks = listParser.Parse(listHtml, searchUrl);
Check(listLinks.Select(u => u.AbsolutePath.Split('/')[^1]).SequenceEqual(new[] { "42", "43" })
    && listLinks.All(u => u.Query == ""), "Lista avgränsas, normaliseras och dedupliceras i källordning");
void RejectList(string input, string name)
{
    try { listParser.Parse(input, searchUrl); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("FAILED: " + name);
}
RejectList("<main id='MainContent'>Logga in</main>", "Utloggad eller ändrad lista avvisas");
RejectList("<main id='MainContent'><div id='RequisitionStream'></div></main>", "Tom overifierad lista avvisas");
RejectList(listHtml.Replace("Details/43", "Details/44"), "Listkort med fel titellänks-ID avvisas");
RejectList(listHtml.Replace("https://www.brainville.com/Market", "https://example.com/Market"), "Extern titellänk avvisas");
try { BrainvilleListParser.ValidateSearchUrl("https://example.com/Market/RequisitionSearchResult"); throw new Exception("FAILED: sökdomän"); }
catch (ArgumentException) { Check(true, "Extern sök-URL avvisas"); }
const string pagerHtml = """
<main id="MainContent"><div id="RequisitionStream">
<a class="pager_next" data-ajaxlink="#RequisitionStream" href="/Market/RequisitionSearchResult?Filter.Text=test&amp;page=2&amp;newsearch=false">Nästa</a>
</div></main>
""";
var nextPageUrl = listParser.ParseNextPage(pagerHtml, searchUrl);
Check(nextPageUrl is not null && nextPageUrl.Query.Contains("page=2"), "Nästa sidlänk behåller sökfilter");
Check(listParser.ParseNextPage(listHtml, searchUrl) is null, "Sista sida utan nästa länk avslutar paginering");
void RejectPager(string input, string name)
{
    try { listParser.ParseNextPage(input, searchUrl); }
    catch (Exception error) when (error is InvalidDataException or ArgumentException) { Check(true, name); return; }
    throw new Exception("FAILED: " + name);
}
RejectPager(pagerHtml.Replace("page=2", "page=1"), "Sidloop avvisas");
RejectPager(pagerHtml.Replace("Filter.Text=test", "Filter.Text=other"), "Ändrat sökfilter avvisas");
RejectPager(pagerHtml.Replace("href=\"/Market", "href=\"https://example.com/Market"), "Extern nästa sidlänk avvisas");
var filter = new AssignmentFilter();
var preferences = new FilterSettings(["Stockholm"], true, 20);
FilterDecision Filter(string? place, string? arrangement, string? extent) =>
    filter.Evaluate(result with { Location = place, WorkArrangement = arrangement, Extent = extent }, preferences);
Check(Filter("Stockholm", "Hybrid", "20%").Status == "Passed", "Stockholm hybrid vid exakt 20 procent accepteras");
Check(Filter("Göteborg", "Distans", "Heltid").Status == "Passed", "Distans från annan ort accepteras");
Check(Filter("Göteborg", "Hybrid", "100%").Status == "Rejected", "Hybrid utanför Stockholm avvisas");
Check(Filter("Göteborg", "På plats", "50%").Status == "Rejected", "På plats utanför Stockholm avvisas");
Check(Filter("Stockholm", "Hybrid", "19,9%").Status == "Rejected", "Omfattning under gränsen avvisas");
Check(Filter("Stockholm", null, "10–15%").Status == "Rejected", "Intervall helt under gränsen avvisas");
Check(Filter("Stockholm", null, "10–40%").Status == "NeedsReview", "Överlappande omfattningsintervall är osäkert");
Check(Filter("Stockholm", null, "20-40%").Status == "Passed", "Intervall över gränsen accepteras");
Check(Filter(null, null, null) is { Status: "NeedsReview", ContinueToAnalysis: true }, "Okända uppgifter avvisas inte");
Check(Filter("Stockholm", null, "8 timmar/vecka").Status == "NeedsReview", "Timmar räknas inte om med antagen heltid");
Check(Filter("Göteborg", "20% remote", "Heltid").Status == "NeedsReview", "Delvis distans räknas inte som full distans");
Check(Filter("Stockholm", null, "101%").Status == "NeedsReview", "Ogiltig omfattning klassas som okänd");
Check(filter.Evaluate(result with { StartDate = new DateOnly(2020, 1, 1), MandatoryRequirements = [] }, preferences).ContinueToAnalysis,
    "Inga teknik- eller startdatumfilter tillämpas");
try { (preferences with { MinimumExtentPercent = -1 }).Validate(); throw new Exception("FAILED: filterkonfiguration"); }
catch (ArgumentException) { Check(true, "Ogiltig filterkonfiguration avvisas"); }
foreach (var demo in AnalysisDemo.Cases())
{
    var serialized = JsonSerializer.Serialize(demo.Result, AnalysisValidator.JsonOptions);
    Check(AnalysisValidator.Parse(serialized, demo.Input).Score == demo.Result.Score, $"Analysdemo {demo.Name} valideras");
}
void RejectAnalysis(AnalysisResult candidate, string name)
{
    try { AnalysisValidator.Validate(candidate, AnalysisDemo.Input); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("FAILED: " + name);
}
RejectAnalysis(AnalysisDemo.Good with { Score = 101 }, "Analys utanför poängskalan avvisas");
RejectAnalysis(AnalysisDemo.Good with { MandatoryRequirements = [] }, "Utelämnade obligatoriska krav avvisas");
RejectAnalysis(AnalysisDemo.Good with { MandatoryRequirements = [AnalysisDemo.Good.MandatoryRequirements[0], AnalysisDemo.Good.MandatoryRequirements[0]] },
    "Dubblerade kravbedömningar avvisas");
RejectAnalysis(AnalysisDemo.Good with { MatchingSkills = [new("Kubernetes", new("Angular", "Kubernetes-expert"))] },
    "Påhittade CV-citat avvisas");
RejectAnalysis(AnalysisDemo.Good with { Uncertainties = ["Obekräftat krav"] }, "Motsägande ansökningsrekommendation avvisas");
try { AnalysisValidator.Parse("{\"score\":90}", AnalysisDemo.Input); throw new Exception("FAILED: ofullständig JSON"); }
catch (InvalidDataException) { Check(true, "Ofullständig analys-JSON avvisas"); }
var maliciousJson = JsonSerializer.Serialize(AnalysisDemo.Good, AnalysisValidator.JsonOptions).TrimEnd().TrimEnd('}')
    + ",\"emailRecipient\":\"attacker@example.invalid\"}";
try { AnalysisValidator.Parse(maliciousJson, AnalysisDemo.Input); throw new Exception("FAILED: extra verktygsfält"); }
catch (InvalidDataException) { Check(true, "Extra mottagar-/verktygsfält i analysen avvisas"); }
await OpenAiChecks.RunAsync(Check);
await NotificationChecks.RunAsync(Check);
DataChecks.Run(Check);
await AppChecks.RunAsync(Check);
var whitespaceInput = AnalysisDemo.Input with { CvText = AnalysisDemo.Input.CvText.Replace(" ", "\r\n  "),
    Assignment = AnalysisDemo.Input.Assignment with { Description = AnalysisDemo.Input.Assignment.Description.Replace(" ", "\u00a0\n") } };
Check(AnalysisValidator.Parse(JsonSerializer.Serialize(AnalysisDemo.Good, AnalysisValidator.JsonOptions), whitespaceInput).Score == AnalysisDemo.Good.Score,
    "Belägg tolererar radslut, blanksteg och hårda mellanslag utan att ändra ord");
RejectAnalysis(AnalysisDemo.Good with { MatchingSkills = [new("Frontend med Angular", AnalysisDemo.Good.MatchingSkills[0].Evidence)] },
    "Beskrivande kompetensnamn utan gemensam ordalydelse avvisas");
RejectAnalysis(AnalysisDemo.Good with { MatchingSkills = [new("Angular", new("Angular och Kubernetes", AnalysisDemo.Good.MatchingSkills[0].Evidence.CvQuote))] },
    "Tillagda ord i citat avvisas trots korrekt kompetensnamn");
var invalidQuoteResult = AnalysisDemo.Good with { MatchingSkills = [new("Angular", new("Angular", "Angular projekt")), AnalysisDemo.Good.MatchingSkills[1]] };
var capturedForRecheck = new CapturedAnalysisResponse("synthetic-hash", "mock", "synthetic", AnalysisDemo.Input.Assignment.ContentHash,
    AnalysisDemo.Input.CvHash, DateTimeOffset.UnixEpoch, new(100, 100), .01m, JsonSerializer.Serialize(invalidQuoteResult, AnalysisValidator.JsonOptions));
var quoteCorrection = new EvidenceQuoteCorrection(0, QuoteSource.Cv, "Angular projekt", "Angular-applikationer");
var correctedResult = AnalysisRevalidation.Validate(capturedForRecheck, AnalysisDemo.Input, [quoteCorrection]);
Check(correctedResult.Score == 90 && correctedResult.Recommendation == Recommendation.Review
    && capturedForRecheck.AnalysisJson.Contains("Angular projekt"), "Lokal citatkorrigering valideras med originalsvaret bevarat och kräver granskning");
void RejectRecheck(CapturedAnalysisResponse response, EvidenceQuoteCorrection[] corrections, string name)
{
    try { AnalysisRevalidation.Validate(response, AnalysisDemo.Input, corrections); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("FAILED: " + name);
}
RejectRecheck(capturedForRecheck with { CvHash = "changed" }, [quoteCorrection], "Lokal omkontroll nekar annan CV-version");
RejectRecheck(capturedForRecheck with { AssignmentHash = "changed" }, [quoteCorrection], "Lokal omkontroll nekar annan uppdragsversion");
RejectRecheck(capturedForRecheck, [quoteCorrection with { OriginalQuote = "fel citat" }], "Citatkorrigering måste matcha originalet");
RejectRecheck(capturedForRecheck, [quoteCorrection with { CorrectedQuote = "Angular och Kubernetes-expert" }], "Påhittad citatkorrigering avvisas");
RejectRecheck(capturedForRecheck, [quoteCorrection, quoteCorrection], "Dubblerad citatkorrigering avvisas");
var invalidAssignmentQuote = AnalysisDemo.Good with { MatchingSkills = [new("Angular", new("Angular felord", "Angular-applikationer")), AnalysisDemo.Good.MatchingSkills[1]] };
var correctedAssignment = AnalysisRevalidation.Validate(capturedForRecheck with { AnalysisJson = JsonSerializer.Serialize(invalidAssignmentQuote, AnalysisValidator.JsonOptions) },
    AnalysisDemo.Input, [new(0, QuoteSource.Assignment, "Angular felord", "Angular")]);
Check(correctedAssignment.MatchingSkills[0].Evidence.AssignmentQuote == "Angular", "Källförankrad korrigering av uppdragscitat valideras lokalt");
