using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace AssignmentFinder.Brainville;

public sealed class BrainvilleListParser
{
    public Uri? ParseNextPage(string html, Uri searchUrl)
    {
        ValidateSearchUrl(searchUrl.ToString());
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var links = document.DocumentNode.SelectNodes("//*[@id='MainContent']//*[@id='RequisitionStream']//a[@data-ajaxlink='#RequisitionStream']")?
            .Where(n => n.GetAttributeValue("class", "").Split(' ').Contains("pager_next")).ToArray() ?? [];
        if (links.Length == 0) return null;
        if (links.Length != 1 || !Uri.TryCreate(searchUrl,
                HtmlEntity.DeEntitize(links[0].GetAttributeValue("href", "")), out var next))
            throw new InvalidDataException("Nästa sidlänk kunde inte verifieras.");
        ValidateSearchUrl(next.ToString());
        Dictionary<string, string> Query(Uri url)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (!fields.TryAdd(System.Net.WebUtility.UrlDecode(pair[0]),
                    System.Net.WebUtility.UrlDecode(pair.Length == 2 ? pair[1] : "")))
                    throw new InvalidDataException("Sidans sökparametrar är tvetydiga.");
            }
            return fields;
        }
        var before = Query(searchUrl);
        var after = Query(next);
        var currentPage = before.TryGetValue("page", out var current) && int.TryParse(current, out var page) ? page : 1;
        if (!after.TryGetValue("page", out var value) || !int.TryParse(value, out var nextPage)
            || currentPage < 1 || nextPage != currentPage + 1)
            throw new InvalidDataException("Nästa sidlänk går inte till följande sida.");
        foreach (var key in new[] { "page", "newsearch" }) { before.Remove(key); after.Remove(key); }
        if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var other) || other != pair.Value))
            throw new InvalidDataException("Nästa sidlänk ändrar sökfilter; hämtningen stoppas.");
        return next;
    }

    public static Uri ValidateSearchUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https"
            || url.Host != "www.brainville.com" || !url.IsDefaultPort || url.UserInfo.Length != 0
            || url.AbsolutePath.TrimEnd('/') != "/Market/RequisitionSearchResult")
            throw new ArgumentException("Ange en HTTPS-söklänk på www.brainville.com.");
        return url;
    }

    public IReadOnlyList<Uri> Parse(string html, Uri searchUrl)
    {
        ValidateSearchUrl(searchUrl.ToString());
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var main = document.DocumentNode.SelectSingleNode("//*[@id='MainContent']")
            ?? throw new InvalidDataException("Sökresultatets MainContent saknas; kontrollera inloggning och sidstruktur.");
        var stream = main.SelectSingleNode(".//*[@id='RequisitionStream']")
            ?? throw new InvalidDataException("Resultatlistan saknas; kontrollera inloggning och sidstruktur.");
        var cards = stream.Descendants().Where(n => n.Attributes["data-requisition-id"] is not null
            && n.GetAttributeValue("data-card", "").Split(' ').Contains("card")
            && !n.Ancestors().Any(a => a.Attributes["data-requisition-id"] is not null)).ToArray();
        if (cards.Length == 0)
            throw new InvalidDataException("Inga verifierbara uppdragskort hittades. Tomma resultat är ännu inte stödda och registreras inte som lyckad hämtning.");
        var urls = new List<Uri>();
        foreach (var card in cards)
        {
            var id = card.GetAttributeValue("data-requisition-id", "");
            if (!Regex.IsMatch(id, @"^\d+$")) throw new InvalidDataException("Ett uppdragskort har ogiltigt ID.");
            var expectedPath = $"/Market/RequisitionSearchResult/Details/{id}";
            var matches = card.Descendants("a")
                .Where(n => n.GetAttributeValue("data-font-type", "") == "title")
                .Select(n => HtmlEntity.DeEntitize(n.GetAttributeValue("href", "")))
                .Select(href => Uri.TryCreate(searchUrl, href, out var link) ? link : null)
                .Where(link => link is not null && link.Scheme == searchUrl.Scheme
                    && link.Authority == searchUrl.Authority && link.UserInfo.Length == 0
                    && link.AbsolutePath.TrimEnd('/') == expectedPath).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("Uppdragskortets titellänk kunde inte verifieras mot dess ID.");
            urls.Add(new Uri("https://www.brainville.com" + expectedPath));
        }
        return urls.Distinct().ToArray();
    }
}
