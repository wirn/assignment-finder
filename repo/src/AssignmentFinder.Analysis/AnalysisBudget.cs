using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssignmentFinder.Analysis;

public sealed record OpenAiSettings(
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] string Model,
    [property: JsonRequired] string PricingModel,
    [property: JsonRequired] decimal TotalBudgetSek,
    [property: JsonRequired] decimal InputUsdPerMillion,
    [property: JsonRequired] decimal OutputUsdPerMillion,
    [property: JsonRequired] decimal SekPerUsd,
    [property: JsonRequired] decimal CostSafetyFactor,
    [property: JsonRequired] int MaxOutputTokens)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || Model != PricingModel || TotalBudgetSek is <= 0 or > 10
            || InputUsdPerMillion <= 0 || OutputUsdPerMillion <= 0 || SekPerUsd <= 0
            || CostSafetyFactor < 1 || MaxOutputTokens is < 512 or > 8192)
            throw new InvalidDataException("Ogiltig AI-konfiguration. Modell/prismodell måste matcha och testbudgeten får vara högst 10 kr.");
    }

    public decimal Estimate(int inputTokenBound) => decimal.Ceiling(
        (inputTokenBound * InputUsdPerMillion + MaxOutputTokens * OutputUsdPerMillion)
        / 1_000_000m * SekPerUsd * CostSafetyFactor * 10_000m) / 10_000m;
}

public sealed record BudgetEntry(string RequestHash, decimal ReservedSek, DateTimeOffset ReservedAtUtc);
public sealed record BudgetState([property: JsonRequired] decimal LimitSek,
    [property: JsonRequired] BudgetEntry[] Entries);

// Full worst-case reservation is retained even after success or uncertain failure.
// Never refund based on an absent usage report, and never retry a reserved request blindly.
public sealed class AnalysisBudget(string path)
{
    public BudgetState Read(decimal configuredLimit)
    {
        if (!File.Exists(path)) return new(configuredLimit, []);
        var state = JsonSerializer.Deserialize<BudgetState>(File.ReadAllText(path), AnalysisValidator.JsonOptions)
            ?? throw new InvalidDataException("Budgetjournal saknas.");
        if (state.LimitSek is <= 0 or > 10 || state.Entries is null
            || state.Entries.Any(e => e is null || e.ReservedSek <= 0 || string.IsNullOrWhiteSpace(e.RequestHash))
            || state.Entries.Select(e => e.RequestHash).Distinct().Count() != state.Entries.Length
            || state.Entries.Sum(e => e.ReservedSek) > state.LimitSek)
            throw new InvalidDataException("Budgetjournal är ogiltig. Inga anrop tillåts.");
        return state with { LimitSek = Math.Min(state.LimitSek, configuredLimit) };
    }

    public void Reserve(string hash, decimal amount, decimal configuredLimit)
    {
        if (amount <= 0 || configuredLimit is <= 0 or > 10) throw new InvalidDataException("Ogiltig budgetreservation.");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(fullPath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // FileShare.None also excludes another CLI process. A busy lock fails closed.
        using var gate = new FileStream(fullPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var state = Read(configuredLimit);
        if (state.Entries.Any(e => e.RequestHash == hash))
            throw new InvalidDataException("Detta analysunderlag har redan reserverats. Kontrollera tidigare resultat innan ett nytt försök.");
        if (state.Entries.Sum(e => e.ReservedSek) + amount > state.LimitSek)
            throw new InvalidDataException("Testbudgeten räcker inte för ytterligare ett AI-anrop.");
        state = state with { Entries = [..state.Entries, new(hash, amount, DateTimeOffset.UtcNow)] };
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(file, state, AnalysisValidator.JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
