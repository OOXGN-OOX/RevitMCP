using RevitMCP.Contracts;
namespace RevitMCP;
public sealed class RuntimeEntry : ICoreModule
{
    public int ProtocolVersion => 1;
    public string Version => "0.0.2";
    public string Execute(object application, string request, Func<bool> cancelled)
    {
#if VARIANT_B
        return "B:" + request;
#else
        return "A:" + request;
#endif
    }
    public void DocumentChanged(object? sender, object args) { }
    public void Dispose() { }
}
