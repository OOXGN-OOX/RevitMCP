using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace RevitMCP.Loader.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class UpdateCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        UpdateWindow.Open(data.Application.MainWindowHandle);
        return Result.Succeeded;
    }
}
