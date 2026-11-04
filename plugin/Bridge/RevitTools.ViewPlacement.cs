using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static object PreviewPlaceViewsOnSheets(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "placeViewsOnSheets", "Размещение видов на листах", PlaceViewsOnSheets, ReadPlacedViewsAfterCommit);

    private static object ApplyPlaceViewsOnSheets(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "placeViewsOnSheets", "Разместить виды на листах?", PlaceViewsOnSheets, cancelled, ReadPlacedViewsAfterCommit);

    private static void SetSheetViewportType(Document doc, Viewport viewport, JsonElement request)
    {
        if (!request.TryGetProperty("viewportTypeId", out _)) return;
        var typeId = new ElementId(HostedReadId(request, "viewportTypeId"));
        if (doc.GetElement(typeId) is not ElementType type || type.Category?.Id.Value != (long)BuiltInCategory.OST_Viewports
            || !viewport.IsValidType(typeId))
            throw new ArgumentException("viewportTypeId must identify a compatible viewport type.");
        if (viewport.GetTypeId() != typeId) viewport.ChangeTypeId(typeId);
    }

    private static object PlaceViewsOnSheets(Document doc, JsonElement args)
    {
        var requests = args.GetProperty("placements");
        if (requests.GetArrayLength() is < 1 or > 50) throw new ArgumentException("Provide 1 to 50 placements.");
        var pairs = new HashSet<(long Sheet, long View)>();
        var ids = new List<long>();
        foreach (var request in requests.EnumerateArray())
        {
            var sheetId = HostedReadId(request, "sheetId");
            var viewId = HostedReadId(request, "viewId");
            if (!pairs.Add((sheetId, viewId))) throw new ArgumentException("Duplicate sheet/view placement.");
            if (doc.GetElement(new ElementId(sheetId)) is not ViewSheet sheet || sheet.IsPlaceholder)
                throw new ArgumentException("sheetId must identify an existing non-placeholder sheet.");
            if (doc.GetElement(new ElementId(viewId)) is not View view || view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                throw new ArgumentException("viewId must identify an existing graphical view.");
            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                throw new InvalidOperationException($"View {viewId} cannot be placed on sheet {sheetId} (Viewport.CanAddViewToSheet=false).");
            var center = new XYZ(SheetUnits(BuildingNumber(request, "centerXmm", -100000, 100000)),
                SheetUnits(BuildingNumber(request, "centerYmm", -100000, 100000)), 0);
            var viewport = Viewport.Create(doc, sheet.Id, view.Id, center);
            SetSheetViewportType(doc, viewport, request);
            doc.Regenerate();
            viewport.SetBoxCenter(center);
            doc.Regenerate();
            ids.Add(viewport.Id.Value);
        }
        return new { count = ids.Count, viewportIds = ids };
    }

    private static JsonElement ReadPlacedViewsAfterCommit(Document doc, JsonElement args, JsonElement result) =>
        JsonSerializer.SerializeToElement(new {
            count = result.GetProperty("count").GetInt32(),
            placements = result.GetProperty("viewportIds").EnumerateArray()
                .Select(id => SheetViewportRecord(doc, (Viewport)doc.GetElement(new ElementId(id.GetInt64())))).ToArray()
        }, JsonTools.SerializerOptions);
}
