using System.Security.Cryptography;
using System.Text;
using AssignmentFinder.App;
using AssignmentFinder.Data;
using Microsoft.EntityFrameworkCore;

if (args is ["--install-browser"])
{
    Environment.ExitCode = Microsoft.Playwright.Program.Main(["install", "--with-deps", "chromium"]);
    return;
}
if (args is ["--browser-check"])
{
    try
    {
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<h1>Lokalt test</h1>");
        if (await page.Locator("h1").InnerTextAsync() != "Lokalt test") throw new InvalidDataException();
        Console.WriteLine("PASS: lokalt Chromium-test. Inga kontoanrop.");
    }
    catch { Console.Error.WriteLine("Lokalt Chromium-test misslyckades."); Environment.ExitCode = 1; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:8091");
// Do not log SQL parameters, connection errors, source text or request credentials.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var connection = builder.Configuration.GetConnectionString("Main");
var adminKey = builder.Configuration["ADMIN_API_KEY"];
if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(adminKey) || adminKey.Length < 32)
    throw new InvalidOperationException("ConnectionStrings__Main och ADMIN_API_KEY (minst 32 tecken) krävs.");
try { _ = new Npgsql.NpgsqlConnectionStringBuilder(connection); }
catch (ArgumentException) { throw new InvalidOperationException("Databasens anslutningskonfiguration är ogiltig."); }
builder.Services.AddDbContext<AssignmentDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddScoped<AssignmentStore>();
builder.Services.AddScoped<AnalysisStore>();
builder.Services.AddSingleton<ImportRunner>();
builder.Services.AddSingleton(new ImportSettings(
    builder.Configuration["ImportDirectory"] ?? "data/private/import-inbox",
    builder.Configuration["FilterPath"] ?? "data/private/filter-settings.json",
    builder.Configuration["BrowserSettingsPath"] ?? "data/private/browser-settings.json"));
var app = builder.Build();
if (args.Contains("--migrate"))
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AssignmentDbContext>().Database.MigrateAsync();
        Console.WriteLine("Databasmigration klar.");
    }
    catch { Console.Error.WriteLine("Databasmigration misslyckades. Kontrollera anslutning och backup; inga hemligheter loggas."); Environment.ExitCode = 1; }
    return;
}
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var supplied = context.Request.Headers["X-Admin-Key"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(adminKey))))
        { context.Response.StatusCode = 401; return; }
    }
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch
    {
        if (!context.Response.HasStarted)
        { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Åtgärden misslyckades; kontrollera lokal konfiguration och databas." }); }
    }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (AssignmentDbContext db, CancellationToken ct) =>
{
    try
    {
        // Checking a table also detects unapplied initial migrations. No AI or SMTP.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await db.Runs.Take(1).Select(r => r.Id).ToListAsync(timeout.Token);
        return Results.Ok(new { status = "ready" });
    }
    catch { return Results.StatusCode(503); }
});
app.MapPost("/api/import/run", async (ImportRunner runner, CancellationToken ct) =>
{
    var result = await runner.RunAsync(ct);
    return result is null ? Results.Conflict(new { error = "En import körs redan." }) : Results.Ok(result);
});
app.MapGet("/api/status", async (AssignmentDbContext db, CancellationToken ct) => Results.Ok(new
{
    mode = "ManualCollection", paidAnalysis = false, email = false, schedule = false,
    assignments = await db.Assignments.CountAsync(ct), revisions = await db.Revisions.CountAsync(ct),
    latestRun = await db.Runs.OrderByDescending(r => r.StartedAtUtc).Select(r => new { r.Id, r.State, r.Imported, r.Failed, r.StartedAtUtc, r.FinishedAtUtc }).FirstOrDefaultAsync(ct)
}));
app.MapPost("/api/brainville/run", async (ImportRunner runner, CancellationToken ct) =>
{
    var result = await runner.RunAsync(ct, browser: true);
    return result is null ? Results.Conflict(new { error = "En import körs redan." }) : Results.Ok(result);
});
app.MapGet("/api/assignments", async (AssignmentDbContext db, CancellationToken ct) =>
{
    var rows = await db.Revisions.AsNoTracking()
        .Where(r => !db.Revisions.Any(newer => newer.AssignmentId == r.AssignmentId && newer.Number > r.Number))
        .OrderByDescending(r => r.Assignment.LastSeenAtUtc).Take(100)
        .Select(r => new { r.Id, r.Assignment.Source, r.Assignment.ExternalId, r.Number, r.SnapshotJson, r.FilterJson }).ToListAsync(ct);
    return Results.Ok(rows.Select(r => new { r.Id, r.Source, r.ExternalId, r.Number,
        assignment = System.Text.Json.JsonSerializer.Deserialize<AssignmentFinder.Brainville.Assignment>(r.SnapshotJson, AssignmentFinder.Analysis.AnalysisValidator.JsonOptions),
        filter = System.Text.Json.JsonSerializer.Deserialize<AssignmentFinder.Brainville.FilterDecision>(r.FilterJson, AssignmentFinder.Analysis.AnalysisValidator.JsonOptions) }));
});
// Explicit fixture demonstration: no candidate data, account requests, AI HTTP or email.
app.MapPost("/api/pipeline/demo", async (AssignmentStore assignments,
    AnalysisStore analyses, CancellationToken ct) =>
{
    var input = AssignmentFinder.Analysis.AnalysisDemo.Input with { CvReviewedByUser = true };
    var imported = await assignments.ImportAsync(input.Assignment,
        new AssignmentFinder.Brainville.FilterSettings(["Stockholm"], true, 20), ct);
    var result = await new AssignmentFinder.Analysis.MockAssignmentAnalyzer(AssignmentFinder.Analysis.AnalysisDemo.Good).AnalyzeAsync(input, ct);
    var envelope = new AssignmentFinder.Analysis.AnalysisEnvelope(input.Assignment.Source, input.Assignment.ExternalId,
        input.Assignment.ContentHash, input.CvHash, "Mock", "fixture-v1", DateTimeOffset.UtcNow, result);
    var stored = await analyses.SaveValidatedAsync(imported.RevisionId, input, envelope, "fixture-v1", "Mock", ct);
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")).DateTime);
    var preview = await analyses.PreparePreviewAsync(stored.Id, "synthetic@example.invalid", new(70, true), today, ct);
    return Results.Ok(new { mode = "SyntheticFixture", paidAnalysis = false, email = false,
        imported.AssignmentId, imported.RevisionId, analysisId = stored.Id, previewId = preview?.Id });
});
app.MapGet("/api/analyses", async (AssignmentDbContext db, CancellationToken ct) => Results.Ok(
    await db.Analyses.AsNoTracking().OrderByDescending(a => a.AnalyzedAtUtc).Take(100)
        .Select(a => new { a.Id, a.RevisionId, a.FilterHash, a.PromptVersion, a.Model, a.AnalyzedAtUtc }).ToListAsync(ct)));
