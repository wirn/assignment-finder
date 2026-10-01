using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;

internal static class AnalysisCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 2 && !(args.Length == 3 && args[2] == "--send"))
                throw new ArgumentException("Använd analyze <uppdrags-JSON> [--send]. Utan --send görs endast lokal kontroll.");
            var settings = JsonSerializer.Deserialize<OpenAiSettings>(await File.ReadAllTextAsync("data/private/openai-settings.json"), AnalysisValidator.JsonOptions)
                ?? throw new InvalidDataException("AI-konfiguration saknas.");
            settings.Validate();
            var assignment = JsonSerializer.Deserialize<Assignment>(await File.ReadAllTextAsync(args[1]), FilterCommands.JsonOptions)
                ?? throw new InvalidDataException("Uppdrag saknas.");
            var filter = new AssignmentFilter().Evaluate(assignment, await FilterCommands.LoadAsync());
            if (!filter.ContinueToAnalysis)
            {
                Console.WriteLine("Uppdraget avvisades av lokala filter. Inget AI-anrop.");
                return 0;
            }
            // The preparation script hashes LF text; Windows may write CRLF to disk.
            var cv = (await File.ReadAllTextAsync("data/private/cv/cv-matching-draft.md")).Replace("\r\n", "\n");
            using var preparation = JsonDocument.Parse(await File.ReadAllTextAsync("data/private/cv/preparation.json"));
            var cvHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cv)));
            if (!preparation.RootElement.GetProperty("reviewedByUser").GetBoolean()
                || !cvHash.Equals(preparation.RootElement.GetProperty("matchingDraftSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CV-utkastet måste granskas på nytt innan analys.");
            var input = new AnalysisInput(assignment, cv) { CvReviewedByUser = true };
            var prepared = OpenAiAssignmentAnalyzer.Prepare(input, settings);
            var budget = new AnalysisBudget("data/private/analysis-budget.json");
            var state = budget.Read(settings.TotalBudgetSek);
            var available = Math.Max(0, state.LimitSek - state.Entries.Sum(e => e.ReservedSek));
            Console.WriteLine($"Modell: {settings.Model}. Total testbudget: {state.LimitSek:0.00} kr. Kvar: {available:0.0000} kr.");
            Console.WriteLine($"Konservativ reservation för detta anrop: {prepared.ReservedSek:0.0000} kr. Max svar: {settings.MaxOutputTokens} tokens.");
            if (args.Length == 2)
            {
                Console.WriteLine("Lokal kontroll klar. Inget skickat till OpenAI, inga pengar reserverade, ingen e-post.");
                return 0;
            }
            if (!preparation.RootElement.GetProperty("externalAnalysisEnabled").GetBoolean())
                throw new InvalidDataException("Extern behandling av CV och uppdrag är inte aktiverad. Granska datahanteringen i README först.");
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var analyzer = new OpenAiAssignmentAnalyzer(client, settings, budget, Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "");
            AnalysisResult result;
            try { result = await analyzer.AnalyzeAsync(input); }
            catch (InvalidDataException error) when (analyzer.LastResponse is not null)
            {
                // This is private diagnostic data, never a validated match or notification.
                var rejectedDirectory = Path.GetFullPath("data/private/rejected-analyses");
                Directory.CreateDirectory(rejectedDirectory);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(rejectedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var rejectedPath = Path.Combine(rejectedDirectory, prepared.Hash + ".json");
                await using var rejectedFile = new FileStream(rejectedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(rejectedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(rejectedFile, new { validated = false,
                    validationError = error.Message, response = analyzer.LastResponse }, AnalysisValidator.JsonOptions);
                throw new InvalidDataException($"{error.Message} Avvisat AI-svar sparat privat till {rejectedPath}. Ingen matchning eller e-post; reservationen behålls.");
            }
            if (filter.Status == "NeedsReview" && result.Recommendation == Recommendation.Apply)
                result = result with { Recommendation = Recommendation.Review,
                    Uncertainties = [..result.Uncertainties, "Lokala filter innehåller okända fakta; granska filterbeslutet."] };
            var envelope = new AnalysisEnvelope(assignment.Source, assignment.ExternalId, assignment.ContentHash,
                prepared.Input.CvHash, settings.Model, "1", DateTimeOffset.UtcNow, result);
            var directory = Path.GetFullPath("data/private/analyses");
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(directory, prepared.Hash + ".json");
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await JsonSerializer.SerializeAsync(file, new { envelope, analyzer.Usage, prepared.Hash, prepared.ReservedSek,
                settings, promptVersion = OpenAiAssignmentAnalyzer.PromptVersion, originalReviewedCvHash = cvHash, filter }, AnalysisValidator.JsonOptions);
            Console.WriteLine($"Analys: {result.Recommendation}, {result.Score}/100. Sparad till {path}. Ingen e-post.");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error is InvalidDataException or ArgumentException ? error.Message
                : "Analysen kunde inte genomföras. Kontrollera privata indata, konfiguration och budgetjournal. Ingen e-post.");
            return 1;
        }
    }
}
