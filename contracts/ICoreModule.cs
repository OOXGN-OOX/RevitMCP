namespace RevitMCP.Contracts;

// Keep this boundary independent of runtime types and Revit wrappers.
public interface ICoreModule : IDisposable
{
    int ProtocolVersion { get; }
    string Version { get; }
    string Execute(object application, string request, Func<bool> cancelled);
    void DocumentChanged(object? sender, object args);
}
