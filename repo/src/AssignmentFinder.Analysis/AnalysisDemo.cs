using System.Security.Cryptography;
using System.Text;
using AssignmentFinder.Brainville;

namespace AssignmentFinder.Analysis;

public static class AnalysisDemo
{
    public static AnalysisInput Input { get; } = CreateInput();
    private static AnalysisInput CreateInput()
    {
        const string description = "Obligatoriska krav: Angular och WCAG. Meriterande: Docker.";
        return new(new Assignment("Synthetic", "demo-1", new Uri("https://example.invalid/assignments/demo-1"),
            DateTimeOffset.UnixEpoch, "Syntetiskt frontenduppdrag", "Testbolag", description,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description))),
            "Stockholm", "Hybrid", "Heltid", null, null, null, ["Angular", "WCAG"], ["Docker"], [], []),
            "Syntetiskt CV: utvecklade Angular-applikationer och genomförde WCAG-granskningar.");
    }

    public static AnalysisResult Good { get; } = new(90, Recommendation.Apply,
        "Syntetisk god matchning.", "Test-CV:t ger belägg för båda obligatoriska kraven.",
        [new("Angular", new("Angular", "Angular-applikationer")),
         new("WCAG", new("WCAG", "WCAG-granskningar"))],
        [new("Angular", RequirementStatus.Evidenced, "Angular", "Angular-applikationer", "Belägg i test-CV."),
         new("WCAG", RequirementStatus.Evidenced, "WCAG", "WCAG-granskningar", "Belägg i test-CV.")], [], []);

    public static IEnumerable<(string Name, AnalysisInput Input, AnalysisResult Result)> Cases()
    {
        yield return ("good", Input, Good);
        var incomplete = Input with { CvText = "Syntetiskt CV: utvecklade Angular-applikationer." };
        var requirements = new RequirementAssessment[]
        {
            Good.MandatoryRequirements[0],
            new("WCAG", RequirementStatus.NotEvidenced, "WCAG", null, "WCAG-erfarenhet framgår inte av test-CV:t.")
        };
        yield return ("gap", incomplete, Good with
        {
            Score = 45, Recommendation = Recommendation.Skip, Summary = "Syntetisk matchning med beläggslucka.",
            Reason = "Ett obligatoriskt krav saknar belägg; detta bevisar inte att kandidaten saknar erfarenheten.",
            MatchingSkills = [Good.MatchingSkills[0]], MandatoryRequirements = requirements,
            Gaps = ["WCAG-belägg saknas i CV-underlaget."]
        });
        yield return ("uncertain", Input, Good with
        {
            Score = 75, Recommendation = Recommendation.Review,
            Summary = "Syntetisk matchning som behöver granskning.",
            Reason = "Teknikerna är belagda men uppdragets övriga omfattning behöver klargöras.",
            Uncertainties = ["Uppdragets ansvarsfördelning är oklar i testfallet."]
        });
    }
}
