using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace RevitMCP.Loader.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowConnectionCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        try { BridgeService.Start(); }
        catch (Exception exception) { message = "Не удалось подключить MCP-мост: " + exception.Message; return Result.Failed; }
        var document = commandData.Application.ActiveUIDocument?.Document;
        var project = document is null ? "Проект не открыт" : document.Title;
        var installed = CoreHost.Modules.Current;
        TaskDialog.Show("Revit MCP", $"Локальный MCP-мост запущен.\nПорт: {BridgeService.Port}\nПроект: {project}\nВерсия: {installed?.Manifest.Version ?? "не загружена"}\nСборка: {installed?.Manifest.BuildId[..8] ?? "—"}\nАвтоподтверждение: {(ApprovalSettings.AutoConfirm ? "включено" : "выключено")}");
        return Result.Succeeded;
    }
}
