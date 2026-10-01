using System.Text.Json;
using System.Text.Json.Serialization;
using AssignmentFinder.Brainville;

internal static class FilterCommands
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<FilterSettings> LoadAsync(string path = "data/private/filter-settings.json")
    {
        var settings = JsonSerializer.Deserialize<FilterSettings>(await File.ReadAllTextAsync(path), JsonOptions)
            ?? throw new InvalidDataException("Filterkonfiguration saknas.");
        settings.Validate();
        return settings;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length is < 2 or > 3) throw new ArgumentException("Använd filter <uppdrags-JSON> [filterkonfiguration].");
            var settings = await LoadAsync(args.Length == 3 ? args[2] : "data/private/filter-settings.json");
            var assignment = JsonSerializer.Deserialize<Assignment>(await File.ReadAllTextAsync(args[1]), JsonOptions)
                ?? throw new InvalidDataException("Uppdrag saknas.");
            Console.WriteLine(JsonSerializer.Serialize(new AssignmentFilter().Evaluate(assignment, settings), JsonOptions));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error is JsonException ? "Ogiltig JSON i uppdrag eller filterkonfiguration." : error.Message);
            return 1;
        }
    }
}
