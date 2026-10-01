using Microsoft.Playwright;

internal static class BrowserPageGuard
{
    public const string SessionExpired = "Sessionen har gått ut eller du är utloggad — kör login igen.";
    public const string StructureChanged = "Sidans förväntade innehåll saknas eller laddades inte i tid. Kontrollera sidstrukturen; ingen tom uppdragslista registreras.";

    public static async Task EnsureReadyAsync(IPage page, Uri expectedUrl, string selector, int? status)
    {
        async Task CheckSessionAsync()
        {
            var isLoginUrl = Uri.TryCreate(page.Url, UriKind.Absolute, out var current)
                && current.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment.Equals("login", StringComparison.OrdinalIgnoreCase)
                        || segment.Equals("signin", StringComparison.OrdinalIgnoreCase));
            if (status == 401 || isLoginUrl || await page.Locator("input[type='password']:visible").CountAsync() > 0)
                throw new InvalidDataException(SessionExpired);
        }

        await CheckSessionAsync();
        if (status == 403)
            throw new InvalidDataException("Åtkomst nekad. Kontrollera behörighet eller hantera eventuellt skydd manuellt; hämtningen har stoppats.");
        if (status is null or < 200 or >= 300)
            throw new InvalidDataException("Hämtningen stoppades av ett HTTP-fel. Ingen automatisk omkörning görs.");
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var currentUrl)
            || currentUrl.Scheme != expectedUrl.Scheme || currentUrl.Authority != expectedUrl.Authority
            || currentUrl.AbsolutePath.TrimEnd('/') != expectedUrl.AbsolutePath.TrimEnd('/'))
            throw new InvalidDataException("Oväntad omdirigering. Kontrollera åtkomst manuellt; hämtningen har stoppats.");
        try
        {
            await page.Locator($"{selector}, input[type='password']:visible").First.WaitForAsync();
        }
        catch (TimeoutException)
        {
            await CheckSessionAsync();
            throw new InvalidDataException(StructureChanged);
        }
        await CheckSessionAsync();
    }
}
