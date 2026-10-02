using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;
using AssignmentFinder.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

internal static class PostgresChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var connection = Environment.GetEnvironmentVariable("ASSIGNMENT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("ASSIGNMENT_TEST_POSTGRES krävs för --postgres. Använd en testdatabas.");
        // Only our random schema is created/dropped. No existing tables or schemas are touched.
        var schema = "af_test_" + Guid.NewGuid().ToString("N");
        var scopedConnection = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString;
        using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin)) await create.ExecuteNonQueryAsync();
        var options = new DbContextOptionsBuilder<AssignmentDbContext>().UseNpgsql(scopedConnection,
            provider => provider.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options;
        try
        {
            await using (var db = new AssignmentDbContext(options)) await db.Database.MigrateAsync();
            var assignment = AnalysisDemo.Input.Assignment;
            var settings = new FilterSettings(["Stockholm"], true, 20);
            async Task<ImportResult> Import(Assignment item)
            {
                await using var db = new AssignmentDbContext(options);
                return await new AssignmentStore(db).ImportAsync(item, settings);
            }
            var first = await Import(assignment);
            var duplicate = await Import(assignment with { ImportedAtUtc = DateTimeOffset.UtcNow });
            check(first.NewRevision && !duplicate.NewRevision && first.RevisionId == duplicate.RevisionId, "PostgreSQL: upprepad import ger samma revision över nya DbContext-instanser");
            var changed = await Import(assignment with { Extent = "50%" });
            check(changed.NewRevision && changed.RevisionNumber == 2, "PostgreSQL: ändrad metadata ger ny revision");
            var restored = await Import(assignment);
            check(restored.NewRevision && restored.RevisionNumber == 3, "PostgreSQL: återgång till gammal text blir en ny historisk revision");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Import(assignment with { ExternalId = "parallel" })));
            check(concurrent.Count(r => r.NewRevision) == 1 && concurrent.Select(r => r.RevisionId).Distinct().Count() == 1,
                "PostgreSQL: samtidig identisk import skapar ett uppdrag och en revision");
            await using var verify = new AssignmentDbContext(options);
            check(await verify.Assignments.CountAsync() == 2 && await verify.Revisions.CountAsync() == 4,
                "PostgreSQL: lagring och versionshistorik består efter återöppning");
            var input = AnalysisDemo.Input with { CvReviewedByUser = true };
            var envelope = new AnalysisEnvelope(assignment.Source, assignment.ExternalId, assignment.ContentHash,
                input.CvHash, "Mock", "fixture-schema", DateTimeOffset.UtcNow, AnalysisDemo.Good);
            async Task<StoredAnalysis> SaveAnalysis()
            {
                await using var db = new AssignmentDbContext(options);
                return await new AnalysisStore(db).SaveValidatedAsync(restored.RevisionId, input, envelope, "fixture-v1", "Mock");
            }
            var analyses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => SaveAnalysis()));
            check(analyses.Select(a => a.Id).Distinct().Count() == 1 && await verify.Profiles.CountAsync() == 1
                && await verify.Analyses.CountAsync() == 1, "PostgreSQL: samtidig analyslagring ger en profil och en versionsbunden analys");
            async Task<StoredNotification?> Preview()
            {
                await using var db = new AssignmentDbContext(options);
                return await new AnalysisStore(db).PreparePreviewAsync(analyses[0].Id, "synthetic@example.invalid",
                    new(70, true), new DateOnly(2026, 10, 2));
            }
            var previews = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Preview()));
            check(previews.All(p => p?.State == NotificationState.Preview)
                && previews.Select(p => p!.Id).Distinct().Count() == 1 && await verify.Notifications.CountAsync() == 1,
                "PostgreSQL: samtidiga förhandsvisningar dedupliceras med stabilt Message-ID utan utskick");
            await Import(assignment with { Extent = "60%" });
            check(await Preview() is null, "PostgreSQL: ersatt revision skapar ingen notisförhandsvisning");
            await using var mismatch = new AssignmentDbContext(options);
            try
            {
                await new AnalysisStore(mismatch).SaveValidatedAsync(changed.RevisionId, input, envelope, "fixture-v1", "Mock");
                throw new InvalidOperationException("Wrong revision accepted");
            }
            catch (InvalidDataException)
            { check(await verify.Analyses.CountAsync() == 1, "PostgreSQL: fel revisionsunderlag lagras inte som analys"); }
            await ServerPipelineChecks.RunAsync(scopedConnection, check);
        }
        finally
        {
            // schema is a fixed prefix plus locally generated lowercase hex, never user input.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