app.MapGet("/api/previews/{id:guid}", async (Guid id, AssignmentDbContext db, CancellationToken ct) =>
{
    var preview = await db.Notifications.AsNoTracking().Include(n => n.Analysis).ThenInclude(a => a.Revision)
        .SingleOrDefaultAsync(n => n.Id == id, ct);
    if (preview is null) return Results.NotFound();
    var analysis = preview.Analysis;
    var revision = analysis.Revision;
    if (analysis.FilterHash != revision.FilterHash
        || await db.Revisions.AnyAsync(r => r.AssignmentId == revision.AssignmentId && r.Number > revision.Number, ct))
        return Results.Conflict(new { error = "Underlaget är ersatt; förhandsvisningen kräver en ny bedömning." });
    var assignment = System.Text.Json.JsonSerializer.Deserialize<AssignmentFinder.Brainville.Assignment>(revision.SnapshotJson, AssignmentFinder.Analysis.AnalysisValidator.JsonOptions)!;
    var envelope = System.Text.Json.JsonSerializer.Deserialize<AssignmentFinder.Analysis.AnalysisEnvelope>(analysis.ResultJson, AssignmentFinder.Analysis.AnalysisValidator.JsonOptions)!;
    // Only the demo policy is configured. Real candidate previews need the user's chosen policy.
    if (envelope.Provider != "Mock" || assignment.Source != "Synthetic")
        return Results.Conflict(new { error = "Notisinställningar för verkliga analyser är ännu inte inkopplade." });
    var filter = System.Text.Json.JsonSerializer.Deserialize<AssignmentFinder.Brainville.FilterDecision>(revision.FilterJson, AssignmentFinder.Analysis.AnalysisValidator.JsonOptions)!;
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")).DateTime);
    var decision = AssignmentFinder.Notifications.NotificationPolicy.Evaluate(assignment, envelope, filter, new(70, true), today);
    if (decision.Action == AssignmentFinder.Notifications.NotificationAction.Suppress)
        return Results.Conflict(new { error = "Aktualitet eller notispolicy stoppar förhandsvisningen." });
    return Results.Ok(new { mode = "SyntheticFixture", state = preview.State.ToString(), decision,
        message = AssignmentFinder.Notifications.EmailTemplate.Render(assignment, envelope, decision) });
});
await app.RunAsync();
