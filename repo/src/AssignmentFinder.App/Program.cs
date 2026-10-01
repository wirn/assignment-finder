using System.Security.Cryptography;
using System.Text;
using AssignmentFinder.App;
using AssignmentFinder.Data;
using Microsoft.EntityFrameworkCore;

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
builder.Services.AddSingleton<ImportRunner>();
builder.Services.AddSingleton(new ImportSettings(
    builder.Configuration["ImportDirectory"] ?? "data/private/import-inbox",
    builder.Configuration["FilterPath"] ?? "data/private/filter-settings.json"));
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
    mode = "LocalImportOnly", paidAnalysis = false, email = false, schedule = false,
    assignments = await db.Assignments.CountAsync(ct), revisions = await db.Revisions.CountAsync(ct),
    latestRun = await db.Runs.OrderByDescending(r => r.StartedAtUtc).Select(r => new { r.Id, r.State, r.Imported, r.Failed, r.StartedAtUtc, r.FinishedAtUtc }).FirstOrDefaultAsync(ct)
}));
await app.RunAsync();
