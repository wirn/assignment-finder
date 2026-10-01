using System.Net;
using System.Text;
using System.Text.Json;
using AssignmentFinder.Analysis;

internal static class OpenAiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "assignment-ai-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new OpenAiSettings(true, "gpt-6-luna", "gpt-6-luna", 10, .125m, .5m, 12, 1.5m, 4096);
        AnalysisBudget Budget() => new(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"));
        async Task Reject(Func<Task> action, string name)
        {
            try { await action(); }
            catch (InvalidDataException) { check(true, name); return; }
            throw new Exception("FAILED: " + name);
        }
        void RejectSync(Action action, string name)
        {
            try { action(); }
            catch (InvalidDataException) { check(true, name); return; }
            throw new Exception("FAILED: " + name);
        }
        try
        {
            var input = AnalysisDemo.Input;
            var prepared = OpenAiAssignmentAnalyzer.Prepare(input, settings);
            var warned = input with { Assignment = input.Assignment with { EndDate = new(2026, 1, 1),
                Warnings = ["Slutdatum före startdatum. Kontakt admin@example.invalid"] } };
            using (var warningRequest = JsonDocument.Parse(OpenAiAssignmentAnalyzer.Prepare(warned, settings).Body))
            using (var warningInput = JsonDocument.Parse(warningRequest.RootElement.GetProperty("input").GetString()!))
            {
                var data = warningInput.RootElement.GetProperty("assignment");
                check(data.GetProperty("endDate").GetString() == "2026-01-01"
                    && data.GetProperty("warnings")[0].GetString()!.Contains("Slutdatum")
                    && !data.GetProperty("warnings")[0].GetString()!.Contains("admin@example.invalid"),
                    "AI får slutdatum och kontaktminimerade källvarningar");
            }
            check(prepared.ReservedSek > 0 && prepared.InputTokenBound > Encoding.UTF8.GetByteCount(input.CvText), "AI-kostnadsreservation inkluderar indata, schema och maximalt svar");
            using (var requestSchema = JsonDocument.Parse(prepared.Body))
            {
                var requirementsSchema = requestSchema.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema")
                    .GetProperty("properties").GetProperty("mandatoryRequirements");
                check(requirementsSchema.GetProperty("minItems").GetInt32() == 2 && requirementsSchema.GetProperty("maxItems").GetInt32() == 2
                    && requirementsSchema.GetProperty("items").GetProperty("properties").GetProperty("requirement").GetProperty("enum")
                        .EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "Angular", "WCAG" }),
                    "AI-schema låser kravantal och tillåtna kravtexter till indatan");
            }
            var unclassifiedInput = input with { Assignment = input.Assignment with { MandatoryRequirements = [], UnclassifiedRequirements = ["Angular", "WCAG"] } };
            using (var emptyRequest = JsonDocument.Parse(OpenAiAssignmentAnalyzer.Prepare(unclassifiedInput, settings).Body))
            {
                var requirementsSchema = emptyRequest.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema")
                    .GetProperty("properties").GetProperty("mandatoryRequirements");
                check(requirementsSchema.GetProperty("minItems").GetInt32() == 0 && requirementsSchema.GetProperty("maxItems").GetInt32() == 0,
                    "Oklassificerade krav ger en obligatoriskt tom kravlista i AI-schemat");
            }
            var contaminated = input with { Assignment = input.Assignment with { Description = input.Assignment.Description + " admin@example.invalid +46 70 123 45 67" } };
            var minimized = OpenAiAssignmentAnalyzer.Prepare(contaminated, settings);
            check(!minimized.Body.Contains("admin@example.invalid") && !minimized.Body.Contains("123 45 67") && !minimized.Body.Contains("example.invalid/"), "AI-underlag minimerar kontaktuppgifter och utesluter källänk");
            RejectSync(() => (settings with { Model = "another-model" }).Validate(), "Modellbyte utan matchande pris nekas");
            RejectSync(() => (settings with { TotalBudgetSek = 11 }).Validate(), "Testbudget över användarens 10 kr nekas");
            var path = Path.Combine(directory, "budget.json");
            new AnalysisBudget(path).Reserve("one", 6m, 10m);
            new AnalysisBudget(path).Reserve("two", 4m, 10m);
            check(new AnalysisBudget(path).Read(10).Entries.Sum(e => e.ReservedSek) == 10, "Budget överlever omstart och tillåter exakt 10 kr");
            RejectSync(() => new AnalysisBudget(path).Reserve("three", .0001m, 10), "Budgetstopp innan ytterligare anrop");
            RejectSync(() => new AnalysisBudget(path).Reserve("one", 1, 10), "Samma underlag reserveras inte två gånger");
            var lockedPath = Path.Combine(directory, "locked.json");
            using (var gate = new FileStream(lockedPath + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                try { new AnalysisBudget(lockedPath).Reserve("locked", 1, 10); throw new Exception("FAILED: budgetlås"); }
                catch (IOException) { check(!File.Exists(lockedPath), "Samtidig budgetskrivning stoppas av exklusivt lås"); }
            }
            File.WriteAllText(path, "{\"limitSek\":10}");
            try { new AnalysisBudget(path).Reserve("missing", 1, 10); throw new Exception("FAILED: budgetdata"); }
            catch (JsonException) { check(true, "Skadad budgetjournal nekas utan återställning av utgifter"); }

            string Response(string status = "completed", bool refusal = false, AnalysisResult? result = null, int outputTokens = 100) => JsonSerializer.Serialize(new
            {
                status, usage = new { input_tokens = 200, output_tokens = outputTokens },
                output = new[] { new { type = "message", content = new[] { new { type = refusal ? "refusal" : "output_text", text = JsonSerializer.Serialize(result ?? AnalysisDemo.Good, AnalysisValidator.JsonOptions) } } } }
            });
            using (var emptyHandler = new StubHandler(Response(result: AnalysisDemo.Good with { MandatoryRequirements = [] })))
            using (var emptyClient = new HttpClient(emptyHandler))
            {
                var result = await new OpenAiAssignmentAnalyzer(emptyClient, settings, Budget(), "key").AnalyzeAsync(unclassifiedInput);
                check(result.MandatoryRequirements.Length == 0, "Mock-analys av uppdrag utan klassificerade krav kan valideras");
            }
            using (var addedHandler = new StubHandler(Response()))
            using (var addedClient = new HttpClient(addedHandler))
                await Reject(() => new OpenAiAssignmentAnalyzer(addedClient, settings, Budget(), "key").AnalyzeAsync(unclassifiedInput),
                    "AI får inte lägga till egna krav i tom klassificerad lista");
            using var handler = new StubHandler(Response());
            using var client = new HttpClient(handler);
            var budget = Budget();
            var analyzer = new OpenAiAssignmentAnalyzer(client, settings, budget, "synthetic-key");
            var valid = await analyzer.AnalyzeAsync(input);
            check(valid.Recommendation == Recommendation.Apply && analyzer.Usage == new AnalysisUsage(200, 100) && handler.Calls == 1, "Responses-svar valideras och tokenanvändning sparas (mock-HTTP)");
            check(analyzer.LastResponse?.RequestHash == prepared.Hash && analyzer.LastResponse.CvHash == prepared.Input.CvHash,
                "AI-svar behåller underlagshash för privat felsökning");
            using (var body = JsonDocument.Parse(handler.Body!))
                check(body.RootElement.GetProperty("store").GetBoolean() == false
                    && body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean()
                    && !body.RootElement.TryGetProperty("tools", out _), "Responses använder store:false, strikt JSON-schema och inga verktyg");
            await Reject(() => analyzer.AnalyzeAsync(input), "Upprepad analys nekas före nätverksanrop");
            check(handler.Calls == 1, "Ingen extra HTTP-begäran för dubblett");
            check(analyzer.LastResponse is null, "Nytt avvisat anrop återanvänder inte gammalt providersvar");
            await Reject(() => new OpenAiAssignmentAnalyzer(client, settings with { Enabled = false }, Budget(), "key").AnalyzeAsync(input), "Avstängd AI gör inget anrop");
            await Reject(() => new OpenAiAssignmentAnalyzer(client, settings, Budget(), "").AnalyzeAsync(input), "Saknad API-nyckel gör inget anrop");
            await Reject(() => new OpenAiAssignmentAnalyzer(client, settings with { TotalBudgetSek = .0001m }, Budget(), "key").AnalyzeAsync(input), "Otillräcklig budget stoppas före HTTP");
            check(handler.Calls == 1, "Förkontroller gör inga betalda anrop");
            foreach (var (response, status, name) in new[]
            {
                (Response("incomplete"), HttpStatusCode.OK, "Ofullständigt AI-svar avvisas"),
                (Response(refusal: true), HttpStatusCode.OK, "Refusal avvisas utan negativ matchning"),
                ("private-error-body", HttpStatusCode.TooManyRequests, "Rate limit ger inget automatiskt återförsök"),
                ("not-json", HttpStatusCode.OK, "Ogiltigt providersvar avvisas"),
                (Response(outputTokens: 99999), HttpStatusCode.OK, "Oväntad tokenanvändning avvisas"),
                (Response(result: AnalysisDemo.Good with { Score = 101 }), HttpStatusCode.OK, "Providerresultat valideras före lagring")
            })
            {
                using var failureHandler = new StubHandler(response, status);
                using var failureClient = new HttpClient(failureHandler);
                var failureBudget = Budget();
                await Reject(() => new OpenAiAssignmentAnalyzer(failureClient, settings, failureBudget, "key").AnalyzeAsync(input), name);
                check(failureHandler.Calls == 1 && failureBudget.Read(10).Entries.Length == 1, "Misslyckat anrop behåller reservation och återförsöks inte");
            }
            using var timeoutHandler = new StubHandler("", timeout: true);
            using var timeoutClient = new HttpClient(timeoutHandler);
            var timeoutBudget = Budget();
            await Reject(() => new OpenAiAssignmentAnalyzer(timeoutClient, settings, timeoutBudget, "key").AnalyzeAsync(input), "Timeout hanteras säkert");
            check(timeoutBudget.Read(10).Entries.Length == 1, "Timeout återför inte osäker kostnad");
            using var evidenceHandler = new StubHandler(Response(result: AnalysisDemo.Good with
            { MatchingSkills = [new("Frontend Angular", AnalysisDemo.Good.MatchingSkills[0].Evidence)] }));
            using var evidenceClient = new HttpClient(evidenceHandler);
            var evidenceAnalyzer = new OpenAiAssignmentAnalyzer(evidenceClient, settings, Budget(), "key");
            await Reject(() => evidenceAnalyzer.AnalyzeAsync(input), "Ogiltigt kompetensnamn avvisas av provider");
            var captured = evidenceAnalyzer.LastResponse;
            check(captured is not null && captured.Usage.OutputTokens == 100 && captured.AnalysisJson.Contains("Frontend Angular")
                && !JsonSerializer.Serialize(captured).Contains("synthetic-key"), "Avvisat svar kan sparas privat utan API-nyckel och utan nytt anrop");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class StubHandler(string response, HttpStatusCode status = HttpStatusCode.OK, bool timeout = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri?.AbsoluteUri != "https://api.openai.com/v1/responses") throw new Exception("FAILED: API-origin");
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (timeout) throw new TaskCanceledException();
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
