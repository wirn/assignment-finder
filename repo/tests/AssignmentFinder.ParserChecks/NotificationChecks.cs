using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;
using AssignmentFinder.Notifications;

internal static class NotificationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var assignment = AnalysisDemo.Input.Assignment with { EndDate = new(2027, 1, 1), ApplicationDeadline = new(2026, 12, 1) };
        var envelope = new AnalysisEnvelope(assignment.Source, assignment.ExternalId, assignment.ContentHash,
            AnalysisDemo.Input.CvHash, "Mock", "1", DateTimeOffset.UnixEpoch, AnalysisDemo.Good);
        var filter = new AssignmentFilter().Evaluate(assignment, new(["Stockholm"], true, 20));
        NotificationDecision Decide(Assignment? a = null, AnalysisResult? r = null, bool corrected = false, NotificationSettings? settings = null) =>
            NotificationPolicy.Evaluate(a ?? assignment, envelope with { Result = r ?? envelope.Result }, filter,
                settings ?? new(70, true), new(2026, 10, 1), corrected);
        check(Decide().Action == NotificationAction.Preview, "Komplett matchning ger enbart förhandsvisning");
        check(Decide(r: AnalysisDemo.Good with { Score = 69 }).Action == NotificationAction.Suppress, "Notis under poänggräns stoppas");
        check(Decide(r: AnalysisDemo.Good with { Recommendation = Recommendation.Skip }).Action == NotificationAction.Suppress, "Skip ger ingen notis");
        check(Decide(a: assignment with { EndDate = new(2026, 9, 30) }).Action == NotificationAction.Suppress, "Avslutat uppdrag ger ingen notis");
        check(Decide(a: assignment with { StartDate = new(2027, 2, 1) }).Action == NotificationAction.NeedsReview, "Motstridiga datum granskas i stället för automatiskt avslag");
        check(Decide(a: assignment with { EndDate = null }).Action == NotificationAction.NeedsReview, "Okänd aktualitet kräver granskning");
        check(Decide(a: assignment with { Warnings = ["Arbetsformen är motsägande"] }).Uncertainties.Contains("Arbetsformen är motsägande"), "Källvarningar följer med till notisbeslut");
        check(Decide(corrected: true).Action == NotificationAction.NeedsReview, "Lokalt korrigerad analys kräver granskning");
        check(Decide(r: AnalysisDemo.Good with { Recommendation = Recommendation.Review }, settings: new(70, false)).Action == NotificationAction.Suppress,
            "Review kan stängas av i notiskonfiguration");
        try { NotificationPolicy.Evaluate(assignment, envelope with { AssignmentHash = "changed" }, filter, new(70, true), new(2026, 10, 1)); throw new Exception("FAILED: version"); }
        catch (InvalidDataException) { check(true, "Notis nekar annan uppdragsversion"); }
        var preview = EmailTemplate.Render(assignment with { Title = "<script>alert(1)</script>\r\nTitel" },
            envelope with { Result = AnalysisDemo.Good with { Summary = "<img src=x onerror=alert(1)>" } }, Decide());
        check(!preview.HtmlBody.Contains("<script>") && !preview.HtmlBody.Contains("<img")
            && !preview.Subject.Contains('\n') && preview.TextBody.Contains(assignment.Url.AbsoluteUri), "E-post HTML-escapar källtext, skyddar ämnesrad och har textalternativ");
        var delivery = await new LogOnlyEmailSender().SendAsync(preview, "test@example.invalid", "test@assignment-finder.invalid");
        check(delivery.State == DeliveryState.DryRun && delivery.AcceptedAtUtc is null, "LogOnly rapporterar aldrig SMTP-acceptans");
        try { await new GoogleSmtpEmailSender(new(false, false, "smtp.gmail.com", 587, "", "", "", "")).SendAsync(preview, "", "id"); throw new Exception("FAILED: SMTP gate"); }
        catch (InvalidOperationException) { check(true, "SMTP nekas före nätverk utan aktivering och godkänd konfiguration"); }
    }
}
