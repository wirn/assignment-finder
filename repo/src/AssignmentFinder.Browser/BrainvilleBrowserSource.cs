using System.Runtime.CompilerServices;
using System.Text.Json;
using AssignmentFinder.Brainville;
using Microsoft.Playwright;

namespace AssignmentFinder.Browser;

public sealed record BrowserSourceSettings(bool Enabled, string SessionPath, string[] SearchUrls,
    int MaximumPages = 1, int MaximumAssignments = 20, int DelaySeconds = 5)
{
    public void Validate()
    {
        if (MaximumPages is < 1 or > 3 || MaximumAssignments is < 1 or > 60 || DelaySeconds is < 5 or > 60)
            throw new InvalidDataException("Ogiltiga gränser för webbläsarhämtning.");
        if (SearchUrls is null || SearchUrls.Length is < 1 or > 5 || string.IsNullOrWhiteSpace(SessionPath)
            || Path.GetFileName(SessionPath).Equals("passw.txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Privat sessionsfil och 1–5 sökningar krävs.");
        foreach (var url in SearchUrls) BrainvilleListParser.ValidateSearchUrl(url);
    }
}

public interface IBrainvillePageReader : IAsyncDisposable
{
    Task<string> ReadAsync(Uri url, bool detail, CancellationToken ct);
}

public sealed class BrowserSourceException(string state) : Exception("Brainville-hämtningen stoppades.")
{
    public string State { get; } = state;
}

public sealed class BrainvilleBrowserSource(string settingsPath,
    Func<BrowserSourceSettings, CancellationToken, Task<IBrainvillePageReader>>? readerFactory = null) : IAssignmentSource
{
    public string Name => "BrainvilleBrowser";

    public async IAsyncEnumerable<Assignment> FetchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Path.GetFileName(settingsPath).Equals("passw.txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Otillåtet konfigurationsfilnamn.");
        if (File.Exists(settingsPath) && new FileInfo(settingsPath).ResolveLinkTarget(true) is { } settingsTarget
            && settingsTarget.Name.Equals("passw.txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Otillåten konfigurationslänk.");
        var settings = JsonSerializer.Deserialize<BrowserSourceSettings>(await File.ReadAllTextAsync(settingsPath, cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Webbläsarkonfiguration saknas.");
        settings.Validate();
        if (!settings.Enabled) throw new BrowserSourceException("Disabled");
        await using var reader = await (readerFactory ?? PlaywrightPageReader.CreateAsync)(settings, cancellationToken);
        var links = new List<Uri>();
        var seen = new HashSet<string>();
        var parser = new BrainvilleListParser();
        foreach (var search in settings.SearchUrls)
        {
            var current = BrainvilleListParser.ValidateSearchUrl(search);
            var visited = new HashSet<string>();
            for (var number = 1; number <= settings.MaximumPages; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(current.AbsoluteUri)) throw new BrowserSourceException("StructureChanged");
                var html = await reader.ReadAsync(current, false, cancellationToken);
                foreach (var link in parser.Parse(html, current))
                    if (seen.Add(link.AbsoluteUri)) links.Add(link);
                if (links.Count >= settings.MaximumAssignments) break;
                var next = parser.ParseNextPage(html, current);
                if (next is null) break;
                current = next;
            }
            if (links.Count >= settings.MaximumAssignments) break;
        }
        foreach (var link in links.Take(settings.MaximumAssignments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var html = await reader.ReadAsync(link, true, cancellationToken);
            yield return new BrainvilleDetailParser().Parse(html, link, DateTimeOffset.UtcNow);
        }
        // A bounded collection never marks unobserved assignments closed.
    }
}

internal sealed class PlaywrightPageReader(IPlaywright playwright, IBrowser browser, IPage page, int delaySeconds) : IBrainvillePageReader
{
    private bool requested;
    public static async Task<IBrainvillePageReader> CreateAsync(BrowserSourceSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(settings.SessionPath)) throw new BrowserSourceException("NeedsLogin");
        if (new FileInfo(settings.SessionPath).ResolveLinkTarget(true) is { } target
            && target.Name.Equals("passw.txt", StringComparison.OrdinalIgnoreCase))
            throw new BrowserSourceException("InvalidConfiguration");
        var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var context = await browser.NewContextAsync(new() { StorageStatePath = settings.SessionPath, AcceptDownloads = false });
            context.SetDefaultTimeout(30_000);
            context.SetDefaultNavigationTimeout(30_000);
            return new PlaywrightPageReader(playwright, browser, await context.NewPageAsync(), settings.DelaySeconds);
        }
        catch
        {
            if (browser is not null) await browser.DisposeAsync();
            playwright.Dispose();
            throw new BrowserSourceException("BrowserUnavailable");
        }
    }

    public async Task<string> ReadAsync(Uri url, bool detail, CancellationToken ct)
    {
        if (requested) await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
        requested = true;
        ct.ThrowIfCancellationRequested();
        // Closing the context aborts in-flight Playwright calls when the request is cancelled.
        using var cancellation = ct.Register(() => { _ = page.Context.CloseAsync(); });
        try
        {
            var response = await page.GotoAsync(url.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await BrowserPageGuard.EnsureReadyAsync(page, url, detail
                ? "#MainTarget .l_tinymce_formatting"
                : "#MainContent #RequisitionStream [data-card][data-requisition-id]", response?.Status);
            ct.ThrowIfCancellationRequested();
            return await page.ContentAsync();
        }
        catch (InvalidDataException error)
        {
            throw new BrowserSourceException(error.Message == BrowserPageGuard.SessionExpired ? "NeedsLogin"
                : responseState(error.Message));
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            throw new BrowserSourceException("FetchFailed");
        }
        static string responseState(string message) => message.StartsWith("Åtkomst nekad", StringComparison.Ordinal)
            ? "AccessDenied" : "StructureChanged";
    }

    public async ValueTask DisposeAsync()
    {
        try { await browser.DisposeAsync(); }
        finally { playwright.Dispose(); }
    }
}
