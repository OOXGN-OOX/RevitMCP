using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    // All paper-space records use absolute sheet coordinates, without subtracting Outline.Min.
    private static object SheetBounds(double minX, double minY, double maxX, double maxY) => new {
        minXmm = SheetMm(minX), minYmm = SheetMm(minY), maxXmm = SheetMm(maxX), maxYmm = SheetMm(maxY),
        widthMillimeters = SheetMm(maxX - minX), heightMillimeters = SheetMm(maxY - minY)
    };

    private static object SheetBounds(Outline outline) => SheetBounds(
        outline.MinimumPoint.X, outline.MinimumPoint.Y, outline.MaximumPoint.X, outline.MaximumPoint.Y);

    private static object? ViewportLabelBounds(Viewport viewport)
    {
        using var outline = viewport.GetLabelOutline();
        return outline.IsEmpty ? null : SheetBounds(outline);
    }

    private static object SheetViewportRecord(Document doc, Viewport viewport)
    {
        var view = (View)doc.GetElement(viewport.ViewId);
        using var box = viewport.GetBoxOutline();
        var center = viewport.GetBoxCenter();
        var offset = viewport.LabelOffset;
        return new {
            id = viewport.Id.Value, viewportId = viewport.Id.Value, sheetId = viewport.SheetId.Value,
            viewId = view.Id.Value, viewName = view.Name, viewportTypeId = viewport.GetTypeId().Value, scale = view.Scale,
            centerXmm = SheetMm(center.X), centerYmm = SheetMm(center.Y),
            minXmm = SheetMm(box.MinimumPoint.X), minYmm = SheetMm(box.MinimumPoint.Y),
            maxXmm = SheetMm(box.MaximumPoint.X), maxYmm = SheetMm(box.MaximumPoint.Y),
            boundsMm = SheetBounds(box), labelBoundsMm = ViewportLabelBounds(viewport),
            titleOnSheet = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString(),
            labelOffsetXmm = SheetMm(offset.X), labelOffsetYmm = SheetMm(offset.Y),
            labelLineLengthMm = SheetMm(viewport.LabelLineLength), rotation = viewport.Rotation.ToString()
        };
    }

    private static object ListSheetResources(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var allSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder).OrderBy(s => s.Id.Value).ToArray();
        var sheets = allSheets;
        if (args.TryGetProperty("sheetId", out _))
        {
            var sheetId = HostedReadId(args, "sheetId");
            sheets = allSheets.Where(s => s.Id.Value == sheetId).ToArray();
            if (sheets.Length != 1) throw new ArgumentException("sheetId must identify an existing non-placeholder sheet.");
        }
        var placedViews = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
            .Select(v => v.ViewId.Value).ToHashSet();
        var blocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().ToLookup(b => b.OwnerViewId.Value);
        var unplaced = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v is not ViewSheet && v is not ViewSchedule && !placedViews.Contains(v.Id.Value))
            .OrderBy(v => v.Id.Value).Select(v => {
                var errors = new List<string>();
                object? paperBounds = null;
                int? scale = null;
                try { scale = v.Scale; }
                catch (Exception e) { errors.Add("scale: " + e.Message); }
                try
                {
                    // View.Outline is already in paper space and includes the displayed annotation extents.
                    // CropBox / Scale would omit annotations outside the model crop.
                    var outline = v.Outline;
                    paperBounds = SheetBounds(outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V);
                }
                catch (Exception e) { errors.Add("paperBoundsMm: " + e.Message); }
                var allowed = new List<long>();
                foreach (var sheet in sheets)
                    try { if (Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id)) allowed.Add(sheet.Id.Value); }
                    catch (Exception e) { errors.Add($"CanAddViewToSheet({sheet.Id.Value}): {e.Message}"); }
                return new { id = v.Id.Value, name = v.Name, kind = v.ViewType.ToString(), scale,
                    canAddToSheet = sheets.Length == 0 ? (bool?)null : allowed.Count > 0, availableSheetIds = allowed,
                    paperBoundsMm = paperBounds, boundsSource = "View.Outline", errors };
            }).ToArray();
        return new {
            coordinateSystem = "absolute_sheet_mm",
            titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType()
                .OfType<FamilySymbol>().Select(t => new { id = t.Id.Value, family = t.FamilyName, name = t.Name,
                    parameters = t.Parameters.Cast<Parameter>().Select(p => new { name = p.Definition.Name,
                        value = p.AsValueString() ?? p.AsString(), storageType = p.StorageType.ToString(), readOnly = p.IsReadOnly }).ToArray() }).ToArray(),
            viewportTypes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Viewports).WhereElementIsElementType()
                .OfType<ElementType>().OrderBy(t => t.Id.Value).Select(t => {
                    var titleId = t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_LABEL_TAG)?.AsElementId();
                    var title = titleId is null ? null : doc.GetElement(titleId) as FamilySymbol;
                    return new { id = t.Id.Value, name = t.Name,
                        showTitle = t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.AsInteger(),
                        showTitleDisplay = t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.AsValueString(),
                        showExtensionLine = t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_EXTENSION_LINE)?.AsInteger() == 1,
                        titleFamilyTypeId = title?.Id.Value, titleFamily = title?.FamilyName, titleType = title?.Name };
                }).ToArray(),
            sheets = sheets.Select(s => {
                var outline = s.Outline;
                var titleBlocks = blocks[s.Id.Value].OrderBy(b => b.Id.Value).ToArray();
                return new { id = s.Id.Value, number = s.SheetNumber, name = s.Name,
                    minXmm = SheetMm(outline.Min.U), minYmm = SheetMm(outline.Min.V),
                    maxXmm = SheetMm(outline.Max.U), maxYmm = SheetMm(outline.Max.V),
                    widthMillimeters = SheetMm(outline.Max.U - outline.Min.U), heightMillimeters = SheetMm(outline.Max.V - outline.Min.V),
                    titleBlockId = titleBlocks.FirstOrDefault()?.Id.Value, titleBlockIds = titleBlocks.Select(b => b.Id.Value).ToArray(),
                    viewports = s.GetAllViewports().OrderBy(id => id.Value).Select(id => SheetViewportRecord(doc, (Viewport)doc.GetElement(id))).ToArray() };
            }).ToArray(),
            unplacedViews = unplaced
        };
    }
}
