using Microsoft.Playwright;

internal static class BrowserSessionChecks
{
    public static async Task<int> RunAsync()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        // Fresh contexts, no stored session. Every browser request is fulfilled locally.
        var detailUrl = new Uri("https://session-check.invalid/Market/RequisitionSearchResult/Details/42");
        var listUrl = new Uri("https://session-check.invalid/Market/RequisitionSearchResult");
        async Task CheckAsync(string name, Uri url, string html, int status, string? expectedError,
            bool redirect = false)
        {
            await using var context = await browser.NewContextAsync();
            context.SetDefaultTimeout(1_000);
            await context.RouteAsync("**/*", async route =>
            {
                if (redirect && new Uri(route.Request.Url).AbsolutePath == url.AbsolutePath)
                    await route.FulfillAsync(new() { Status = 200, ContentType = "text/html",
                        Body = "<script>location.replace('/Account/Login')</script>" });
                else await route.FulfillAsync(new() { Status = status, ContentType = "text/html", Body = html });
            });
            var page = await context.NewPageAsync();
            IResponse? response;
            try { response = await page.GotoAsync(url.ToString()); }
            catch (PlaywrightException error) { throw new InvalidDataException($"Lokalt test {name}: {error.Message}"); }
            if (redirect) await page.WaitForURLAsync("**/Account/Login");
            var selector = url == listUrl ? "#RequisitionStream [data-requisition-id]" : "#MainTarget .l_tinymce_formatting";
            var accepted = false;
            try
            {
                await BrowserPageGuard.EnsureReadyAsync(page, url, selector, response?.Status);
                accepted = true;
            }
            catch (InvalidDataException error)
            {
                if (expectedError is null || !error.Message.Contains(expectedError, StringComparison.Ordinal))
                    throw new InvalidDataException($"FAILED: {name}");
            }
            if (accepted != (expectedError is null)) throw new InvalidDataException($"FAILED: {name}");
            Console.WriteLine($"PASS: {name}");
        }
        const string login = "<form><input type='password'><button>Logga in</button></form>";
        foreach (var url in new[] { detailUrl, listUrl })
        {
            var type = url == listUrl ? "lista" : "detalj";
            await CheckAsync($"{type}: HTTP 401 stoppar hämtning", url, "Utloggad", 401, BrowserPageGuard.SessionExpired);
            await CheckAsync($"{type}: omdirigering till login upptäcks", url, login, 200, BrowserPageGuard.SessionExpired, redirect: true);
            await CheckAsync($"{type}: login på samma URL upptäcks", url, login, 200, BrowserPageGuard.SessionExpired);
            await CheckAsync($"{type}: ändrad struktur skiljs från utloggning", url, "<main>Ändrat innehåll</main>", 200, BrowserPageGuard.StructureChanged);
            await CheckAsync($"{type}: HTTP 403 ger åtkomstfel", url, "Åtkomst nekad", 403, "Åtkomst nekad");
        }
        await CheckAsync("Giltig detalj accepteras", detailUrl,
            "<main id='MainTarget'><div class='l_tinymce_formatting'>Beskrivning</div></main>", 200, null);
        await CheckAsync("Giltig lista accepteras även med dolt lösenordsfält", listUrl,
            "<div id='RequisitionStream'><div data-requisition-id='42'>Uppdrag</div></div><input type='password' hidden>", 200, null);
        Console.WriteLine("12 lokala sessionstester passerar. Inga Brainville-anrop eller sessionsfiler används.");
        return 0;
    }
}
