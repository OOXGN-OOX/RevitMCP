using Autodesk.Revit.UI;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RevitMCP.Loader;

internal sealed class BridgeService
{
    public static int Port => (_instance?._listener.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    private const int MaxLineLength = 1024 * 1024;
    private static readonly ConcurrentQueue<PendingRequest> Queue = new();
    private static BridgeService? _instance;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ExternalEvent _externalEvent;
    private readonly string _token;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly string _descriptorPath;
    private string? _documentName;

    private BridgeService()
    {
        _externalEvent = ExternalEvent.Create(new RevitRequestHandler());
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "instances");
        Directory.CreateDirectory(directory);
        _descriptorPath = Path.Combine(directory, $"{Environment.ProcessId}.json");
        _listener = new TcpListener(IPAddress.Loopback, 0);
        try { _listener.Start(8); }
        catch
        {
            _listener.Stop();
            _externalEvent.Dispose();
            _cancellation.Dispose();
            throw;
        }
        try { WriteDescriptor(); }
        catch
        {
            _listener.Stop();
            _externalEvent.Dispose();
            _cancellation.Dispose();
            throw;
        }
        _ = Task.Run(AcceptLoopAsync);
    }

    public static void Start()
    {
        if (_instance is not null) return;
        _instance = new BridgeService();
    }

    public static void Stop()
    {
        var instance = _instance;
        _instance = null;
        if (instance is null) return;
        instance._cancellation.Cancel();
        CancelPending("Мост остановлен.");
        instance._listener.Stop();
        instance._externalEvent.Dispose();
        instance._cancellation.Dispose();
        try { File.Delete(instance._descriptorPath); } catch (IOException) { }
    }

    public static void CancelPending(string message)
    {
        while (Queue.TryDequeue(out var pending))
        {
            pending.Cancelled = true;
            pending.Response = BridgeResponse.Failure(pending.Request.Id, message);
            pending.Completed.Set();
        }
    }

    private void WriteDescriptor()
    {
        var temporary = _descriptorPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new {
            processId = Environment.ProcessId, instanceId = _instanceId,
            port = ((IPEndPoint)_listener.LocalEndpoint).Port, token = _token,
            documentName = _documentName
        }), new UTF8Encoding(false));
        File.Move(temporary, _descriptorPath, true);
    }

    public static void RefreshContext(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs args)
    {
        if (_instance is not { } instance || sender is not UIApplication app) return;
        var name = app.ActiveUIDocument?.Document.Title;
        if (name == instance._documentName) return;
        instance._documentName = name;
        try { instance.WriteDescriptor(); } catch (IOException) { }
    }
    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                if (endpoint is null || !IPAddress.IsLoopback(endpoint.Address))
                {
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(() => HandleClientAsync(client, _cancellation.Token));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (_cancellation.IsCancellationRequested) { break; }
            catch { await Task.Delay(250); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
        {
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Failure("", "Request is empty or exceeds the 1 MB limit."), JsonTools.SerializerOptions));
                    return;
                }

                BridgeRequest? request;
                try { request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonTools.SerializerOptions); }
                catch (JsonException exception)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Failure("", $"Invalid JSON request: {exception.Message}"), JsonTools.SerializerOptions));
                    return;
                }

                if (request is null || !TokensMatch(request.Token, _token))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Failure(request?.Id ?? "", "Authentication failed."), JsonTools.SerializerOptions));
                    return;
                }

                if (request.Tool == "connection_info")
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Success(request.Id, new {
                        processId = Environment.ProcessId, instanceId = _instanceId, documentName = _documentName
                    }), JsonTools.SerializerOptions));
                    return;
                }

                var pending = new PendingRequest(request);
                Queue.Enqueue(pending);
                var raised = _externalEvent.Raise();
                if (raised == ExternalEventRequest.Denied)
                {
                    pending.Cancelled = true;
                    await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Failure(request.Id, "Revit is not accepting external API events."), JsonTools.SerializerOptions));
                    return;
                }

                var deadline = DateTime.UtcNow + RequestTimeout;
                while (!pending.Completed.Wait(TimeSpan.FromMilliseconds(200)))
                {
                    if (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0)
                    {
                        pending.Cancelled = true;
                        return;
                    }
                    if (DateTime.UtcNow >= deadline)
                    {
                        pending.Cancelled = true;
                        await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Failure(request.Id, "Revit did not respond within 5 minutes. The request was cancelled."), JsonTools.SerializerOptions));
                        return;
                    }
                }

                var response = pending.Response ?? BridgeResponse.Failure(request.Id, "Revit finished without a result.");
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonTools.SerializerOptions));
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception exception)
            {
                try { await writer.WriteLineAsync(JsonSerializer.Serialize(BridgeResponse.Failure("", exception.Message), JsonTools.SerializerOptions)); }
                catch { }
            }
        }
    }

    private static bool TokensMatch(string supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied) || supplied.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }

    public static void ProcessPending(UIApplication application)
    {
        while (Queue.TryDequeue(out var pending))
        {
            if (pending.Cancelled) { pending.Completed.Set(); continue; }
            try
            {
                if (pending.Cancelled)
                {
                    pending.Response = BridgeResponse.Failure(pending.Request.Id, "The MCP client cancelled the request before Revit started it.");
                    continue;
                }
                var response = CoreHost.Modules.Execute(application,
                    JsonSerializer.Serialize(pending.Request, JsonTools.SerializerOptions), () => pending.Cancelled);
                pending.Response = JsonSerializer.Deserialize<BridgeResponse>(response, JsonTools.SerializerOptions);
            }
            catch (Exception exception)
            {
                pending.Response = BridgeResponse.Failure(pending.Request.Id, exception.Message);
            }
            finally { pending.Completed.Set(); }
        }
    }
}

internal sealed class RevitRequestHandler : IExternalEventHandler
{
    public void Execute(UIApplication application) => BridgeService.ProcessPending(application);
    public string GetName() => "RevitMCP local MCP bridge";
}
