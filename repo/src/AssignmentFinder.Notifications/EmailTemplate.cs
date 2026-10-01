using System.Text.Encodings.Web;
using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;

namespace AssignmentFinder.Notifications;

public sealed record EmailMessage(string Subject, string TextBody, string HtmlBody);
public static class EmailTemplate
{
    public static EmailMessage Render(Assignment assignment, AnalysisEnvelope analysis, NotificationDecision decision)
    {
        if (assignment.Url.Scheme != "https" || !string.IsNullOrEmpty(assignment.Url.UserInfo))
            throw new InvalidDataException("E-postens källänk måste vara HTTPS utan inloggningsuppgifter.");
        var result = analysis.Result;
        var time = TimeZoneInfo.ConvertTime(analysis.AnalyzedAtUtc, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"));
        List<string> lines = [assignment.Title, assignment.Url.AbsoluteUri,
            $"Bedömning: {result.Recommendation}, {result.Score}/100 (bedömning, inte sannolikhet)",
            result.Summary, "Varför uppdraget passar:", result.Reason,
            "Matchande kompetenser: " + string.Join(", ", result.MatchingSkills.Select(s => s.Skill)),
            "Viktiga luckor:", ..result.Gaps.Select(g => "• " + g), "Osäkerheter:",
            ..decision.Uncertainties.Select(u => "• " + u),
            decision.Reason, $"Analyserat: {time:yyyy-MM-dd HH:mm} Europe/Stockholm",
            "Du avgör själv om du vill söka uppdraget."];
        string Encode(string value) => HtmlEncoder.Default.Encode(value);
        var html = "<!doctype html><html lang=\"sv\"><meta charset=\"utf-8\"><title>Uppdragsförhandsvisning</title><body>"
            + "<h1>" + Encode(assignment.Title) + "</h1><p><a href=\"" + Encode(assignment.Url.AbsoluteUri)
            + "\">Öppna uppdraget</a></p>" + string.Join("", lines.Skip(2).Select(l => "<p>" + Encode(l) + "</p>")) + "</body></html>";
        return new("Uppdragsmatchning: " + assignment.Title.Replace('\r', ' ').Replace('\n', ' '), string.Join("\n\n", lines), html);
    }
}
