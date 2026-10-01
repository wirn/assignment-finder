using System.Text.Json;
using System.Text.RegularExpressions;
using AssignmentFinder.Brainville;
using Microsoft.Playwright;

internal static class BrowserCommands
{
    private static readonly string DataRoot = Path.GetFullPath("data/private");
    private static readonly string SessionPath = Path.Combine(DataRoot, "brainville-session.json");

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["session-check"]) return await BrowserSessionChecks.RunAsync();
            if (args is ["browser-check"])
            {
                using var localPlaywright = await Playwright.CreateAsync();
                await using var localBrowser = await localPlaywright.Chromium.LaunchAsync();
                var localPage = await localBrowser.NewPageAsync();
                await localPage.SetContentAsync("<h1>Lokalt test</h1>");
                if (await localPage.Locator("h1").InnerTextAsync() != "Lokalt test")
                    throw new InvalidDataException("Lokalt webbläsartest misslyckades.");
                Console.WriteLine("PASS: Chromium startar och läser lokal HTML. Inga Brainville-anrop.");
                return 0;
            }
            var maxPages = 1;
            if (args[0] == "fetch-list" && args.Length == 4 && args[2] == "--pages"
                && int.TryParse(args[3], out var requestedPages) && requestedPages is >= 1 and <= 3)
                maxPages = requestedPages;
            else if (args[0] == "fetch-list" && args.Length != 2)
                throw new ArgumentException("Använd fetch-list <sök-URL-fil> [--pages 1–3].");
            if (args[0] == "login" && args.Length != 2 || args[0] == "fetch" && args.Length is < 2 or > 4)
                throw new ArgumentException("Använd login <URL> eller fetch <URL> [URL2] [URL3].");
            var searchUrl = args[0] == "fetch-list"
                ? BrainvilleListParser.ValidateSearchUrl((await File.ReadAllTextAsync(args[1])).Trim()) : null;
            var urls = searchUrl is null ? args.Skip(1).Select(ValidateUrl).DistinctBy(u => u.AbsolutePath).ToArray() : [];
            if (Environment.GetEnvironmentVariable("BRAINVILLE_BROWSER_ACCESS_CONFIRMED") != "true")
                throw new InvalidDataException("Klarlägg tillåtelse för automatiserad webbläsarhämtning hos Brainville innan körning. Se README.");
            if (args[0] != "login" && !File.Exists(SessionPath))
                throw new InvalidDataException("Session saknas. Kör login först.");
            var filterSettings = args[0] != "login" ? await FilterCommands.LoadAsync() : null;
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = args[0] != "login" });
            await using var context = await browser.NewContextAsync(new()
            {
                StorageStatePath = args[0] != "login" ? SessionPath : null,
                AcceptDownloads = false
            });
            context.SetDefaultTimeout(30_000);
            var page = await context.NewPageAsync();
            if (args[0] == "login")
            {
                await page.GotoAsync(urls[0].ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                Console.WriteLine("Logga in manuellt i webbläsaren, öppna angivet uppdrag och tryck Enter här. Lösenord läses inte av programmet.");
                if (Console.ReadLine() is null) throw new InvalidDataException("Inloggningen avbröts; sessionen sparades inte.");
                await ReadAssignmentAsync(page, urls[0]);
                var state = await context.StorageStateAsync(new() { IndexedDB = true });
                await WritePrivateAsync(SessionPath, state, replace: true);
                Console.WriteLine("Session verifierad mot detaljsidan och sparad lokalt under data/private. Kör login igen när den går ut.");
                return 0;
            }
            var runDirectory = Path.Combine(DataRoot, "imports", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8]);
            if (searchUrl is not null)
            {
                var allLinks = new List<Uri>();
                var listParser = new BrainvilleListParser();
                var currentSearch = searchUrl;
                var visited = new HashSet<string>();
                for (var pageNumber = 1; pageNumber <= maxPages; pageNumber++)
                {
                    if (!visited.Add(currentSearch.AbsoluteUri)) throw new InvalidDataException("Sidloopen stoppades.");
                    var response = await page.GotoAsync(currentSearch.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await BrowserPageGuard.EnsureReadyAsync(page, currentSearch,
                        "#MainContent #RequisitionStream [data-card][data-requisition-id]", response?.Status);
                    var html = await page.ContentAsync();
                    var pageLinks = listParser.Parse(html, currentSearch);
                    allLinks.AddRange(pageLinks);
                    Console.WriteLine($"Listsida {pageNumber}: {pageLinks.Count} uppdragslänkar.");
                    if (pageNumber == maxPages) break;
                    var next = listParser.ParseNextPage(html, currentSearch);
                    if (next is null) break;
                    currentSearch = next;
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                urls = allLinks.Distinct().Take(3).ToArray();
                await WritePrivateAsync(Path.Combine(runDirectory, "list-links.json"),
                    JsonSerializer.Serialize(urls, new JsonSerializerOptions { WriteIndented = true }), replace: false);
                Console.WriteLine($"Listinsamlingen gav {allLinks.Distinct().Count()} unika uppdrag. Hämtar de första {urls.Length}.");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            foreach (var url in urls)
            {
                var response = await page.GotoAsync(url.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                var assignment = await ReadAssignmentAsync(page, url, response?.Status);
                var output = Path.Combine(runDirectory, $"brainville-{assignment.ExternalId}.json");
                await WritePrivateAsync(output, JsonSerializer.Serialize(assignment,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), replace: false);
                Console.WriteLine($"Hämtat uppdrag {assignment.ExternalId} till {output}.");
                var decision = new AssignmentFilter().Evaluate(assignment, filterSettings!);
                await WritePrivateAsync(Path.Combine(runDirectory, $"filter-{assignment.ExternalId}.json"),
                    JsonSerializer.Serialize(decision, FilterCommands.JsonOptions), replace: false);
                Console.WriteLine($"Filter: {decision.Status}. AI-analys är ännu inte aktiverad.");
                if (url != urls[^1]) await Task.Delay(TimeSpan.FromSeconds(5));
            }
            return 0;
        }
        catch (PlaywrightException)
        {
            // Browser errors may contain URLs, HTML or authentication values; never print them.
            Console.Error.WriteLine("Webbläsarkörningen misslyckades eller tog för lång tid. Kontrollera Chromium-installationen och kör login igen vid utgången session. Inga skydd kringgås.");
            return 1;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine(error is JsonException ? "Sessionsfilen är ogiltig. Kör login igen." : error.Message);
            return 1;
        }
    }

    private static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https"
            || url.Host != "www.brainville.com" || !url.IsDefaultPort || url.UserInfo.Length != 0
            || !Regex.IsMatch(url.AbsolutePath, @"^/Market/RequisitionSearchResult/Details/\d+/?$"))
            throw new ArgumentException("Ange en HTTPS-detaljlänk på www.brainville.com.");
        return url;
    }

    private static async Task<Assignment> ReadAssignmentAsync(IPage page, Uri url, int? status = 200)
    {
        await BrowserPageGuard.EnsureReadyAsync(page, url, "#MainTarget .l_tinymce_formatting", status);
        try { return new BrainvilleDetailParser().Parse(await page.ContentAsync(), url, DateTimeOffset.UtcNow); }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("Detaljsidan kunde inte verifieras. Sessionen kan vara utgången eller sidstrukturen ändrad; ingen tom uppdragslista registreras.");
        }
    }

    private static async Task WritePrivateAsync(string path, string content, bool replace)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            await using (var writer = new StreamWriter(stream)) await writer.WriteAsync(content);
            File.Move(temporary, path, overwrite: replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
