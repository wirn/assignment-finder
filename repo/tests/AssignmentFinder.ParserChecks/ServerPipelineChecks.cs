using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal static class ServerPipelineChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var appPath = Path.GetFullPath("src/AssignmentFinder.App/bin/Release/net10.0/AssignmentFinder.App.dll");
        var directory = Path.Combine(Path.GetTempPath(), "af-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var key = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(appPath);
        start.Environment["ConnectionStrings__Main"] = connection;
        start.Environment["ADMIN_API_KEY"] = key;
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["ImportDirectory"] = directory;
        start.Environment["FilterPath"] = Path.Combine(directory, "filter.json");
        start.Environment["BrowserSettingsPath"] = Path.Combine(directory, "browser.json");
        await File.WriteAllTextAsync(start.Environment["FilterPath"]!, "{\"allowedLocations\":[\"Stockholm\"],\"allowRemote\":true,\"minimumExtentPercent\":20}");
        await File.WriteAllTextAsync(start.Environment["BrowserSettingsPath"]!,
            "{\"enabled\":false,\"sessionPath\":\"unused.json\",\"searchUrls\":[\"https://www.brainville.com/Market/RequisitionSearchResult?Filter.Text=test\"]}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Add("X-Admin-Key", key);
            var ready = false;
            for (var attempt = 0; attempt < 60 && !process.HasExited; attempt++)
            {
                try { ready = (await client.GetAsync("/health/ready")).IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                if (ready) break;
                await Task.Delay(100);
            }
            check(ready, "PostgreSQL/API: appen blir ready i isolerat testschema");
            var sourceDisabled = await client.PostAsync("/api/brainville/run", null);
            using var sourceResult = JsonDocument.Parse(await sourceDisabled.Content.ReadAsStringAsync());
            check(sourceDisabled.IsSuccessStatusCode && sourceResult.RootElement.GetProperty("state").GetString() == "Disabled",
                "PostgreSQL/API: avstängd webbläsarkälla registreras utan kontoanrop");
            var local = await client.PostAsync("/api/import/run", null);
            using var localResult = JsonDocument.Parse(await local.Content.ReadAsStringAsync());
            check(local.IsSuccessStatusCode && localResult.RootElement.GetProperty("state").GetString() == "Completed",
                "PostgreSQL/API: källfel hindrar inte nästa lokala import");
            var first = await client.PostAsync("/api/pipeline/demo", null);
            var second = await client.PostAsync("/api/pipeline/demo", null);
            using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            check(first.IsSuccessStatusCode && second.IsSuccessStatusCode
                && firstJson.RootElement.GetProperty("analysisId").GetGuid() == secondJson.RootElement.GetProperty("analysisId").GetGuid()
                && firstJson.RootElement.GetProperty("previewId").GetGuid() == secondJson.RootElement.GetProperty("previewId").GetGuid(),
                "PostgreSQL/API: demopipeline deduplicerar analys och förhandsvisning");
            var preview = await client.GetAsync("/api/previews/" + firstJson.RootElement.GetProperty("previewId").GetGuid());
            using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            check(preview.IsSuccessStatusCode && previewJson.RootElement.GetProperty("mode").GetString() == "SyntheticFixture"
                && previewJson.RootElement.GetProperty("state").GetString() == "Preview"
                && previewJson.RootElement.GetProperty("message").GetProperty("textBody").GetString()!.Contains("Syntetisk"),
                "PostgreSQL/API: syntetisk svensk e-postförhandsvisning kan läsas utan utskick");
            var assignments = await client.GetAsync("/api/assignments");
            check(assignments.IsSuccessStatusCode, "PostgreSQL/API: skyddad uppdragsöversikt läser senaste revisioner");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await stdout; await stderr;
            Directory.Delete(directory, recursive: true);
        }
    }
}
