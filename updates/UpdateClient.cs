using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitMCP.Updates;

public sealed class UpdateClient(HttpClient http, string applicationRoot)
{
    public const string StableFeed = "https://github.com/OOXGN-OOX/RevitMCP/releases/latest/download/update.json";
    public const string BetaFeed = "https://ooxgn-oox.github.io/RevitMCP/updates/beta/manifest.json";
    public const long MaximumDownload = 32 * 1024 * 1024;
    public const long MaximumExpanded = 64 * 1024 * 1024;

    public async Task<UpdateFeed?> CheckAsync(UpdateChannel channel = UpdateChannel.Stable, CancellationToken cancellation = default)
    {
        var address = channel switch { UpdateChannel.Stable => StableFeed, UpdateChannel.Beta => BetaFeed, _ => throw new ArgumentOutOfRangeException(nameof(channel)) };
        using var request = new HttpRequestMessage(HttpMethod.Get, address + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var body = new MemoryStream();
        await CopyLimited(input, body, 128 * 1024, null, cancellation).ConfigureAwait(false);
        var feed = JsonSerializer.Deserialize<UpdateFeed>(body.ToArray(), UpdateManifest.Json) ?? throw new InvalidDataException("Пустой ответ сервера.");
        ValidateFeed(feed, channel);
        return feed;
    }

    public static void ValidateFeed(UpdateFeed feed, UpdateChannel channel = UpdateChannel.Stable)
    {
        feed.Manifest.Validate();
        var expectedChannel = channel switch { UpdateChannel.Stable => "stable", UpdateChannel.Beta => "beta", _ => throw new ArgumentOutOfRangeException(nameof(channel)) };
        if (feed.Manifest.Channel != expectedChannel) throw new InvalidDataException("Сборка относится к другому каналу обновлений.");
        var package = $"RevitMCP-{feed.Manifest.BuildId}-Update.zip";
        var expectedUrl = channel == UpdateChannel.Stable
            ? $"https://github.com/OOXGN-OOX/RevitMCP/releases/download/v{feed.Manifest.Version}/{package}"
            : $"https://ooxgn-oox.github.io/RevitMCP/updates/beta/{package}";
        if (feed.PackageUrl != expectedUrl)
            throw new InvalidDataException("Недопустимый адрес обновления.");
        if (feed.Size is < 1 or > MaximumDownload || !Regex.IsMatch(feed.Sha256, "^[a-f0-9]{64}$"))
            throw new InvalidDataException("Некорректные данные обновления.");
    }

    public async Task<InstalledVersion> StageAsync(UpdateFeed feed, IProgress<UpdateProgress>? progress, CancellationToken cancellation = default, UpdateChannel channel = UpdateChannel.Stable)
    {
        ValidateFeed(feed, channel);
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new(UpdateStage.Downloading, 0, feed.Size));
        var cache = Path.Combine(applicationRoot, "downloads");
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var response = await http.GetAsync(feed.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != feed.Size)
                throw new InvalidDataException("Размер загружаемого пакета не совпадает с манифестом.");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
            await using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyLimited(source, target, feed.Size, progress is null ? null : new DownloadProgress(progress, feed.Size), cancellation).ConfigureAwait(false);
            if (new FileInfo(file).Length != feed.Size) throw new InvalidDataException("Пакет загружен не полностью.");
            progress?.Report(new(UpdateStage.Verifying));
            await using (var source = File.OpenRead(file))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(source, cancellation).ConfigureAwait(false)).Equals(feed.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
            return await Task.Run(() => ExtractVerified(file, feed.Manifest, applicationRoot, progress, cancellation), cancellation).ConfigureAwait(false);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    public static InstalledVersion ExtractVerified(string zipPath, UpdateManifest expected, string applicationRoot, IProgress<UpdateProgress>? progress = null, CancellationToken cancellation = default)
    {
        expected.Validate();
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new(UpdateStage.Extracting, 0, expected.Files.Count + 1));
        var relative = "updates/" + expected.Channel + "/" + expected.BuildId;
        var destination = SafePath.Child(applicationRoot, relative);
        if (Directory.Exists(destination))
        {
            var existing = UpdateManifest.Read(destination);
            cancellation.ThrowIfCancellationRequested();
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
                cancellation.ThrowIfCancellationRequested();
                var name = entry.FullName;
                if (name.EndsWith('/')) { _ = SafePath.Child(staging, name.TrimEnd('/')); continue; }
                var target = SafePath.Child(staging, name);
                if (!names.Add(name) || (name != "manifest.json" && !expected.Files.ContainsKey(name)) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Лишний, повторный или связанный файл в архиве.");
                expanded = checked(expanded + entry.Length);
                if (expanded > MaximumExpanded) throw new InvalidDataException("Слишком большой распакованный пакет.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var input = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew))
                    CopyLimited(input, output, entry.Length, null, cancellation).GetAwaiter().GetResult();
                progress?.Report(new(UpdateStage.Extracting, names.Count, expected.Files.Count + 1));
            }
            var manifest = UpdateManifest.Read(staging);
            RequireSame(expected, manifest);
            cancellation.ThrowIfCancellationRequested();
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
            expected.Channel != actual.Channel || expected.SourceBranch != actual.SourceBranch ||
            expected.Files.Count != actual.Files.Count || expected.Files.Any(pair => !actual.Files.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidDataException("Манифест пакета отличается от выбранной сборки.");
    }

    static async Task CopyLimited(Stream input, Stream output, long limit, IProgress<long>? progress, CancellationToken cancellation)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (count == 0) break;
            total = checked(total + count);
            if (total > limit) throw new InvalidDataException("Превышен допустимый размер данных.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
            progress?.Report(total);
        }
    }

    sealed class DownloadProgress(IProgress<UpdateProgress> progress, long total) : IProgress<long>
    {
        public void Report(long value) => progress.Report(new(UpdateStage.Downloading, value, total));
    }
}
