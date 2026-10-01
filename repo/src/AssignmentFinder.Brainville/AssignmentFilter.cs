using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace AssignmentFinder.Brainville;

public sealed record FilterSettings(
    [property: JsonRequired] string[] AllowedLocations,
    [property: JsonRequired] bool AllowRemote,
    [property: JsonRequired] decimal MinimumExtentPercent)
{
    public void Validate()
    {
        if (AllowedLocations is null || AllowedLocations.Any(string.IsNullOrWhiteSpace)
            || MinimumExtentPercent is < 0 or > 100)
            throw new ArgumentException("Ogiltig filterkonfiguration: orter krävs och omfattning måste ligga mellan 0 och 100.");
    }
}

public sealed record FilterDecision(string ExternalId, string Status, bool ContinueToAnalysis,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> Uncertainties);

public sealed class AssignmentFilter
{
    public FilterDecision Evaluate(Assignment assignment, FilterSettings settings)
    {
        settings.Validate();
        var reasons = new List<string>();
        var uncertainties = new List<string>();
        var rejected = false;
        var location = assignment.Location?.Trim();
        var work = assignment.WorkArrangement?.Trim().ToLowerInvariant();
        var remote = work is "distans" or "remote" or "fully remote" or "100% remote" or "100% distans";
        var onsiteRequired = work is "hybrid" or "på plats" or "onsite" or "on-site";
        var locations = location?.Split([',', ';', '/', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var locationAllowed = locations?.Any(l => settings.AllowedLocations.Contains(l, StringComparer.OrdinalIgnoreCase)) == true;
        if (locationAllowed) reasons.Add("Platsen matchar en tillåten ort.");
        else if (remote && settings.AllowRemote) reasons.Add("Uppdraget är på distans.");
        else if (locations is { Length: > 0 } && (onsiteRequired || remote || !settings.AllowRemote))
        {
            rejected = true;
            reasons.Add("Platsen är utanför tillåtna orter och arbetsformen uppfyller inte distansalternativet.");
        }
        else uncertainties.Add("Plats eller arbetsform är okänd/otydlig; Stockholm eller distans kan inte verifieras.");

        var extent = assignment.Extent?.Trim();
        decimal? lower = null, upper = null;
        if (extent?.ToLowerInvariant() is "heltid" or "full time" or "full-time") lower = upper = 100;
        else if (extent is not null)
        {
            var match = Regex.Match(extent, @"^(\d{1,3}(?:[.,]\d+)?)\s*(?:[-–]\s*(\d{1,3}(?:[.,]\d+)?)\s*)?(?:%|procent)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                lower = decimal.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                upper = match.Groups[2].Success ? decimal.Parse(match.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : lower;
                if (lower > upper || upper > 100) lower = upper = null;
            }
        }
        if (upper < settings.MinimumExtentPercent)
        {
            rejected = true;
            reasons.Add("Omfattningen understiger miniminivån.");
        }
        else if (lower >= settings.MinimumExtentPercent) reasons.Add("Omfattningen uppfyller miniminivån.");
        else uncertainties.Add("Omfattningen är okänd eller ett intervall som överlappar miniminivån.");
        return new(assignment.ExternalId, rejected ? "Rejected" : uncertainties.Count > 0 ? "NeedsReview" : "Passed",
            !rejected, reasons, uncertainties);
    }
}
