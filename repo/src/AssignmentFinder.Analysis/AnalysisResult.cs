using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AssignmentFinder.Brainville;

namespace AssignmentFinder.Analysis;

public enum Recommendation { Apply, Review, Skip }
public enum RequirementStatus { Evidenced, NotEvidenced, Uncertain }
public sealed record Evidence(
    [property: JsonRequired] string AssignmentQuote,
    [property: JsonRequired] string CvQuote);
public sealed record RequirementAssessment(
    [property: JsonRequired] string Requirement,
    [property: JsonRequired] RequirementStatus Status,
    [property: JsonRequired] string AssignmentQuote,
    [property: JsonRequired] string? CvQuote,
    [property: JsonRequired] string Reason);
public sealed record SkillMatch(
    [property: JsonRequired] string Skill,
    [property: JsonRequired] Evidence Evidence);
public sealed record AnalysisResult(
    [property: JsonRequired] int Score,
    [property: JsonRequired] Recommendation Recommendation,
    [property: JsonRequired] string Summary,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] SkillMatch[] MatchingSkills,
    [property: JsonRequired] RequirementAssessment[] MandatoryRequirements,
    [property: JsonRequired] string[] Gaps,
    [property: JsonRequired] string[] Uncertainties);

public sealed record AnalysisInput(Assignment Assignment, string CvText)
{
    public bool CvReviewedByUser { get; init; }
    public string CvHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CvText)));
}
public sealed record AnalysisEnvelope(string Source, string ExternalId, string AssignmentHash,
    string CvHash, string Provider, string SchemaVersion, DateTimeOffset AnalyzedAtUtc, AnalysisResult Result);

public interface IAssignmentAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(AnalysisInput input, CancellationToken cancellationToken = default);
}

// Deterministic fixture provider for testing contracts, not a matching algorithm.
public sealed class MockAssignmentAnalyzer(AnalysisResult fixture) : IAssignmentAnalyzer
{
    public Task<AnalysisResult> AnalyzeAsync(AnalysisInput input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisValidator.Validate(fixture, input);
        return Task.FromResult(fixture);
    }
}

public static class AnalysisValidator
{
    public static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    public static AnalysisResult Parse(string json, AnalysisInput input)
    {
        try
        {
            var result = JsonSerializer.Deserialize<AnalysisResult>(json, JsonOptions)
                ?? throw new InvalidDataException("Analysresultat saknas.");
            Validate(result, input);
            return result;
        }
        catch (JsonException) { throw new InvalidDataException("Analysens JSON uppfyller inte schemat."); }
    }

    public static void Validate(AnalysisResult result, AnalysisInput input)
    {
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        // Preserve words, punctuation and case; only formatting whitespace may differ.
        string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
        bool Contains(string text, string part, StringComparison comparison = StringComparison.Ordinal) => Normalize(text).Contains(Normalize(part), comparison);
        bool Quote(string? quote, string text) => !string.IsNullOrWhiteSpace(quote) && Contains(text, quote);
        Require(result.Score is >= 0 and <= 100 && Enum.IsDefined(result.Recommendation), "Ogiltig poäng eller rekommendation.");
        Require(!string.IsNullOrWhiteSpace(result.Summary) && !string.IsNullOrWhiteSpace(result.Reason), "Analysmotivering saknas.");
        Require(result.MatchingSkills is not null && result.MandatoryRequirements is not null
            && result.Gaps is not null && result.Uncertainties is not null, "Analyslistor saknas.");
        for (var index = 0; index < result.MatchingSkills!.Length; index++)
        {
            var match = result.MatchingSkills[index];
            var field = $"matchingSkills[{index}]";
            Require(match is not null && !string.IsNullOrWhiteSpace(match.Skill) && match.Evidence is not null,
                $"{field}: kompetens eller belägg saknas.");
            Require(Quote(match.Evidence.AssignmentQuote, input.Assignment.Description),
                $"{field}.evidence.assignmentQuote: citatet finns inte i uppdragstexten.");
            Require(Quote(match.Evidence.CvQuote, input.CvText),
                $"{field}.evidence.cvQuote: citatet finns inte i CV-underlaget.");
            Require(Contains(match.Evidence.AssignmentQuote, match.Skill, StringComparison.OrdinalIgnoreCase)
                && Contains(match.Evidence.CvQuote, match.Skill, StringComparison.OrdinalIgnoreCase),
                $"{field}.skill: kompetensnamnet måste förekomma i båda citaten.");
        }
        var expected = input.Assignment.MandatoryRequirements.Distinct().Order().ToArray();
        Require(result.MandatoryRequirements!.All(r => r is not null)
            && result.MandatoryRequirements.Select(r => r.Requirement).Order().SequenceEqual(expected),
            $"mandatoryRequirements: förväntade {expected.Length} klassificerade krav, fick {result.MandatoryRequirements.Length} poster. Krav har utelämnats, lagts till eller dubblerats.");
        foreach (var requirement in result.MandatoryRequirements)
        {
            Require(Enum.IsDefined(requirement.Status) && !string.IsNullOrWhiteSpace(requirement.Reason)
                && Quote(requirement.AssignmentQuote, input.Assignment.Description)
                && Contains(requirement.AssignmentQuote, requirement.Requirement),
                "Kravbedömningen är ogiltig.");
            Require(requirement.Status != RequirementStatus.Evidenced || Quote(requirement.CvQuote, input.CvText),
                "Uppfyllt krav saknar CV-belägg.");
            Require(requirement.CvQuote is null || Quote(requirement.CvQuote, input.CvText), "CV-citatet finns inte i underlaget.");
        }
        Require(result.Gaps!.All(s => !string.IsNullOrWhiteSpace(s)) && result.Uncertainties!.All(s => !string.IsNullOrWhiteSpace(s)),
            "Tomma luckor eller osäkerheter är ogiltiga.");
        Require(result.Recommendation != Recommendation.Apply || result.MandatoryRequirements.All(r => r.Status == RequirementStatus.Evidenced)
            && result.Gaps.Length == 0 && result.Uncertainties.Length == 0 && result.MatchingSkills.Length > 0,
            "Ansökningsrekommendationen motsäger luckor, osäkerheter eller kravbedömningen.");
    }
}
