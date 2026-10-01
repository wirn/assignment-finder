using System.Text.Json;
using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;

if (args is ["--help"] or ["-h"] || args.Length == 0)
{
    Console.WriteLine("Lokal Brainville-import (inga nätverksanrop):");
    Console.WriteLine("parse <html-fil> <uppdragets HTTPS-URL> <json-fil>");
    Console.WriteLine("login <uppdragets HTTPS-URL> (manuell inloggning och lokal session)");
    Console.WriteLine("fetch <uppdragets HTTPS-URL> [URL2] [URL3]");
    Console.WriteLine("parse-list <html-fil> <sök-URL-fil> <json-fil> (lokalt)");
    Console.WriteLine("fetch-list <sök-URL-fil> [--pages 1–3] (första tre unika uppdrag)");
    Console.WriteLine("browser-check (lokalt webbläsartest utan Brainville-anrop)");
    Console.WriteLine("session-check (syntetiska sessionstester utan Brainville-anrop)");
    Console.WriteLine("filter <uppdrags-JSON> [filterkonfiguration] (lokalt, inga AI-anrop)");
    Console.WriteLine("analysis-demo (syntetiska mock-analyser, lokalt utan CV/AI/e-post)");
    Console.WriteLine("notification-demo (syntetiska notisbeslut och svensk e-postförhandsvisning, inga utskick)");
    Console.WriteLine("analyze <uppdrags-JSON> [--send] (lokal kontroll som standard; --send kräver aktiverad AI)");
    Console.WriteLine("analysis-recheck <uppdrags-JSON> <avvisat-svar> [citatkorrigeringar] (lokalt, inga AI-anrop)");
    return 0;
}
if (args[0] == "filter") return await FilterCommands.RunAsync(args);
if (args is ["notification-demo"]) return await NotificationCommands.DemoAsync();
if (args[0] == "analyze") return await AnalysisCommands.RunAsync(args);
if (args[0] == "analysis-recheck") return await RevalidationCommands.RunAsync(args);
if (args is ["analysis-demo"])
{
    var directory = Path.GetFullPath(Path.Combine("data/private/analysis-demo", Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(directory);
    foreach (var demo in AnalysisDemo.Cases())
    {
        var result = await new MockAssignmentAnalyzer(demo.Result).AnalyzeAsync(demo.Input);
        var envelope = new AnalysisEnvelope(demo.Input.Assignment.Source, demo.Input.Assignment.ExternalId,
            demo.Input.Assignment.ContentHash, demo.Input.CvHash, "Mock-synthetic-fixture", "1", DateTimeOffset.UtcNow, result);
        var path = Path.Combine(directory, demo.Name + ".json");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, envelope, AnalysisValidator.JsonOptions);
        Console.WriteLine($"Syntetiskt test {demo.Name}: {result.Recommendation}, {result.Score}/100. {path}");
    }
    Console.WriteLine("Demonstration klar. Resultaten är förberedda testfall, inte bedömningar av ditt CV.");
    return 0;
}
if (args[0] is "login" or "fetch" or "fetch-list" or "browser-check" or "session-check")
    return await BrowserCommands.RunAsync(args);
if (args.Length == 4 && args[0] == "parse-list")
{
    try
    {
        var searchUrl = BrainvilleListParser.ValidateSearchUrl((await File.ReadAllTextAsync(args[2])).Trim());
        var listHtml = await File.ReadAllTextAsync(args[1]);
        var parser = new BrainvilleListParser();
        var links = parser.Parse(listHtml, searchUrl);
        var nextPage = parser.ParseNextPage(listHtml, searchUrl);
        var output = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await JsonSerializer.SerializeAsync(stream, links, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Verifierade {links.Count} unika uppdragslänkar. Sparat till {output}.");
        Console.WriteLine(nextPage is null ? "Ingen nästa sidlänk hittades." : "Nästa sidlänk verifierad med samma sökfilter.");
        return 0;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}
if (args.Length != 4 || args[0] != "parse")
{
    Console.Error.WriteLine("Ogiltiga argument. Använd --help.");
    return 2;
}
try
{
    var assignment = new BrainvilleDetailParser().Parse(await File.ReadAllTextAsync(args[1]),
        new Uri(args[2], UriKind.Absolute), DateTimeOffset.UtcNow);
    var output = Path.GetFullPath(args[3]);
    if (File.Exists(output)) throw new IOException("Utdatafilen finns redan. Välj ett nytt filnamn.");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    await JsonSerializer.SerializeAsync(file, assignment, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    Console.WriteLine($"Importerade uppdrag {assignment.ExternalId} till {output}.");
    Console.WriteLine($"Krav: {assignment.MandatoryRequirements.Count}, meriterande: {assignment.PreferredQualifications.Count}, okänd kravstatus: {assignment.UnclassifiedRequirements.Count}.");
    foreach (var warning in assignment.Warnings) Console.WriteLine($"Varning: {warning}");
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UriFormatException or UnauthorizedAccessException)
{
    // Do not print input HTML or a stack trace containing private content.
    Console.Error.WriteLine($"Import misslyckades: {error.Message}");
    return 1;
}
