using System.Text.Json;
using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;

internal static class RevalidationCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length is < 3 or > 4) throw new ArgumentException("Använd analysis-recheck <uppdrags-JSON> <avvisat-svar> [citatkorrigeringar]. Endast lokal kontroll.");
            var assignment = JsonSerializer.Deserialize<Assignment>(await File.ReadAllTextAsync(args[1]), FilterCommands.JsonOptions)
                ?? throw new InvalidDataException("Uppdrag saknas.");
            var cv = (await File.ReadAllTextAsync("data/private/cv/cv-matching-draft.md")).Replace("\r\n", "\n");
            var originalInput = new AnalysisInput(assignment, cv);
            using var preparation = JsonDocument.Parse(await File.ReadAllTextAsync("data/private/cv/preparation.json"));
            if (!preparation.RootElement.GetProperty("reviewedByUser").GetBoolean()
                || !originalInput.CvHash.Equals(preparation.RootElement.GetProperty("matchingDraftSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CV-utkastet måste granskas på nytt.");
            var settings = JsonSerializer.Deserialize<OpenAiSettings>(await File.ReadAllTextAsync("data/private/openai-settings.json"), AnalysisValidator.JsonOptions)
                ?? throw new InvalidDataException("AI-konfiguration saknas.");
            var minimizedInput = OpenAiAssignmentAnalyzer.Prepare(originalInput, settings).Input;
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(args[2]));
            var response = saved.RootElement.GetProperty("response").Deserialize<CapturedAnalysisResponse>(AnalysisValidator.JsonOptions)
                ?? throw new InvalidDataException("Sparat providersvar saknas.");
            var corrections = args.Length == 4
                ? JsonSerializer.Deserialize<EvidenceQuoteCorrection[]>(await File.ReadAllTextAsync(args[3]), AnalysisValidator.JsonOptions)
                    ?? throw new InvalidDataException("Citatkorrigeringar saknas.")
                : [];
            var result = AnalysisRevalidation.Validate(response, minimizedInput, corrections);
            var filter = new AssignmentFilter().Evaluate(assignment, await FilterCommands.LoadAsync());
            if (!filter.ContinueToAnalysis) throw new InvalidDataException("Uppdraget avvisas av nuvarande filter.");
            if (filter.Status == "NeedsReview" && result.Recommendation == Recommendation.Apply)
                result = result with { Recommendation = Recommendation.Review,
                    Uncertainties = [..result.Uncertainties, "Lokala filter innehåller okända fakta."] };
            var envelope = new AnalysisEnvelope(assignment.Source, assignment.ExternalId, assignment.ContentHash,
                minimizedInput.CvHash, response.Model, "1", DateTimeOffset.UtcNow, result);
            var directory = Path.GetFullPath("data/private/rechecked-analyses");
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await JsonSerializer.SerializeAsync(file, new { validated = true, localRevalidation = true,
                originalRejectedResponsePath = Path.GetFullPath(args[2]), response.RequestHash, response.Usage,
                response.PromptVersion, corrections, filter, envelope }, AnalysisValidator.JsonOptions);
            Console.WriteLine($"Lokal omkontroll: {result.Recommendation}, {result.Score}/100. Sparad till {path}.");
            Console.WriteLine("Ingen AI-begäran, ingen ny kostnad, ingen e-post. Originalsvaret och budgetjournalen är bevarade.");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException or KeyNotFoundException)
        {
            Console.Error.WriteLine(error is InvalidDataException or ArgumentException ? error.Message
                : "Lokal omkontroll misslyckades. Kontrollera privata indata och sparat svar.");
            return 1;
        }
    }
}
