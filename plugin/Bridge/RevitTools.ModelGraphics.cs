using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static object GetGraphicsResources(UIApplication app)
    {
        var doc = RequireDocument(app);
        return new {
            fillPatterns = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().Select(p => new {
                id = p.Id.Value, p.Name, target = p.GetFillPattern().Target.ToString(), solid = p.GetFillPattern().IsSolidFill }).ToArray(),
            linePatterns = new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Select(p => new { id = p.Id.Value, p.Name }).ToArray(),
            solidLinePatternId = LinePatternElement.GetSolidPatternId().Value,
            categories = doc.Settings.Categories.Cast<Category>().Select(c => new { id = c.Id.Value, c.Name, kind = c.CategoryType.ToString() }).ToArray()
        };
    }

    private static Color ModelColor(JsonElement value) => new((byte)value.GetProperty("r").GetInt32(),(byte)value.GetProperty("g").GetInt32(),(byte)value.GetProperty("b").GetInt32());
    private static object OverrideModelGraphics(Document doc, JsonElement args)
    {
        var view = GraphicsRequireView(doc,args);
        var categoryMode = args.TryGetProperty("categoryIds",out var categories);
        var ids = categoryMode ? categories.EnumerateArray().Select(v => new ElementId(v.GetInt64())).ToList() : GraphicsReadIds(args,false);
        foreach (var id in ids)
        {
            if (categoryMode && Category.GetCategory(doc,id) is null) throw new ArgumentException("Unknown category.");
            if (!categoryMode) GraphicsRequireElement(doc,view,id);
            var settings = categoryMode ? view.GetCategoryOverrides(id) : view.GetElementOverrides(id);
            if (args.TryGetProperty("projectionLineColor",out var lineColor)) settings.SetProjectionLineColor(ModelColor(lineColor));
            if (args.TryGetProperty("projectionLineWeight",out var weight)) settings.SetProjectionLineWeight(weight.GetInt32());
            if (args.TryGetProperty("projectionLinePatternId",out var linePattern))
            {
                var pid = new ElementId(linePattern.GetInt64());
                if (pid != LinePatternElement.GetSolidPatternId() && doc.GetElement(pid) is not LinePatternElement) throw new ArgumentException("Invalid line pattern.");
                settings.SetProjectionLinePatternId(pid);
            }
            foreach (var key in new[]{"surfaceForegroundPatternId","cutForegroundPatternId"})
                if (args.TryGetProperty(key,out var pattern))
                {
                    var pid = new ElementId(pattern.GetInt64());
                    if (doc.GetElement(pid) is not FillPatternElement fill || fill.GetFillPattern().Target != FillPatternTarget.Drafting)
                        throw new ArgumentException("Overrides require a drafting fill pattern.");
                    if (key == "surfaceForegroundPatternId") settings.SetSurfaceForegroundPatternId(pid); else settings.SetCutForegroundPatternId(pid);
                }
            if (args.TryGetProperty("surfaceForegroundColor",out var surface)) settings.SetSurfaceForegroundPatternColor(ModelColor(surface));
            if (args.TryGetProperty("cutForegroundColor",out var cut)) settings.SetCutForegroundPatternColor(ModelColor(cut));
            if (args.TryGetProperty("transparency",out var transparency)) settings.SetSurfaceTransparency(transparency.GetInt32());
            if (args.TryGetProperty("detailLevel",out var detail)) settings.SetDetailLevel(Enum.Parse<ViewDetailLevel>(detail.GetString()!));
            if (args.TryGetProperty("halftone",out var halftone)) settings.SetHalftone(halftone.GetBoolean());
            if (categoryMode) view.SetCategoryOverrides(id,settings); else view.SetElementOverrides(id,settings);
        }
        return new { viewId = view.Id.Value, target = categoryMode ? "categories" : "elements", changedIds = ids.Select(i=>i.Value).ToArray(), settings = args };
    }

    private static Category[] AnnotationCategories(Document doc, View view) =>
        new FilteredElementCollector(doc).OwnedByView(view.Id).WhereElementIsNotElementType().ToElements()
            .Where(e=>e.OwnerViewId == view.Id && e.Category?.CategoryType == CategoryType.Annotation).Select(e=>e.Category!)
            .DistinctBy(c=>c.Id.Value).OrderBy(c=>c.Id.Value).ToArray();
    private static object GetAnnotationCategories(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app); var view = GraphicsRequireView(doc,args);
        return new { viewId = view.Id.Value, mode = "Revit2026TemporaryCategoryIsolation",
            temporaryModeActive = view.IsTemporaryHideIsolateActive(),
            categories = AnnotationCategories(doc,view).Select(c=>new { id=c.Id.Value,c.Name,
                hidden=view.GetCategoryHidden(c.Id),
                included = !view.IsTemporaryHideIsolateActive() || new FilteredElementCollector(doc).OwnedByView(view.Id).WhereElementIsNotElementType()
                    .Where(e=>e.OwnerViewId==view.Id && e.Category?.Id==c.Id)
                    .Any(e=>view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate,e.Id))
            }).ToArray() };
    }
    private static object IsolateModelAnnotations(Document doc, JsonElement args)
    {
        var view = GraphicsRequireView(doc,args);
        if (args.TryGetProperty("reset",out var reset) && reset.GetBoolean())
        {
            view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            return new { viewId=view.Id.Value, reset=true };
        }
        var ids = args.GetProperty("categoryIds").EnumerateArray().Select(v=>new ElementId(v.GetInt64())).ToList();
        foreach (var id in ids)
            if (Category.GetCategory(doc,id)?.CategoryType != CategoryType.Annotation) throw new ArgumentException("Only annotation categories can be isolated.");
        view.IsolateCategoriesTemporary(ids);
        return new { viewId=view.Id.Value, categoryIds=ids.Select(i=>i.Value).ToArray(), temporary=true };
    }
}
