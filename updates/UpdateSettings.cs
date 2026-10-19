using System.Text.Json;

namespace RevitMCP.Updates;

public enum UpdateChannel { Stable, Beta }

public sealed record UpdateSettings
{
    public bool IncludeBeta { get; init; }
    public UpdateChannel Channel => IncludeBeta ? UpdateChannel.Beta : UpdateChannel.Stable;

    public static UpdateSettings Read(string applicationRoot)
    {
        var file = Path.Combine(applicationRoot, "update-settings.json");
        if (!File.Exists(file)) return new();
        return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(file), UpdateManifest.Json)
            ?? throw new InvalidDataException("Не удалось прочитать настройки обновления.");
    }

    public void Save(string applicationRoot)
    {
        Directory.CreateDirectory(applicationRoot);
        var file = Path.Combine(applicationRoot, "update-settings.json");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { IncludeBeta }, UpdateManifest.Json));
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public enum UpdateStage { Downloading, Verifying, Extracting }

public sealed record UpdateProgress(UpdateStage Stage, long Completed = 0, long Total = 0);
