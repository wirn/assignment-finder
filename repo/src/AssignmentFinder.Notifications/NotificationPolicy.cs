using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;

namespace AssignmentFinder.Notifications;

public sealed record NotificationSettings(int MinimumScore, bool IncludeReview)
{
    public void Validate()
    {
        if (MinimumScore is < 0 or > 100) throw new ArgumentException("Notisgränsen måste vara 0–100.");
    }
}
public enum NotificationAction { Suppress, NeedsReview, Preview }
public sealed record NotificationDecision(NotificationAction Action, string Reason, string[] Uncertainties);

public static class NotificationPolicy
{
    // This only decides whether a validated result deserves a preview, never authorizes SMTP.
    public static NotificationDecision Evaluate(Assignment assignment, AnalysisEnvelope analysis,
        FilterDecision filter, NotificationSettings settings, DateOnly today, bool locallyRevalidated = false)
    {
        settings.Validate();
        if (analysis.Source != assignment.Source || analysis.ExternalId != assignment.ExternalId
            || analysis.AssignmentHash != assignment.ContentHash || filter.ExternalId != assignment.ExternalId)
            throw new InvalidDataException("Notisunderlaget gäller en annan uppdragsversion.");
        var result = analysis.Result;
        if (filter.Status == "Rejected" || result.Recommendation == Recommendation.Skip || result.Score < settings.MinimumScore)
            return new(NotificationAction.Suppress, "Filter, rekommendation eller poänggräns stoppar notisen.", []);
        var warnings = assignment.Warnings.Concat(filter.Uncertainties).Concat(result.Uncertainties).ToList();
        if (assignment.EndDate is { } end && assignment.StartDate is { } start && end < start)
            warnings.Add("Slutdatum ligger före startdatum; uppdragets aktualitet måste klargöras.");
        else if (assignment.EndDate < today || assignment.ApplicationDeadline < today)
            return new(NotificationAction.Suppress, "Uppdragets slutdatum eller sista ansökningsdag har passerat.", []);
        if (assignment.EndDate is null || assignment.ApplicationDeadline is null)
            warnings.Add("Uppdragets aktualitet är inte fullständigt känd.");
        if (result.MandatoryRequirements.Any(r => r.Status != RequirementStatus.Evidenced))
            warnings.Add("Obligatoriska krav saknar fullständiga belägg.");
        if (result.Gaps.Length > 0) warnings.Add("Analysen innehåller kompetensluckor att granska.");
        if (locallyRevalidated) warnings.Add("Citat har omkontrollerats lokalt; semantisk granskning behövs.");
        if (result.Recommendation == Recommendation.Review && !settings.IncludeReview)
            return new(NotificationAction.Suppress, "Konfigurationen inkluderar inte Review-resultat.", warnings.Distinct().ToArray());
        return warnings.Count > 0 || result.Recommendation == Recommendation.Review || filter.Status != "Passed"
            ? new(NotificationAction.NeedsReview, "Potentiell matchning som behöver granskas före utskick.", warnings.Distinct().ToArray())
            : new(NotificationAction.Preview, "Validerad matchning uppfyller notiskriterierna; endast förhandsvisning.", []);
    }
}
