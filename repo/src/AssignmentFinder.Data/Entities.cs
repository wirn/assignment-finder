namespace AssignmentFinder.Data;

public sealed class StoredAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public List<AssignmentRevision> Revisions { get; set; } = [];
}
public sealed class AssignmentRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    public StoredAssignment Assignment { get; set; } = null!;
    public int Number { get; set; }
    public string Fingerprint { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public string FilterJson { get; set; } = "";
    public string FilterHash { get; set; } = "";
    public DateTimeOffset CollectedAtUtc { get; set; }
}
public sealed class CandidateProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CvHash { get; set; } = "";
    public bool ReviewedByUser { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
public sealed class StoredAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RevisionId { get; set; }
    public AssignmentRevision Revision { get; set; } = null!;
    public Guid CandidateProfileId { get; set; }
    public CandidateProfile CandidateProfile { get; set; } = null!;
    public string FilterHash { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public DateTimeOffset AnalyzedAtUtc { get; set; }
}
public enum NotificationState { Preview, Pending, Sending, Accepted, Uncertain, Failed }
public sealed class StoredNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AnalysisId { get; set; }
    public StoredAnalysis Analysis { get; set; } = null!;
    public string RecipientHash { get; set; } = "";
    public string MessageId { get; set; } = "";
    public NotificationState State { get; set; } = NotificationState.Preview;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
}
public sealed class PipelineRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string State { get; set; } = "Running";
    public int Imported { get; set; }
    public int Failed { get; set; }
}
