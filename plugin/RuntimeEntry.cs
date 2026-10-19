using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RevitMCP.Contracts;
using System.Text.Json;

namespace RevitMCP;

public sealed class RuntimeEntry : ICoreModule
{
    public int ProtocolVersion => 1;
    public string Version => typeof(RuntimeEntry).Assembly.GetName().Version!.ToString(3);
    public string Execute(object application, string request, Func<bool> cancelled)
    {
        var parsed = JsonSerializer.Deserialize<BridgeRequest>(request, JsonTools.SerializerOptions)
            ?? throw new InvalidDataException("Пустой запрос.");
        return JsonSerializer.Serialize(RevitTools.Execute((UIApplication)application, parsed, cancelled), JsonTools.SerializerOptions);
    }
    public void DocumentChanged(object? sender, object args) => RevitTools.OnDocumentChanged(sender, (DocumentChangedEventArgs)args);
    public void Dispose() => RevitTools.ClearRuntimeState();
}
