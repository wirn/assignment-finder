using System.Runtime.CompilerServices;
using System.Text.Json;
using AssignmentFinder.Analysis;
using AssignmentFinder.Brainville;
using AssignmentFinder.Data;

namespace AssignmentFinder.App;

public sealed record ImportSettings(string Directory, string FilterPath);
public sealed class LocalJsonAssignmentSource(string directory) : IAssignmentSource
{
    public string Name => "LocalJson";
    public async IAsyncEnumerable<Assignment> FetchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(directory)) throw new InvalidDataException("Importkatalogen saknas.");
        var files = System.IO.Directory.EnumerateFiles(directory, "brainville-*.json", SearchOption.TopDirectoryOnly).Order().Take(101).ToArray();
        if (files.Length > 100) throw new InvalidDataException("Importen tillåter högst 100 uppdrag per körning.");
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("Importfilen är för stor.");
            var item = JsonSerializer.Deserialize<Assignment>(await File.ReadAllTextAsync(path, cancellationToken), AnalysisValidator.JsonOptions)
                ?? throw new InvalidDataException("Importfilen saknar uppdrag.");
            yield return item;
        }
    }
}
public sealed class ImportRunner(IServiceScopeFactory scopes, ImportSettings settings, ILogger<ImportRunner> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<PipelineRun?> RunAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AssignmentDbContext>();
        var run = new PipelineRun { StartedAtUtc = DateTimeOffset.UtcNow };
        try
        {
            var filter = JsonSerializer.Deserialize<FilterSettings>(await File.ReadAllTextAsync(settings.FilterPath, ct), AnalysisValidator.JsonOptions)
                ?? throw new InvalidDataException("Filterkonfiguration saknas.");
            filter.Validate();
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);
            try
            {
                await foreach (var item in new LocalJsonAssignmentSource(settings.Directory).FetchAsync(ct))
                {
                    // A new scope per import avoids keeping failed tracked changes in the next transaction.
                    using var itemScope = scopes.CreateScope();
                    await itemScope.ServiceProvider.GetRequiredService<AssignmentStore>().ImportAsync(item, filter, ct);
                    run.Imported++;
                }
                run.State = "Completed";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { run.State = "Cancelled"; }
            catch { run.State = "Failed"; run.Failed++; }
            run.FinishedAtUtc = DateTimeOffset.UtcNow;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await db.SaveChangesAsync(timeout.Token);
            logger.LogInformation("Lokal import {State}: {Imported} uppdrag, {Failed} fel. Inga AI-anrop eller utskick.", run.State, run.Imported, run.Failed);
            return run;
        }
        finally { gate.Release(); }
    }
}
