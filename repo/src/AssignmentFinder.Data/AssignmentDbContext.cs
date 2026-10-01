using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AssignmentFinder.Data;

public sealed class AssignmentDbContext(DbContextOptions<AssignmentDbContext> options) : DbContext(options)
{
    public DbSet<StoredAssignment> Assignments => Set<StoredAssignment>();
    public DbSet<AssignmentRevision> Revisions => Set<AssignmentRevision>();
    public DbSet<CandidateProfile> Profiles => Set<CandidateProfile>();
    public DbSet<StoredAnalysis> Analyses => Set<StoredAnalysis>();
    public DbSet<StoredNotification> Notifications => Set<StoredNotification>();
    public DbSet<PipelineRun> Runs => Set<PipelineRun>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<StoredAssignment>().HasIndex(a => new { a.Source, a.ExternalId }).IsUnique();
        model.Entity<StoredAssignment>().Property(a => a.Source).HasMaxLength(64);
        model.Entity<StoredAssignment>().Property(a => a.ExternalId).HasMaxLength(128);
        model.Entity<AssignmentRevision>().HasOne(r => r.Assignment).WithMany(a => a.Revisions).HasForeignKey(r => r.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AssignmentRevision>().HasIndex(r => new { r.AssignmentId, r.Number }).IsUnique();
        model.Entity<CandidateProfile>().HasIndex(p => p.CvHash).IsUnique();
        model.Entity<StoredAnalysis>().HasOne(a => a.Revision).WithMany().HasForeignKey(a => a.RevisionId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<StoredAnalysis>().HasOne(a => a.CandidateProfile).WithMany().HasForeignKey(a => a.CandidateProfileId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<StoredAnalysis>().HasIndex(a => new { a.RevisionId, a.CandidateProfileId, a.FilterHash, a.PromptVersion, a.Model }).IsUnique();
        model.Entity<StoredNotification>().HasOne(n => n.Analysis).WithMany().HasForeignKey(n => n.AnalysisId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<StoredNotification>().HasIndex(n => new { n.AnalysisId, n.RecipientHash }).IsUnique();
        model.Entity<StoredNotification>().HasIndex(n => n.MessageId).IsUnique();
        model.Entity<StoredNotification>().Property(n => n.State).HasConversion<string>();
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(string)))
                property.IsNullable = false;
    }
}

// For generation only: never reads private settings, and migrations are not applied here.
public sealed class DesignFactory : IDesignTimeDbContextFactory<AssignmentDbContext>
{
    public AssignmentDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AssignmentDbContext>()
        .UseNpgsql("Host=localhost;Database=assignmentfinder_design;Username=design;Password=unused").Options);
}
