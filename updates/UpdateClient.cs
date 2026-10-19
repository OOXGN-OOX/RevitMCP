using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitMCP.Updates;

public sealed class UpdateClient(HttpClient http, string applicationRoot)
{
    public const string DevelopmentFeed = "https://ooxgn-oox.github.io/RevitMCP/updates/0.0.2/manifest.json";
    public const long MaximumDownload = 32 * 1024 * 1024;
    public const long MaximumExpanded = 64 * 1024 * 1024;

    public async Task<UpdateFeed> CheckAsync(CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DevelopmentFeed + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var body = new MemoryStream();
        await CopyLimited(input, body, 128 * 1024, null, cancellation);
        var feed = JsonSerializer.Deserialize<UpdateFeed>(body.ToArray(), UpdateManifest.Json) ?? throw new InvalidDataException("Пустой ответ сервера.");
        ValidateFeed(feed);
        return feed;
    }

    public static void ValidateFeed(UpdateFeed feed)
    {
        feed.Manifest.Validate();
        if (!Uri.TryCreate(feed.PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "ooxgn-oox.github.io" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !Regex.IsMatch(uri.AbsolutePath, "^/RevitMCP/updates/0\\.0\\.2/RevitMCP-[a-f0-9]{40}-Update\\.zip$"))
            throw new InvalidDataException("Недопустимый адрес обновления.");
        if (feed.Size is < 1 or > MaximumDownload || !Regex.IsMatch(feed.Sha256, "^[a-f0-9]{64}$"))
            throw new InvalidDataException("Некорректные данные обновления.");
    }

    public async Task<InstalledVersion> StageAsync(UpdateFeed feed, IProgress<long>? progress, CancellationToken cancellation = default)
    {
        ValidateFeed(feed);
        var cache = Path.Combine(applicationRoot, "downloads");
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var response = await http.GetAsync(feed.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != feed.Size)
                throw new InvalidDataException("Размер загружаемого пакета не совпадает с манифестом.");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyLimited(source, target, feed.Size, progress, cancellation);
            if (new FileInfo(file).Length != feed.Size) throw new InvalidDataException("Пакет загружен не полностью.");
            await using (var source = File.OpenRead(file))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(source, cancellation)).Equals(feed.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
            return ExtractVerified(file, feed.Manifest, applicationRoot);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    public static InstalledVersion ExtractVerified(string zipPath, UpdateManifest expected, string applicationRoot)
    {
        expected.Validate();
        var relative = "updates/" + expected.BuildId;
        var destination = SafePath.Child(applicationRoot, relative);
        if (Directory.Exists(destination))
        {
            var existing = UpdateManifest.Read(destination);
            RequireSame(expected, existing);
            return new(relative, existing);
        }
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > 256) throw new InvalidDataException("Слишком много файлов в обновлении.");
            long expanded = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith('/')) { _ = SafePath.Child(staging, name.TrimEnd('/')); continue; }
                var target = SafePath.Child(staging, name);
                if (!names.Add(name) || (name != "manifest.json" && !expected.Files.ContainsKey(name)) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Лишний, повторный или связанный файл в архиве.");
                expanded = checked(expanded + entry.Length);
                if (expanded > MaximumExpanded) throw new InvalidDataException("Слишком большой распакованный пакет.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew);
                CopyLimited(input, output, entry.Length, null, CancellationToken.None).GetAwaiter().GetResult();
            }
            var manifest = UpdateManifest.Read(staging);
            RequireSame(expected, manifest);
            Directory.Move(staging, destination);
            return new(relative, manifest);
        }
        finally
        {
            // staging is a freshly created, validated child of the owned updates directory.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    static void RequireSame(UpdateManifest expected, UpdateManifest actual)
    {
        if (expected.Version != actual.Version || expected.BuildId != actual.BuildId || expected.Sequence != actual.Sequence ||
            expected.MinimumLoaderVersion != actual.MinimumLoaderVersion || expected.ProtocolVersion != actual.ProtocolVersion ||
            expected.Files.Count != actual.Files.Count || expected.Files.Any(pair => !actual.Files.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidDataException("Манифест пакета отличается от выбранной сборки.");
    }

    static async Task CopyLimited(Stream input, Stream output, long limit, IProgress<long>? progress, CancellationToken cancellation)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellation);
            if (count == 0) break;
            total = checked(total + count);
            if (total > limit) throw new InvalidDataException("Превышен допустимый размер данных.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
            progress?.Report(total);
        }
    }
}
