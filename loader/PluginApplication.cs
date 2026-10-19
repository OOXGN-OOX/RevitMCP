using Autodesk.Revit.UI;
using System.Reflection;

namespace RevitMCP.Loader;

public sealed class PluginApplication : IExternalApplication
{
    readonly Dictionary<PushButton, string> buttons = [];
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            CoreHost.Initialize();
            application.ControlledApplication.DocumentChanged += CoreHost.Changed;

            const string tabName = "Revit MCP";
            try { application.CreateRibbonTab(tabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }

            var panel = application.CreateRibbonPanel(tabName, "Модель");
            var button = new PushButtonData(
                "RevitMcpConnection",
                "Подключение",
                Assembly.GetExecutingAssembly().Location,
                typeof(Commands.ShowConnectionCommand).FullName!);
            button.ToolTip = "Подключение и восстановление локального MCP-моста Revit MCP";
            buttons.Add((PushButton)panel.AddItem(button), "connection");
            var autoConfirmButton = new PushButtonData("RevitMcpAutoConfirm", "Авто\nподтверждение",
                Assembly.GetExecutingAssembly().Location,
                typeof(Commands.ToggleAutoConfirmCommand).FullName!);
            autoConfirmButton.ToolTip = "Включить или выключить автоматическое подтверждение локальных MCP-команд";
            buttons.Add((PushButton)panel.AddItem(autoConfirmButton), "auto-confirm");
            var update = new PushButtonData("RevitMcpUpdate", "Обновление",
                Assembly.GetExecutingAssembly().Location, typeof(Commands.UpdateCommand).FullName!);
            update.ToolTip = "Обновить рабочую часть плагина без перезапуска Revit";
            buttons.Add((PushButton)panel.AddItem(update), "update");
            CoreHost.RefreshIcons = RefreshIcons;
            RefreshIcons();
            // Keep the ribbon available if another Revit process currently owns
            // the port. The connection button can retry once the port is free.
            try { BridgeService.Start(); }
            catch (System.Net.Sockets.SocketException) { }
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            application.ControlledApplication.DocumentChanged -= CoreHost.Changed;
            TaskDialog.Show("Revit MCP", $"Не удалось запустить локальный мост:\n{exception.Message}");
            CoreHost.Shutdown();
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentChanged -= CoreHost.Changed;
        CoreHost.Shutdown();
        return Result.Succeeded;
    }

    void RefreshIcons()
    {
        foreach (var (button, name) in buttons)
        {
            button.Image = RibbonAssets.Load(name, 16);
            button.LargeImage = RibbonAssets.Load(name, 32);
        }
    }
}
