using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssignmentFinder.Analysis;

public enum QuoteSource { Cv, Assignment }
public sealed record EvidenceQuoteCorrection([property: JsonRequired] int MatchingSkillIndex,
    [property: JsonRequired] QuoteSource Source,
    [property: JsonRequired] string OriginalQuote, [property: JsonRequired] string CorrectedQuote);

public static class AnalysisRevalidation
{
    // Offline source-backed corrections only. Preserve the original provider response.
    public static AnalysisResult Validate(CapturedAnalysisResponse response, AnalysisInput input, EvidenceQuoteCorrection[] corrections)
    {
        if (response.CvHash != input.CvHash || response.AssignmentHash != input.Assignment.ContentHash)
            throw new InvalidDataException("Sparat svar hör till en annan CV- eller uppdragsversion.");
        var result = JsonSerializer.Deserialize<AnalysisResult>(response.AnalysisJson, AnalysisValidator.JsonOptions)
            ?? throw new InvalidDataException("Sparat analysresultat saknas.");
        if (result.MatchingSkills is null || corrections is null
            || corrections.Any(c => c is null || !Enum.IsDefined(c.Source))
            || corrections.Select(c => (c.MatchingSkillIndex, c.Source)).Distinct().Count() != corrections.Length)
            throw new InvalidDataException("Ogiltiga citatkorrigeringar.");
        result = result with { MatchingSkills = result.MatchingSkills.ToArray() };
        foreach (var correction in corrections)
        {
            if (correction.MatchingSkillIndex < 0 || correction.MatchingSkillIndex >= result.MatchingSkills.Length
                || string.IsNullOrWhiteSpace(correction.CorrectedQuote))
                throw new InvalidDataException("Ogiltig citatkorrigering.");
            var match = result.MatchingSkills[correction.MatchingSkillIndex];
            if (match?.Evidence is null || (correction.Source == QuoteSource.Cv ? match.Evidence.CvQuote : match.Evidence.AssignmentQuote) != correction.OriginalQuote)
                throw new InvalidDataException("Citatkorrigeringen matchar inte det sparade originalsvaret.");
            result.MatchingSkills[correction.MatchingSkillIndex] = match with
            { Evidence = correction.Source == QuoteSource.Cv
                ? match.Evidence with { CvQuote = correction.CorrectedQuote }
                : match.Evidence with { AssignmentQuote = correction.CorrectedQuote } };
        }
        // Even a source-backed repair requires human review before an Apply decision.
        if (corrections.Length > 0 && result.Recommendation == Recommendation.Apply && result.Uncertainties is not null)
            result = result with { Recommendation = Recommendation.Review,
                Uncertainties = [..result.Uncertainties, "Beläggscitat har korrigerats lokalt; granska bedömningen före ansökan."] };
        AnalysisValidator.Validate(result, input);
        return result;
    }
}
