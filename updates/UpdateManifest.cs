using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitMCP.Updates;

public sealed record UpdateManifest
{
    public int SchemaVersion { get; init; } = 1;
    public int ProtocolVersion { get; init; } = 1;
    public string Version { get; init; } = "";
    public string MinimumLoaderVersion { get; init; } = "0.0.2";
    public string BuildId { get; init; } = "";
    public long Sequence { get; init; }
    public string Channel { get; init; } = "legacy";
    public string SourceBranch { get; init; } = "";
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public void Validate()
    {
        if (SchemaVersion != 1 || ProtocolVersion != 1) throw new InvalidDataException("Несовместимый формат обновления.");
        if (!System.Version.TryParse(Version, out _) || !System.Version.TryParse(MinimumLoaderVersion, out _))
            throw new InvalidDataException("Некорректная версия пакета.");
        if (!Regex.IsMatch(BuildId, "^[a-f0-9]{40}$") || Sequence < 0) throw new InvalidDataException("Некорректный номер сборки.");
        if (Channel is not ("stable" or "beta" or "legacy")
            || (Channel == "stable" && SourceBranch != "main")
            || (Channel == "beta" && (SourceBranch == "main" || !Regex.IsMatch(SourceBranch, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$"))))
            throw new InvalidDataException("Канал обновления не соответствует исходной ветке.");
        if (Files.Count is < 1 or > 128 || !Files.ContainsKey("core/RevitMCP.Core.dll")) throw new InvalidDataException("Неполный пакет обновления.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, digest) in Files)
        {
            if (!names.Add(name) || !IsAllowedFile(name) || !Regex.IsMatch(digest, "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Недопустимое содержимое пакета.");
        }
    }

    public static bool IsAllowedFile(string name) => name == "core/RevitMCP.Core.dll" ||
        Regex.IsMatch(name, "^assets/[a-z0-9-]+\\.png$");

    public void VerifyDirectory(string directory)
    {
        Validate();
        foreach (var (name, digest) in Files)
        {
            var file = SafePath.Child(directory, name);
            using var input = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Проверка файла не пройдена: " + name);
        }
        var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Where(name => name != "manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(Files.Keys)) throw new InvalidDataException("В пакете присутствуют лишние файлы.");
    }

    public static UpdateManifest Read(string directory)
    {
        var file = SafePath.Child(directory, "manifest.json");
        if (new FileInfo(file).Length > 128 * 1024) throw new InvalidDataException("Слишком большой манифест.");
        var value = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("Пустой манифест.");
        value.VerifyDirectory(directory);
        return value;
    }
}

public static class SafePath
{
    public static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') ||
            relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Некорректный путь в пакете.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь выходит за пределы каталога.");
        return full;
    }
}

public sealed record UpdateFeed
{
    public UpdateManifest Manifest { get; init; } = new();
    public string PackageUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
}

public sealed record InstalledVersion(string RelativePath, UpdateManifest Manifest);
