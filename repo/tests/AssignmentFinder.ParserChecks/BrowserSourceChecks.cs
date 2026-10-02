using System.Text.Json;
using AssignmentFinder.Browser;
using AssignmentFinder.Brainville;

internal static class BrowserSourceChecks
{
    public static async Task RunAsync(string listHtml, string detailHtml, Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "af-browser-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "browser.json");
        var settings = new BrowserSourceSettings(true, "synthetic-session.json",
            ["https://www.brainville.com/Market/RequisitionSearchResult?Filter.Text=test",
             "https://www.brainville.com/Market/RequisitionSearchResult?Filter.Text=other"], MaximumAssignments: 3);
        async Task Write(BrowserSourceSettings value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        async Task<List<Assignment>> Fetch(BrainvilleBrowserSource source, CancellationToken ct = default)
        {
            var items = new List<Assignment>();
            await foreach (var item in source.FetchAsync(ct)) items.Add(item);
            return items;
        }
        try
        {
            await Write(settings);
            var reader = new FixtureReader(listHtml, detailHtml);
            var items = await Fetch(new(path, (_, _) => Task.FromResult<IBrainvillePageReader>(reader)));
            check(items.Count == 2 && items.Select(a => a.ExternalId).Distinct().Count() == 2
                && reader.ListRequests == 2 && reader.DetailRequests == 2, "Webbläsarkälla deduplicerar mellan sökningar före detaljhämtning");
            check(reader.Disposed && items.All(a => a.Source == "Brainville"), "Webbläsarkälla normaliserar uppdrag och stänger läsaren");
            await Write(settings with { MaximumAssignments = 1 });
            var bounded = new FixtureReader(listHtml, detailHtml);
            check((await Fetch(new(path, (_, _) => Task.FromResult<IBrainvillePageReader>(bounded)))).Count == 1
                && bounded.DetailRequests == 1 && bounded.ListRequests == 1, "Webbläsarkälla respekterar uppdragsgränsen");
            await Write(settings with { Enabled = false });
            var created = false;
            try
            {
                await Fetch(new(path, (_, _) => { created = true; return Task.FromResult<IBrainvillePageReader>(reader); }));
                throw new Exception("Disabled source fetched");
            }
            catch (BrowserSourceException error) { check(error.State == "Disabled" && !created, "Avstängd källa öppnar ingen webbläsare"); }
            foreach (var invalid in new[] { settings with { DelaySeconds = 0 }, settings with { MaximumPages = 4 },
                settings with { MaximumAssignments = 61 }, settings with { SessionPath = "passw.txt" },
                settings with { SearchUrls = ["https://example.com/Market/RequisitionSearchResult"] } })
            {
                try { invalid.Validate(); throw new Exception("Invalid browser settings accepted"); }
                catch (Exception error) when (error is InvalidDataException or ArgumentException)
                { check(true, "Webbläsarkälla avvisar ogiltig frekvens, gräns, sessionsnamn eller domän"); }
            }
            await Write(settings);
            try { await Fetch(new(path, (_, _) => throw new BrowserSourceException("NeedsLogin"))); throw new Exception("Missing session accepted"); }
            catch (BrowserSourceException error) { check(error.State == "NeedsLogin", "Sessionsbehov bevaras som särskild källstatus"); }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await Fetch(new(path), cancellation.Token); throw new Exception("Cancelled source ran"); }
            catch (OperationCanceledException) { check(true, "Avbruten webbläsarkälla gör inga anrop"); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FixtureReader(string listHtml, string detailHtml) : IBrainvillePageReader
    {
        public int ListRequests { get; private set; }
        public int DetailRequests { get; private set; }
        public bool Disposed { get; private set; }
        public Task<string> ReadAsync(Uri url, bool detail, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!detail) { ListRequests++; return Task.FromResult(listHtml); }
            DetailRequests++;
            var id = url.AbsolutePath.Split('/')[^1];
            return Task.FromResult(detailHtml.Replace("Details/42", "Details/" + id));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
