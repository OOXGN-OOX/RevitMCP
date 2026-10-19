using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using RevitMCP.Updates;

var root = Path.Combine(Path.GetTempPath(), "RevitMCP-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("Accepted: " + name); }
var a = File.ReadAllBytes(args[0]); var b = File.ReadAllBytes(args[1]);
UpdateManifest Manifest(byte[] bytes, char id, long sequence) => new() { Version = "0.0.2", BuildId = new string(id, 40), Sequence = sequence, Channel = "stable", SourceBranch = "main", Files = new() { ["core/RevitMCP.Core.dll"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() } };
string Zip(UpdateManifest manifest, byte[] bytes, string? extra = null)
{
    var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
    using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
    using (var entry = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(entry, manifest, UpdateManifest.Json);
    using (var entry = zip.CreateEntry("core/RevitMCP.Core.dll").Open()) entry.Write(bytes);
    if (extra is not null) { using var entry = zip.CreateEntry(extra).Open(); entry.WriteByte(0); }
    return file;
}
try
{
    var ma = Manifest(a, 'a', 1); var mb = Manifest(b, 'b', 2);
    var za = Zip(ma, a); var zb = Zip(mb, b);
    var ia = UpdateClient.ExtractVerified(za, ma, root); var ib = UpdateClient.ExtractVerified(zb, mb, root);
    using var manager = new ModuleManager(root, new Version(0, 0, 2));
    manager.Activate(ia);
    Check(manager.Execute(new object(), "hello", () => false) == "A:hello", "load first core");
    Check(manager.IsNewer(mb) && !manager.IsNewer(ma), "development sequence comparison");
    Replace(manager, ib);
    Check(manager.Execute(new object(), "hello", () => false) == "B:hello", "replace core in running process");
    var old = manager.RetiredContexts[0];
    for (var i = 0; old.IsAlive && i < 10; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    Check(!old.IsAlive, "previous AssemblyLoadContext is collected");
    using (File.Open(Path.Combine(root, ia.RelativePath, "core/RevitMCP.Core.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    Check(true, "loaded source DLL has no file lock");
    manager.Rollback(); Check(manager.Execute(new object(), "test", () => false) == "A:test", "rollback restores previous behavior");
    Reject(() => manager.Activate(ia), "same build cannot replace active build");
    var broken = Manifest([1, 2, 3], 'c', 3);
    var badInstall = UpdateClient.ExtractVerified(Zip(broken, [1, 2, 3]), broken, root);
    Reject(() => manager.Activate(badInstall), "invalid assembly fails activation");
    Check(manager.Execute(new object(), "still", () => false) == "A:still", "failed activation keeps active module");
    Check(JsonSerializer.Deserialize<InstalledVersion>(File.ReadAllText(Path.Combine(root, "active.json")), UpdateManifest.Json)!.Manifest.BuildId == ma.BuildId, "failed activation keeps disk pointer");
    var newerLoader = Manifest(a, 'd', 4) with { MinimumLoaderVersion = "9.0.0" };
    var incompatible = UpdateClient.ExtractVerified(Zip(newerLoader, a), newerLoader, root);
    Reject(() => manager.Activate(incompatible), "incompatible loader rejected");
    var fresh = Manifest(a, 'e', 5);
    Reject(() => UpdateClient.ExtractVerified(Zip(fresh, b), fresh, root), "per-file hash mismatch rejected");
    foreach (var extra in new[] { "../outside.dll", "extra.dll", "core/RevitMCP.Core.dll", "CORE/RevitMCP.Core.dll", "C:/outside.dll" })
        Reject(() => UpdateClient.ExtractVerified(Zip(fresh, a, extra), fresh, root), "reject archive entry " + extra);
    Check(!File.Exists(Path.Combine(root, "outside.dll")), "archive never writes outside staging");
    Check(!Directory.EnumerateDirectories(Path.Combine(root, "updates"), "*.staging-*", SearchOption.AllDirectories).Any(), "failed staging cleaned up");
    var bytes = File.ReadAllBytes(zb);
    var feed = new UpdateFeed { Manifest = mb, PackageUrl = "https://github.com/OOXGN-OOX/RevitMCP/releases/download/v0.0.2/RevitMCP-" + mb.BuildId + "-Update.zip", Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    UpdateClient.ValidateFeed(feed); Check(true, "owned HTTPS feed accepted");
    foreach (var address in new[] { "http://ooxgn-oox.github.io/evil.zip", "https://example.com/a.zip", feed.PackageUrl + "?redirect=1" })
        Reject(() => UpdateClient.ValidateFeed(feed with { PackageUrl = address }), "foreign or mutable package URL rejected");
    using var http = new HttpClient(new BytesHandler(bytes));
    var client = new UpdateClient(http, root);
    var progress = new List<UpdateProgress>();
    Check((await client.StageAsync(feed, new InlineProgress(progress.Add))).Manifest.BuildId == mb.BuildId, "download validates and stages package");
    Check(progress.Select(p => p.Stage).Distinct().SequenceEqual(new[] { UpdateStage.Downloading, UpdateStage.Verifying, UpdateStage.Extracting }), "progress covers download, verification and extraction in order");
    Check(progress.Last(p => p.Stage == UpdateStage.Downloading).Completed == feed.Size, "download progress reports actual bytes");
    try { await client.StageAsync(feed with { Sha256 = new string('0', 64) }, null); throw new Exception("checksum accepted"); }
    catch (InvalidDataException) { Check(true, "download hash mismatch rejected"); }
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await client.StageAsync(feed, null, cancelled.Token); throw new Exception("cancellation ignored"); }
    catch (OperationCanceledException) { Check(true, "cancelled download cannot activate"); }
    Check(!Directory.EnumerateFiles(Path.Combine(root, "downloads")).Any(), "download temporary files cleaned up");
    var betaManifest = Manifest(b, 'f', 999) with { Channel = "beta", SourceBranch = "0.0.2" };
    var betaFeed = feed with { Manifest = betaManifest, PackageUrl = "https://ooxgn-oox.github.io/RevitMCP/updates/beta/RevitMCP-" + betaManifest.BuildId + "-Update.zip" };
    UpdateClient.ValidateFeed(betaFeed, UpdateChannel.Beta);
    Reject(() => UpdateClient.ValidateFeed(betaFeed), "stable selection refuses beta package");
    Reject(() => UpdateClient.ValidateFeed(feed, UpdateChannel.Beta), "beta selection refuses stable package");
    Reject(() => UpdateClient.ValidateFeed(feed with { PackageUrl = feed.PackageUrl.Replace(mb.BuildId, ma.BuildId) }), "package URL must match manifest commit");
    Reject(() => (betaManifest with { SourceBranch = "main" }).Validate(), "main cannot publish beta manifests");
    Reject(() => (mb with { SourceBranch = "0.0.2" }).Validate(), "development branch cannot publish stable manifests");
    Reject(() => UpdateClient.ExtractVerified(zb, mb with { Channel = "beta", SourceBranch = "0.0.2" }, root), "cached package cannot change channel");
    Check(!manager.IsNewer(betaManifest), "stable version does not regress to beta of same version");
    var betaRoot = Path.Combine(root, "beta-install");
    var betaZip = Zip(betaManifest, b);
    using (var betaManager = new ModuleManager(betaRoot, new Version(0, 0, 2)))
    {
        betaManager.Activate(UpdateClient.ExtractVerified(betaZip, betaManifest, betaRoot));
        Check(betaManager.IsNewer(ma), "stable promotion ignores larger beta workflow sequence");
        var promoted = betaManifest with { Channel = "stable", SourceBranch = "main", Sequence = 1 };
        Check(betaManager.IsNewer(promoted), "same commit can be promoted from beta to stable");
        betaManager.Activate(UpdateClient.ExtractVerified(Zip(promoted, b), promoted, betaRoot));
        Check(betaManager.Current!.Manifest.Channel == "stable", "stable and beta packages of same commit have separate installation directories");
    }
    var settingsRoot = Path.Combine(root, "preferences");
    Check(!UpdateSettings.Read(settingsRoot).IncludeBeta, "beta is disabled by default");
    new UpdateSettings { IncludeBeta = true }.Save(settingsRoot);
    Check(UpdateSettings.Read(settingsRoot).Channel == UpdateChannel.Beta, "beta opt-in persists");
    new UpdateSettings().Save(settingsRoot);
    Check(UpdateSettings.Read(settingsRoot).Channel == UpdateChannel.Stable, "beta opt-out persists");
    File.WriteAllText(Path.Combine(settingsRoot, "update-settings.json"), "broken");
    Reject(() => UpdateSettings.Read(settingsRoot), "invalid preferences do not silently enable beta");
    using (var checking = new HttpClient(new ResponseHandler(request =>
    {
        Check(request.RequestUri!.GetLeftPart(UriPartial.Path) == UpdateClient.StableFeed, "default check only requests stable feed");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }))) Check(await new UpdateClient(checking, root).CheckAsync() is null, "absent stable release does not fall back to beta");
    using (var checking = new HttpClient(new ResponseHandler(request =>
    {
        Check(request.RequestUri!.GetLeftPart(UriPartial.Path) == UpdateClient.BetaFeed, "opt-in requests beta feed");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(betaFeed, UpdateManifest.Json)) };
    }))) Check((await new UpdateClient(checking, root).CheckAsync(UpdateChannel.Beta))!.Manifest.Channel == "beta", "beta feed validates before display");
    using (var checking = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(betaFeed, UpdateManifest.Json)) })))
    {
        try { await new UpdateClient(checking, root).CheckAsync(); throw new Exception("beta feed accepted by stable channel"); }
        catch (InvalidDataException) { Check(true, "wrong-channel server response rejected"); }
    }
    using (var stopExtraction = new CancellationTokenSource())
    {
        var cancelledRoot = Path.Combine(root, "cancel-extract");
        try
        {
            UpdateClient.ExtractVerified(betaZip, betaManifest, cancelledRoot,
                new InlineProgress(p => { if (p.Stage == UpdateStage.Extracting && p.Completed > 0) stopExtraction.Cancel(); }), stopExtraction.Token);
            throw new Exception("extraction cancellation ignored");
        }
        catch (OperationCanceledException) { Check(true, "extraction supports cancellation between entries"); }
        Check(!Directory.EnumerateFiles(Path.Combine(cancelledRoot, "updates"), "*", SearchOption.AllDirectories).Any()
            && !Directory.Exists(Path.Combine(cancelledRoot, "updates", "beta", betaManifest.BuildId)), "cancelled extraction leaves no installation or staging");
    }
    manager.Dispose();
    using var restored = new ModuleManager(root, new Version(0, 0, 2)); restored.LoadActive();
    Check(restored.Execute(new object(), "restart", () => false) == "A:restart", "active version survives process restart");
    Console.WriteLine($"{checks} update checks passed.");
}
finally { Directory.Delete(root, true); }

[MethodImpl(MethodImplOptions.NoInlining)]
static void Replace(ModuleManager manager, InstalledVersion next) => manager.Activate(next);
sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}

sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
{
    public void Report(UpdateProgress value) => report(value);
}

sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(response(request));
    }
}
