using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

internal static class AppChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var appPath = Path.GetFullPath("src/AssignmentFinder.App/bin/Release/net10.0/AssignmentFinder.App.dll");
        if (!File.Exists(appPath)) throw new InvalidOperationException("Bygg hela solution i Release före API-kontrollerna.");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(appPath);
        start.Environment["ADMIN_API_KEY"] = "synthetic-local-check-key-32-characters";
        start.Environment["ConnectionStrings__Main"] = "Host=127.0.0.1;Port=1;Database=fixture;Username=fixture;Password=unused;Timeout=1;Command Timeout=1";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Kunde inte starta lokalt API-test.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            var live = false;
            for (var attempt = 0; attempt < 40 && !process.HasExited; attempt++)
            {
                try { live = (await client.GetAsync("/health/live")).StatusCode == HttpStatusCode.OK; }
                catch (HttpRequestException) { }
                if (live) break;
                await Task.Delay(100);
            }
            check(live, "API live fungerar utan databas, AI eller SMTP");
            check((await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.ServiceUnavailable,
                "API ready ger 503 vid otillgänglig databas");
            check((await client.GetAsync("/api/status")).StatusCode == HttpStatusCode.Unauthorized
                && (await client.PostAsync("/api/import/run", null)).StatusCode == HttpStatusCode.Unauthorized,
                "Status och manuell import kräver administratörsnyckel");
            check((await client.PostAsync("/api/brainville/run", null)).StatusCode == HttpStatusCode.Unauthorized
                && (await client.PostAsync("/api/pipeline/demo", null)).StatusCode == HttpStatusCode.Unauthorized
                && (await client.GetAsync("/api/assignments")).StatusCode == HttpStatusCode.Unauthorized
                && (await client.GetAsync("/api/analyses")).StatusCode == HttpStatusCode.Unauthorized
                && (await client.GetAsync("/api/previews/" + Guid.NewGuid())).StatusCode == HttpStatusCode.Unauthorized,
                "Hämtning, demopipeline, uppdrag, analyser och förhandsvisningar kräver nyckel");
            client.DefaultRequestHeaders.Add("X-Admin-Key", "wrong-key");
            check((await client.GetAsync("/api/status")).StatusCode == HttpStatusCode.Unauthorized, "Fel API-nyckel avvisas");
            client.DefaultRequestHeaders.Remove("X-Admin-Key");
            client.DefaultRequestHeaders.Add("X-Admin-Key", "synthetic-local-check-key-32-characters");
            var response = await client.GetAsync("/api/status");
            var body = await response.Content.ReadAsStringAsync();
            check(response.StatusCode == HttpStatusCode.ServiceUnavailable && !body.Contains("unused") && !body.Contains("fixture"),
                "Databasfel med korrekt nyckel ger generiskt svar utan anslutningsuppgifter");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await output; await errors;
        }
    }
}
