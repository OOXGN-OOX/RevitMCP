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
UpdateManifest Manifest(byte[] bytes, char id, long sequence) => new() { Version = "0.0.2", BuildId = new string(id, 40), Sequence = sequence, Files = new() { ["core/RevitMCP.Core.dll"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() } };
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
    Check(!Directory.EnumerateDirectories(Path.Combine(root, "updates"), "*.staging-*").Any(), "failed staging cleaned up");
    var bytes = File.ReadAllBytes(zb);
    var feed = new UpdateFeed { Manifest = mb, PackageUrl = "https://ooxgn-oox.github.io/RevitMCP/updates/0.0.2/RevitMCP-" + mb.BuildId + "-Update.zip", Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    UpdateClient.ValidateFeed(feed); Check(true, "owned HTTPS feed accepted");
    foreach (var address in new[] { "http://ooxgn-oox.github.io/evil.zip", "https://example.com/a.zip", feed.PackageUrl + "?redirect=1" })
        Reject(() => UpdateClient.ValidateFeed(feed with { PackageUrl = address }), "foreign or mutable package URL rejected");
    using var http = new HttpClient(new BytesHandler(bytes));
    var client = new UpdateClient(http, root);
    Check((await client.StageAsync(feed, null)).Manifest.BuildId == mb.BuildId, "download validates and stages package");
    try { await client.StageAsync(feed with { Sha256 = new string('0', 64) }, null); throw new Exception("checksum accepted"); }
    catch (InvalidDataException) { Check(true, "download hash mismatch rejected"); }
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await client.StageAsync(feed, null, cancelled.Token); throw new Exception("cancellation ignored"); }
    catch (OperationCanceledException) { Check(true, "cancelled download cannot activate"); }
    Check(!Directory.EnumerateFiles(Path.Combine(root, "downloads")).Any(), "download temporary files cleaned up");
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
