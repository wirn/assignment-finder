using AssignmentFinder.Analysis;
using AssignmentFinder.Data;
using Microsoft.EntityFrameworkCore;

internal static class DataChecks
{
    public static void Run(Action<bool, string> check)
    {
        var assignment = AnalysisDemo.Input.Assignment;
        check(AssignmentStore.Fingerprint(assignment) == AssignmentStore.Fingerprint(assignment with { ImportedAtUtc = DateTimeOffset.UtcNow }),
            "Ny hämtningstid skapar inte i sig en ny revision");
        check(AssignmentStore.Fingerprint(assignment) != AssignmentStore.Fingerprint(assignment with { MandatoryRequirements = [] })
            && AssignmentStore.Fingerprint(assignment) != AssignmentStore.Fingerprint(assignment with { Warnings = ["Datumkonflikt"] }),
            "Ändrad kravtolkning eller källvarning ger ny revision även med samma texthash");
        using var db = new DesignFactory().CreateDbContext([]);
        check(!db.Database.HasPendingModelChanges(), "Databasmigrationen motsvarar aktuell EF-modell");
        var sql = db.Database.GenerateCreateScript();
        check(sql.Contains("timestamp with time zone") && sql.Contains("CREATE UNIQUE INDEX")
            && sql.Contains("RecipientHash") && sql.Contains("CandidateProfileId"),
            "PostgreSQL-schema kan genereras med UTC-tider och versions-/dubblettnycklar");
    }
}
