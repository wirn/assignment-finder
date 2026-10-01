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
        }
        finally
        {
            // schema is a fixed prefix plus locally generated lowercase hex, never user input.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
