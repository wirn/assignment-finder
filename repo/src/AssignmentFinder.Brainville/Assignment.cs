namespace AssignmentFinder.Brainville;

public sealed record Assignment(
    string Source,
    string ExternalId,
    Uri Url,
    DateTimeOffset ImportedAtUtc,
    string Title,
    string? Company,
    string Description,
    string ContentHash,
    string? Location,
    string? WorkArrangement,
    string? Extent,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateOnly? ApplicationDeadline,
    IReadOnlyList<string> MandatoryRequirements,
    IReadOnlyList<string> PreferredQualifications,
    IReadOnlyList<string> SourceMetadata,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<string> UnclassifiedRequirements { get; init; } = [];
}
