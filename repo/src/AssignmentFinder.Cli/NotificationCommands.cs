using System.Text.Json;
using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;
using AssignmentFinder.Notifications;

internal static class NotificationCommands
{
    public static async Task<int> DemoAsync()
    {
        var directory = Path.GetFullPath(Path.Combine("data/private/notification-demo", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var demo in AnalysisDemo.Cases())
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")).DateTime);
            var input = demo.Input with { Assignment = demo.Input.Assignment with { StartDate = today.AddDays(30),
                EndDate = today.AddMonths(6), ApplicationDeadline = today.AddDays(14) } };
            var result = await new MockAssignmentAnalyzer(demo.Result).AnalyzeAsync(input);
            var envelope = new AnalysisEnvelope(input.Assignment.Source, input.Assignment.ExternalId,
                input.Assignment.ContentHash, input.CvHash, "Mock-synthetic-fixture", "1", DateTimeOffset.UtcNow, result);
            var filter = new AssignmentFilter().Evaluate(input.Assignment, new(["Stockholm"], true, 20));
            // Example threshold only; this is not the user's notification configuration.
            var decision = NotificationPolicy.Evaluate(input.Assignment, envelope, filter, new(70, true), today);
            var preview = EmailTemplate.Render(input.Assignment, envelope, decision);
            await File.WriteAllTextAsync(Path.Combine(directory, demo.Name + ".html"), preview.HtmlBody);
            await File.WriteAllTextAsync(Path.Combine(directory, demo.Name + ".txt"), preview.TextBody);
            await File.WriteAllTextAsync(Path.Combine(directory, demo.Name + ".json"), JsonSerializer.Serialize(decision, AnalysisValidator.JsonOptions));
            var sent = await new LogOnlyEmailSender().SendAsync(preview, "fixture@example.invalid", Guid.NewGuid().ToString("N") + "@assignment-finder.invalid");
            Console.WriteLine($"Syntetiskt {demo.Name}: {decision.Action}, {sent.State}.");
        }
        Console.WriteLine($"Förhandsvisningar: {directory}. Bara testfall; ingen faktisk CV-bedömning eller e-post.");
        return 0;
    }
}
