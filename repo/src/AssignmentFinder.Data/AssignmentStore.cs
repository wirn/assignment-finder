using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;
using Microsoft.EntityFrameworkCore;

namespace AssignmentFinder.Data;

public sealed record ImportResult(Guid AssignmentId, Guid RevisionId, bool NewRevision, int RevisionNumber);
public sealed class AssignmentStore(AssignmentDbContext db)
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Fingerprint(Assignment assignment) => Hash(JsonSerializer.Serialize(
        assignment with { ImportedAtUtc = DateTimeOffset.UnixEpoch }, AnalysisValidator.JsonOptions));

    public async Task<ImportResult> ImportAsync(Assignment assignment, FilterSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(assignment.Source) || assignment.Source.Length > 64
            || string.IsNullOrWhiteSpace(assignment.ExternalId) || assignment.ExternalId.Length > 128
            || string.IsNullOrWhiteSpace(assignment.Title) || string.IsNullOrWhiteSpace(assignment.Description)
            || assignment.Url.Scheme != "https")
            throw new InvalidDataException("Importunderlaget är ofullständigt.");
        var decision = new AssignmentFilter().Evaluate(assignment, settings);
        var fingerprint = Fingerprint(assignment);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Cross-process lock protects insertion and the monotonically increasing revision number.
        var lockKey = assignment.Source + ":" + assignment.ExternalId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        var stored = await db.Assignments.SingleOrDefaultAsync(a => a.Source == assignment.Source && a.ExternalId == assignment.ExternalId, cancellationToken);
        if (stored is null)
        {
            stored = new() { Source = assignment.Source, ExternalId = assignment.ExternalId };
            db.Assignments.Add(stored);
        }
        stored.LastSeenAtUtc = DateTimeOffset.UtcNow;
        var latest = await db.Revisions.Where(r => r.AssignmentId == stored.Id).OrderByDescending(r => r.Number).FirstOrDefaultAsync(cancellationToken);
        var changed = latest is null || latest.Fingerprint != fingerprint;
        var filterHash = Hash(JsonSerializer.Serialize(settings, AnalysisValidator.JsonOptions));
        if (changed)
        {
            latest = new() { AssignmentId = stored.Id, Number = (latest?.Number ?? 0) + 1,
                Fingerprint = fingerprint, ContentHash = assignment.ContentHash,
                SnapshotJson = JsonSerializer.Serialize(assignment, AnalysisValidator.JsonOptions),
                FilterJson = JsonSerializer.Serialize(decision, AnalysisValidator.JsonOptions), FilterHash = filterHash,
                CollectedAtUtc = assignment.ImportedAtUtc.ToUniversalTime() };
            db.Revisions.Add(latest);
        }
        else
        {
            latest!.FilterJson = JsonSerializer.Serialize(decision, AnalysisValidator.JsonOptions);
            latest.FilterHash = filterHash;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(stored.Id, latest!.Id, changed, latest.Number);
    }
}
