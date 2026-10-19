using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RevitMCP.Updates;

namespace RevitMCP.Loader;

internal static class CoreHost
{
    public static readonly string ApplicationRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "Application");
    public static ModuleManager Modules { get; private set; } = null!;
    public static ApiDispatcher Dispatcher { get; private set; } = null!;
    public static Action? RefreshIcons { get; set; }

    public static void Initialize()
    {
        Dispatcher = new ApiDispatcher();
        Modules = new ModuleManager(ApplicationRoot, new Version(0, 0, 2));
        try { Modules.LoadActive(); }
        catch (Exception ex) { Log(ex.Message); }
    }

    public static void Changed(object? sender, DocumentChangedEventArgs args) => Modules.DocumentChanged(sender, args);

    public static void Activate(InstalledVersion next)
    {
        BridgeService.CancelPending("Revit MCP обновляется. Повторите запрос и создайте новый предпросмотр.");
        Modules.Activate(next);
        RefreshIcons?.Invoke();
        Log("Activated " + next.Manifest.Version + " " + next.Manifest.BuildId);
    }

    public static void Rollback()
    {
        BridgeService.CancelPending("Revit MCP переключает сборку. Создайте новый предпросмотр.");
        Modules.Rollback();
        RefreshIcons?.Invoke();
        Log("Rolled back to " + Modules.Current?.Manifest.BuildId);
    }

    public static void Shutdown()
    {
        UpdateWindow.CloseActive();
        BridgeService.Stop();
        Dispatcher?.Dispose();
        Modules?.Dispose();
        RefreshIcons = null;
    }

    public static void Log(string message)
    {
        try
        {
            var data = Path.GetDirectoryName(ApplicationRoot)!;
            Directory.CreateDirectory(data);
            File.AppendAllText(Path.Combine(data, "update.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
