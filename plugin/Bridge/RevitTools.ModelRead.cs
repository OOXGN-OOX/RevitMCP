using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static object FullElementProperties(Document doc, Element element, JsonElement args)
    {
        var scope = JsonTools.GetString(args, "parameterScope", "both");
        var offset = JsonTools.GetInt(args, "parameterOffset", 0);
        var limit = JsonTools.GetInt(args, "parameterLimit", 100);
        if (offset < 0 || limit is < 1 or > 500 || scope is not ("instance" or "type" or "both")) throw new ArgumentException("Invalid parameter page or scope.");
        var parameters = new List<object>();
        var type = doc.GetElement(element.GetTypeId());
        void Add(Element owner, string kind)
        {
            foreach (var p in owner.Parameters.Cast<Parameter>().OrderBy(p => p.Id.Value))
            {
                var spec = p.Definition.GetDataType();
                var measurable = UnitUtils.IsMeasurableSpec(spec);
                parameters.Add(new {
                    id = p.Id.Value, name = p.Definition.Name, ownerId = owner.Id.Value, scope = kind,
                    guid = p.IsShared ? p.GUID.ToString() : null, dataType = spec.TypeId,
                    unitId = measurable ? p.GetUnitTypeId().TypeId : null,
                    storageType = p.StorageType.ToString(), hasValue = p.HasValue,
                    rawValue = RawParameterValue(p), value = DisplayParameterValue(p), readOnly = p.IsReadOnly
                });
            }
        }
        var include = !args.TryGetProperty("includeParameters", out var flag) || flag.GetBoolean();
        if (include)
        {
            if (scope is "instance" or "both") Add(element, element is ElementType ? "type" : "instance");
            if (scope is "type" or "both" && type is not null && type.Id != element.Id) Add(type, "type");
            if (scope == "type" && element is ElementType) Add(element, "type");
        }
        var result = JsonSerializer.Deserialize<Dictionary<string,object?>>(JsonSerializer.Serialize(Summarize(doc,element,false)))!;
        result["className"] = element.GetType().Name;
        result["boundingBox"] = DocBox(element.get_BoundingBox(null));
        result["geometry"] = SummarizeLocation(element);
        result["parameterCount"] = parameters.Count;
        result["parameterOffset"] = offset;
        result["parameterHasMore"] = offset + limit < parameters.Count;
        result["parameters"] = parameters.Skip(offset).Take(limit).ToArray();
        return result;
    }

    private static bool MatchParameter(Document doc, Element element, JsonElement filter)
    {
        var owner = JsonTools.GetString(filter, "scope", "instance") == "type" ? doc.GetElement(element.GetTypeId()) : element;
        if (owner is null) return false;
        Parameter? parameter;
        if (filter.TryGetProperty("parameterId", out var id))
            parameter = owner.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id.Value == id.GetInt64());
        else
        {
            var matches = owner.GetParameters(JsonTools.GetString(filter, "parameterName"));
            if (matches.Count > 1) throw new ArgumentException($"Ambiguous parameter name on element {owner.Id.Value}; use parameterId.");
            parameter = matches.FirstOrDefault();
        }
        var op = JsonTools.GetString(filter, "operator");
        if (op == "hasValue") return parameter?.HasValue == true;
        if (op == "hasNoValue") return parameter?.HasValue != true;
        if (parameter?.HasValue != true) return false;
        var value = filter.GetProperty("value");
        if (value.ValueKind == JsonValueKind.Number)
        {
            var expected = value.GetDouble();
            double actual;
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    actual = parameter.AsDouble();
                    expected = InputParameterNumber(parameter.Definition.GetDataType(), expected, filter);
                    break;
                case StorageType.Integer: actual = parameter.AsInteger(); break;
                case StorageType.ElementId: actual = parameter.AsElementId().Value; break;
                default: return false;
            }
            return op switch {
                "equals" => Math.Abs(actual-expected) <= 1e-9, "notEquals" => Math.Abs(actual-expected) > 1e-9,
                "greaterThan" => actual > expected, "greaterOrEqual" => actual >= expected,
                "lessThan" => actual < expected, "lessOrEqual" => actual <= expected,
                _ => throw new ArgumentException("Numeric comparisons require a numeric operator.")
            };
        }
        var text = value.GetString() ?? "";
        var current = parameter.StorageType == StorageType.String ? parameter.AsString() ?? "" : DisplayParameterValue(parameter);
        return op switch {
            "equals" => string.Equals(current,text,StringComparison.OrdinalIgnoreCase),
            "notEquals" => !string.Equals(current,text,StringComparison.OrdinalIgnoreCase),
            "contains" => current.Contains(text,StringComparison.OrdinalIgnoreCase),
            "startsWith" => current.StartsWith(text,StringComparison.OrdinalIgnoreCase),
            "endsWith" => current.EndsWith(text,StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentException("Ordered comparisons require a numeric value.")
        };
    }

    private static double InputParameterNumber(ForgeTypeId spec, double value, JsonElement args)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("A finite number is required.");
        if (!args.TryGetProperty("unitId", out var unit)) return value; // Explicitly documented internal units.
        var unitId = new ForgeTypeId(unit.GetString());
        if (!UnitUtils.IsMeasurableSpec(spec) || !UnitUtils.IsValidUnit(spec, unitId)) throw new ArgumentException("unitId is incompatible with the parameter data type.");
        return UnitUtils.ConvertToInternalUnits(value, unitId);
    }

    private static object ShowElements(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var ids = GraphicsReadIds(args, false);
        var view = app.ActiveUIDocument!.ActiveView;
        var visible = new FilteredElementCollector(doc, view.Id).ToElementIds().ToHashSet();
        foreach (var id in ids)
            if (!visible.Contains(id) || doc.GetElement(id).IsHidden(view)) throw new ArgumentException($"Element {id.Value} is not visible in the active view.");
        app.ActiveUIDocument.ShowElements(ids);
        return new { shownIds = ids.Select(id => id.Value).ToArray(), modelChanged = false };
    }

    private static object ListViews(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var kind = JsonTools.GetString(args, "kind", "all");
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => kind == "all" || (kind == "templates" ? v.IsTemplate : kind == "schedules" ? v is ViewSchedule : !v.IsTemplate && v is not ViewSchedule))
            .OrderBy(v => v.Id.Value).ToArray();
        return ModelPage(views.Select(v => {
            var errors = new List<string>();
            object? Read(string field, Func<object> getter) { try { return getter(); } catch(Exception e) { errors.Add(field+": "+e.Message); return null; } }
            return (object)new { id = v.Id.Value, name = v.Name, kind = v.ViewType.ToString(), v.IsTemplate,
                scale = Read("scale",()=>v.Scale), templateId = v.ViewTemplateId.Value, active = v.Id == app.ActiveUIDocument!.ActiveView.Id,
                detailLevel = Read("detailLevel",()=>v.DetailLevel.ToString()), displayStyle = Read("displayStyle",()=>v.DisplayStyle.ToString()),
                controlledParameterIds = v.IsTemplate ? v.GetTemplateParameterIds().Select(i=>i.Value).ToArray() : null, errors };
        }).ToArray(), args);
    }

    private static object ModelPage(object[] rows, JsonElement args)
    {
        var offset = JsonTools.GetInt(args, "offset", 0); var limit = JsonTools.GetInt(args, "limit", 100);
        if (offset < 0 || limit is < 1 or > 500) throw new ArgumentException("Invalid page.");
        return new { total = rows.Length, offset, hasMore = offset + limit < rows.Length, items = rows.Skip(offset).Take(limit).ToArray() };
    }

    private static object ListSheetCollections(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var collections = new FilteredElementCollector(doc).OfClass(typeof(SheetCollection)).ToElements();
        var filter = args.TryGetProperty("collectionId", out var value) ? value.GetInt64() : 0;
        if (filter > 0 && !collections.Any(c => c.Id.Value == filter)) throw new ArgumentException("Unknown sheet collection.");
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToArray();
        return ModelPage(collections.Where(c => filter == 0 || c.Id.Value == filter).OrderBy(c => c.Id.Value)
            .Select(c => (object)new { id = c.Id.Value, name = c.Name,
                sheets = sheets.Where(s => s.SheetCollectionId == c.Id).Select(s => new { id = s.Id.Value, name = s.Name, number = s.SheetNumber }).ToArray() }).ToArray(), args);
    }

    private static object GetRoomBoundaries(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        using var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = Enum.Parse<SpatialElementBoundaryLocation>(JsonTools.GetString(args,"location","Finish")) };
        return new { rooms = GraphicsReadIds(args,false).Select(id => {
            var room = doc.GetElement(id) as Room ?? throw new ArgumentException($"{id.Value} is not a room.");
            var loops = room.GetBoundarySegments(options);
            return new { id = id.Value, room.Name, room.Number, closed = room.Area > 0,
                loops = loops?.Select(loop => loop.Select(segment => {
                    using var curve = segment.GetCurve();
                    return new { elementId = segment.ElementId.Value, kind = curve.GetType().Name, points = curve.Tessellate().Select(p => DocPoint(p)).ToArray() };
                }).ToArray()).ToArray() };
        }).ToArray() };
    }
}
