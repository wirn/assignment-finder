using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace AssignmentFinder.Brainville;

public sealed class BrainvilleDetailParser
{
    public Assignment Parse(string html, Uri sourceUrl, DateTimeOffset importedAtUtc)
    {
        var idMatch = Regex.Match(sourceUrl.AbsolutePath,
            @"^/Market/RequisitionSearchResult/Details/(\d+)/?$", RegexOptions.IgnoreCase);
        if (sourceUrl.Scheme != "https" || sourceUrl.Host != "www.brainville.com" || !idMatch.Success)
            throw new ArgumentException("Ange en HTTPS-länk till en uppdragsdetaljsida på www.brainville.com.");

        var document = new HtmlDocument();
        document.LoadHtml(html);
        var main = document.DocumentNode.SelectSingleNode("//*[@id='MainContent']")
            ?? throw new InvalidDataException("MainContent saknas. Sidan kan vara utloggad eller ha ändrad struktur.");
        var header = main.Descendants().SingleOrDefault(n => HasClass(n, "c_product_header"))
            ?? throw new InvalidDataException("Uppdragets rubrikområde saknas.");
        var title = Text(header.SelectSingleNode(".//h1"));
        if (title.Length == 0) throw new InvalidDataException("Uppdragets titel saknas.");

        // Scope to the assignment body; never fall back to all page text.
        var target = main.SelectSingleNode(".//*[@id='MainTarget']")
            ?? throw new InvalidDataException("Uppdragets detaljområde saknas.");
        var descriptions = target.Descendants().Where(n => HasClass(n, "l_tinymce_formatting")).ToArray();
        if (descriptions.Length != 1)
            throw new InvalidDataException("Förväntade exakt en uppdragsbeskrivning. Kontrollera sidstrukturen.");
        var body = descriptions[0].CloneNode(true);
        foreach (var unwanted in body.Descendants().Where(n =>
                     n.Name is "script" or "style" or "input" or "form" or "button" or "template"
                     || n.GetAttributeValue("aria-hidden", "") == "true"
                     || n.Attributes["hidden"] is not null).ToArray())
            unwanted.Remove();
        var description = MultilineText(body);
        if (description.Length == 0) throw new InvalidDataException("Uppdragsbeskrivningen är tom.");

        var warnings = new List<string>();
        var facts = body.Descendants("li").Select(Text).ToArray();
        string? Fact(params string[] labels)
        {
            var values = facts.Where(x => labels.Any(label => x.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Split(':', 2)[1].Trim()).ToList();
            foreach (var node in body.Descendants("span").Where(n => !n.Descendants("span").Any()
                         && labels.Contains(Text(n), StringComparer.OrdinalIgnoreCase)))
            {
                var next = node.NextSibling;
                while (next is not null && (next.Name == "br" || Text(next).Length == 0)) next = next.NextSibling;
                if (next?.Name == "span") values.Add(Text(next));
            }
            var known = values.Where(v => v.Length > 0 && v != "N/A").Distinct().ToArray();
            if (known.Length > 1) warnings.Add($"Motstridiga värden för {labels[0]}; värdet lämnas okänt.");
            return known.Length == 1 ? known[0] : null;
        }
        var start = ParseDate(Fact("Start"), "startdatum", warnings);
        var end = ParseDate(Fact("Slut", "End"), "slutdatum", warnings);
        var deadlines = Regex.Matches(description, @"SVAR SENAST\s+(\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        var deadline = deadlines.Length == 1 ? ParseDate(deadlines[0], "sista svarsdag", warnings) : null;
        if (deadlines.Length > 1) warnings.Add("Flera sista svarsdatum hittades; värdet behöver granskas.");

        var metadata = target.Descendants().Where(n => HasClass(n, "c_chip")).Select(Text)
            .Where(s => s.Length > 0).Distinct().ToArray();
        var explicitSidebarDates = metadata.Select(s => DateOnly.TryParseExact(s, "d MMMM yyyy",
                CultureInfo.GetCultureInfo("sv-SE"), DateTimeStyles.None, out var date) ? (DateOnly?)date : null)
            .Where(d => d.HasValue).Select(d => d!.Value).ToArray();
        if (end.HasValue && explicitSidebarDates.Any(d => d != end.Value))
            warnings.Add("Datum i sidans metadata avviker från beskrivningens slutdatum. Metadatafältets betydelse behöver granskas.");
        if (start.HasValue && end.HasValue && end < start)
            warnings.Add("Beskrivningens slutdatum ligger före startdatum.");
        if (start is null) warnings.Add("Inget entydigt startdatum kunde läsas ur beskrivningens fakta.");
        if (end is null) warnings.Add("Inget entydigt slutdatum kunde läsas ur beskrivningens fakta.");

        var company = header.Descendants("a").FirstOrDefault(n =>
            n.GetAttributeValue("href", "").StartsWith("/Network/PublicProfile/Index/", StringComparison.Ordinal));
        var canonicalUrl = new Uri($"https://www.brainville.com/Market/RequisitionSearchResult/Details/{idMatch.Groups[1].Value}");
        var canonicalLinks = main.Descendants("a").Where(n => Text(n) == "Översikt")
            .Select(n => HtmlEntity.DeEntitize(n.GetAttributeValue("href", "")))
            .Select(href => Uri.TryCreate(canonicalUrl, href, out var link) ? link : null)
            .Where(link => link is not null && link.Scheme == canonicalUrl.Scheme
                && link.Authority == canonicalUrl.Authority)
            .ToArray();
        if (!canonicalLinks.Any(link => link!.AbsolutePath.TrimEnd('/') == canonicalUrl.AbsolutePath))
            throw new InvalidDataException("HTML-filens översiktslänk matchar inte angivet uppdrags-ID.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + Text(company) + "\n" + description)));
        var unclassified = Requirements(body, "Skills", "Kompetenser och färdigheter");
        if (unclassified.Count > 0)
            warnings.Add("Kompetensavsnitt har okänd kravstatus; de har inte markerats som obligatoriska.");
        if (Regex.IsMatch(description, @"\bhybrid\b", RegexOptions.IgnoreCase)
            && metadata.Contains("Ej distansarbete", StringComparer.OrdinalIgnoreCase))
            warnings.Add("Beskrivningen anger hybrid men metadata anger Ej distansarbete; arbetsformen behöver granskas.");
        string? ChipFact(string icon) => target.Descendants().Where(n => HasClass(n, "c_chip")
                && n.Descendants().Any(child => HasClass(child, icon)))
            .Select(Text).Where(s => s.Length > 0).Distinct().ToArray() is [var value] ? value : null;
        return new Assignment("Brainville", idMatch.Groups[1].Value, canonicalUrl, importedAtUtc.ToUniversalTime(),
            title, company is null ? null : Text(company), description, hash,
            Fact("Plats", "Location") ?? ChipFact("fa-location-dot"),
            Fact("Arbetsform") ?? ChipFact("fa-house-user"),
            Fact("Omfattning", "Extent") ?? ChipFact("fa-watch"), start, end, deadline,
            Requirements(body, "Obligatoriska krav", "Required Skills & Experience", "Särskilda skallkrav/särskild kravprofil"),
            Requirements(body, "Meriterande", "Good to have", "Nice to have", "Utvärderingskriterier"), metadata, warnings)
            { UnclassifiedRequirements = unclassified };
    }

    private static IReadOnlyList<string> Requirements(HtmlNode body, params string[] labels)
    {
        string[] boundaries = ["Obligatoriska krav", "Meriterande", "Skills", "Required Skills & Experience",
            "Good to have", "Nice to have", "Personal skills", "Education", "About the role", "Application", "Responsibilities",
            "Särskilda skallkrav/särskild kravprofil", "Kompetenser och färdigheter", "Utvärderingskriterier",
            "Start", "Slut", "End", "Placering", "Tidsperiod", "Uppdragets omfattning"];
        string Heading(string text) => text.Trim().TrimEnd(':').Trim();
        var results = new List<string>();
        foreach (var candidate in body.Descendants().Where(n => n.NodeType == HtmlNodeType.Element
                     && labels.Contains(Heading(Text(n)), StringComparer.OrdinalIgnoreCase)
                     && !n.Descendants().Any(c => c.NodeType == HtmlNodeType.Element
                         && labels.Contains(Heading(Text(c)), StringComparer.OrdinalIgnoreCase))))
        {
            var heading = candidate;
            while (heading.ParentNode is { } parent && parent != body && Text(parent) == Text(heading)) heading = parent;
            for (var next = heading.NextSibling; next is not null; next = next.NextSibling)
            {
                if (Text(next).Length == 0) continue;
                if (boundaries.Any(b => Heading(Text(next)).Equals(b, StringComparison.OrdinalIgnoreCase)
                        || Text(next).StartsWith(b + ":", StringComparison.OrdinalIgnoreCase))
                    || next.Name is "h1" or "h2" or "h3" or "h4"
                    || next.Descendants().Any(n => n.Name is "b" or "strong" && Text(n) == Text(next))) break;
                var entries = next.Name is "ul" or "ol" ? next.ChildNodes.Where(n => n.Name == "li")
                    : next.Name == "span" && next.ChildNodes.Count(n => n.NodeType == HtmlNodeType.Element) > 1
                        ? next.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element) : [next];
                results.AddRange(entries.Select(Text).Where(s => s.Length > 0));
            }
        }
        // Some advertisers put headings and bullet lines in one paragraph with <br>.
        if (results.Count == 0)
        {
            var active = false;
            foreach (var line in MultilineText(body).Split('\n'))
            {
                if (labels.Contains(Heading(line), StringComparer.OrdinalIgnoreCase)) { active = true; continue; }
                if (boundaries.Any(b => Heading(line).Equals(b, StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith(b + ":", StringComparison.OrdinalIgnoreCase))) { active = false; continue; }
                if (!active) continue;
                var bullet = Regex.Match(line, @"^[-•]\s+(.+)$");
                if (bullet.Success) results.Add(bullet.Groups[1].Value);
                else if (line.Length > 0) active = false;
            }
        }
        return results.Distinct().ToArray();
    }

    private static DateOnly? ParseDate(string? value, string label, List<string> warnings)
    {
        if (value is null) return null;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        warnings.Add($"Beskrivningens {label} har ett okänt format och har inte tolkats.");
        return null;
    }

    private static bool HasClass(HtmlNode node, string className) =>
        node.GetAttributeValue("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(className);

    private static string Text(HtmlNode? node) => node is null ? "" :
        Regex.Replace(MultilineText(node, false), @"\s+", " ").Trim();

    private static string MultilineText(HtmlNode root, bool bullets = true)
    {
        var builder = new StringBuilder();
        void Visit(HtmlNode node)
        {
            if (node.NodeType == HtmlNodeType.Text) builder.Append(HtmlEntity.DeEntitize(node.InnerText));
            else
            {
                if (node.Name == "br") builder.AppendLine();
                if (bullets && node.Name == "li") builder.Append("- ");
                foreach (var child in node.ChildNodes) Visit(child);
                if (node.Name == "span") builder.Append(' ');
                if (node.Name is "p" or "div" or "li" or "ul" or "ol" or "h2" or "h3") builder.AppendLine();
            }
        }
        Visit(root);
        return string.Join("\n", builder.ToString().Split('\n')
            .Select(line => Regex.Replace(line.Replace('\u00a0', ' '), @"\s+", " ").Trim())
            .Where(line => line.Length > 0));
    }
}
