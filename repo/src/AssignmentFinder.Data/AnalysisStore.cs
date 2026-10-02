using System.Text.Json;
using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;
using AssignmentFinder.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AssignmentFinder.Data;

// Persistence boundary: caller must supply the exact CV and assignment used by the analyzer.
// No HTTP or SMTP is performed here; rejected answers never enter Analyses.
public sealed class AnalysisStore(AssignmentDbContext db)
{
    public static void ValidateInput(AnalysisInput input, AnalysisEnvelope envelope, string promptVersion, string model)
    {
        if (!input.CvReviewedByUser || string.IsNullOrWhiteSpace(input.CvText)
            || string.IsNullOrWhiteSpace(promptVersion) || string.IsNullOrWhiteSpace(model)
            || envelope.Source != input.Assignment.Source || envelope.ExternalId != input.Assignment.ExternalId
            || envelope.AssignmentHash != input.Assignment.ContentHash || envelope.CvHash != input.CvHash
            || string.IsNullOrWhiteSpace(envelope.Provider) || string.IsNullOrWhiteSpace(envelope.SchemaVersion))
            throw new InvalidDataException("Analysen saknar granskat eller versionsbundet underlag.");
        if (envelope.Provider == "Mock" && input.Assignment.Source != "Synthetic")
            throw new InvalidDataException("Mock-resultat får endast lagras för syntetiska uppdrag.");
        AnalysisValidator.Validate(envelope.Result, input);
    }

    public async Task<StoredAnalysis> SaveValidatedAsync(Guid revisionId, AnalysisInput input,
        AnalysisEnvelope envelope, string promptVersion, string model, CancellationToken ct = default)
    {
        ValidateInput(input, envelope, promptVersion, model);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Shared CV lock also serializes profile creation across different assignments/processes.
        var lockKey = "analysis:" + input.CvHash;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
        var assignmentLock = input.Assignment.Source + ":" + input.Assignment.ExternalId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({assignmentLock}, 0))", ct);
        var revision = await db.Revisions.SingleAsync(r => r.Id == revisionId, ct);
        if (revision.Fingerprint != AssignmentStore.Fingerprint(input.Assignment))
            throw new InvalidDataException("Analysunderlaget matchar inte den lagrade revisionen.");
        var profile = await db.Profiles.SingleOrDefaultAsync(p => p.CvHash == input.CvHash, ct);
        if (profile is null)
        {
            profile = new CandidateProfile { CvHash = input.CvHash, ReviewedByUser = true, CreatedAtUtc = DateTimeOffset.UtcNow };
            db.Profiles.Add(profile);
        }
        var stored = await db.Analyses.SingleOrDefaultAsync(a => a.RevisionId == revisionId
            && a.CandidateProfileId == profile.Id && a.FilterHash == revision.FilterHash
            && a.PromptVersion == promptVersion && a.Model == model, ct);
        if (stored is null)
        {
            stored = new StoredAnalysis { RevisionId = revisionId, CandidateProfileId = profile.Id,
                FilterHash = revision.FilterHash, PromptVersion = promptVersion, Model = model,
                ResultJson = JsonSerializer.Serialize(envelope, AnalysisValidator.JsonOptions), AnalyzedAtUtc = envelope.AnalyzedAtUtc };
            db.Analyses.Add(stored);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return stored;
    }

    public async Task<StoredNotification?> PreparePreviewAsync(Guid analysisId, string recipient,
        NotificationSettings settings, DateOnly today, CancellationToken ct = default)
    {
        settings.Validate();
        // Validation only; the address itself is not stored in the database.
        var parsedRecipient = new System.Net.Mail.MailAddress(recipient);
        if (parsedRecipient.Address != recipient) throw new InvalidDataException("Ange en mottagaradress utan visningsnamn.");
        var recipientHash = AssignmentStore.Hash(recipient.ToLowerInvariant());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockKey = "preview:" + analysisId + ":" + recipientHash;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
        var analysis = await db.Analyses.Include(a => a.Revision).ThenInclude(r => r.Assignment)
            .SingleAsync(a => a.Id == analysisId, ct);
        var revision = analysis.Revision;
        var assignmentLock = revision.Assignment.Source + ":" + revision.Assignment.ExternalId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({assignmentLock}, 0))", ct);
        await db.Entry(revision).ReloadAsync(ct);
        // Superseded assignment versions or changed filters must never create a new preview.
        if (await db.Revisions.AnyAsync(r => r.AssignmentId == revision.AssignmentId && r.Number > revision.Number, ct)
            || analysis.FilterHash != revision.FilterHash) return null;
        var assignment = JsonSerializer.Deserialize<Assignment>(revision.SnapshotJson, AnalysisValidator.JsonOptions)!;
        var envelope = JsonSerializer.Deserialize<AnalysisEnvelope>(analysis.ResultJson, AnalysisValidator.JsonOptions)!;
        var filter = JsonSerializer.Deserialize<FilterDecision>(revision.FilterJson, AnalysisValidator.JsonOptions)!;
        var decision = NotificationPolicy.Evaluate(assignment, envelope, filter, settings, today);
        if (decision.Action == NotificationAction.Suppress) return null;
        var existing = await db.Notifications.SingleOrDefaultAsync(n => n.AnalysisId == analysisId && n.RecipientHash == recipientHash, ct);
        if (existing is not null) return existing; // never reset a Sending/Accepted/Uncertain notification
        var notification = new StoredNotification { AnalysisId = analysisId, RecipientHash = recipientHash,
            MessageId = "<" + AssignmentStore.Hash(lockKey).ToLowerInvariant() + "@assignment-finder.invalid>",
            State = NotificationState.Preview, CreatedAtUtc = DateTimeOffset.UtcNow };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return notification;
    }
}
