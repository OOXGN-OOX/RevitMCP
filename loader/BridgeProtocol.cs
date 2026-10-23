using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitMCP.Loader;

internal sealed class BridgeRequest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("token")] public string Token { get; init; } = "";
    [JsonPropertyName("tool")] public string Tool { get; init; } = "";
    [JsonPropertyName("arguments")] public JsonElement Arguments { get; init; }
}
internal sealed class BridgeResponse
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("errorDetails")] public JsonElement? ErrorDetails { get; init; }
    public static BridgeResponse Failure(string id, string error) => new() { Id = id, Error = error };
    public static BridgeResponse Success(string id, object result) => new() { Id = id, Ok = true, Result = result };
}
internal sealed class PendingRequest(BridgeRequest request)
{
    public BridgeRequest Request { get; } = request;
    public ManualResetEventSlim Completed { get; } = new(false);
    public BridgeResponse? Response { get; set; }
    public volatile bool Cancelled;
}
internal static class JsonTools
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);
}
