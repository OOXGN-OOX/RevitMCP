using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static object TagModelElements(Document doc, JsonElement args)
    {
        var view=GraphicsRequireView(doc,args);
        var typeId=new ElementId(HostedReadId(args,"tagTypeId"));
        if(doc.GetElement(typeId) is not FamilySymbol symbol || symbol.Category?.CategoryType!=CategoryType.Annotation)
            throw new ArgumentException("tagTypeId must identify a loaded annotation family type.");
        if(!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        var tagged=new FilteredElementCollector(doc,view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
            .SelectMany(t=>t.GetTaggedLocalElementIds()).ToHashSet();
        var created=new List<object>(); var skipped=new List<long>();
        foreach(var id in GraphicsReadIds(args,false))
        {
            if(tagged.Contains(id)) { skipped.Add(id.Value); continue; }
            var element=GraphicsRequireElement(doc,view,id);
            var box=element.get_BoundingBox(view) ?? throw new ArgumentException($"Element {id.Value} has no bounds on this view.");
            var point=(box.Min+box.Max)*0.5 + view.RightDirection*UnitUtils.ConvertToInternalUnits(args.GetProperty("offsetRightMeters").GetDouble(),UnitTypeId.Meters)
                +view.UpDirection*UnitUtils.ConvertToInternalUnits(args.GetProperty("offsetUpMeters").GetDouble(),UnitTypeId.Meters);
            var tag=IndependentTag.Create(doc,typeId,view.Id,new Reference(element),args.GetProperty("leader").GetBoolean(),TagOrientation.Horizontal,point);
            doc.Regenerate();
            if(string.IsNullOrWhiteSpace(tag.TagText)) throw new ArgumentException($"Tag for {id.Value} has no text.");
            created.Add(new { elementId=id.Value, tagId=tag.Id.Value, text=tag.TagText });
        }
        return new { created, skippedAlreadyTagged=skipped };
    }
    private static XYZ ModelPoint(JsonElement value) => new(
        UnitUtils.ConvertToInternalUnits(value.GetProperty("x").GetDouble(), UnitTypeId.Meters),
        UnitUtils.ConvertToInternalUnits(value.GetProperty("y").GetDouble(), UnitTypeId.Meters),
        UnitUtils.ConvertToInternalUnits(value.GetProperty("z").GetDouble(), UnitTypeId.Meters));

    private static List<ElementId> EditableModelIds(Document doc, JsonElement args)
    {
        var ids = GraphicsReadIds(args, false);
        foreach (var id in ids)
        {
            var e = doc.GetElement(id) ?? throw new ArgumentException($"Unknown element {id.Value}.");
            if (e is ElementType or View || e.Pinned || e.GroupId != ElementId.InvalidElementId)
                throw new ArgumentException($"Element {id.Value} is a type/view, pinned or a group member.");
        }
        return ids;
    }

    private static object MoveModelElements(Document doc, JsonElement args)
    {
        var ids = EditableModelIds(doc,args);
        ElementTransformUtils.MoveElements(doc, ids, ModelPoint(args.GetProperty("translation")));
        doc.Regenerate();
        return new { movedIds = ids.Select(i => i.Value).ToArray(), positions = ids.Select(i => new { id = i.Value, geometry = SummarizeLocation(doc.GetElement(i)) }).ToArray() };
    }

    private static object RotateModelElements(Document doc, JsonElement args)
    {
        var ids = EditableModelIds(doc,args);
        var start = ModelPoint(args.GetProperty("axisStart")); var end = ModelPoint(args.GetProperty("axisEnd"));
        using var axis = Line.CreateBound(start,end);
        var angle = args.GetProperty("angleDegrees").GetDouble();
        if (!double.IsFinite(angle) || Math.Abs(angle)>360) throw new ArgumentException("Invalid rotation angle.");
        ElementTransformUtils.RotateElements(doc,ids,axis,angle*Math.PI/180);
        doc.Regenerate();
        return new { rotatedIds = ids.Select(i => i.Value).ToArray(), angleDegrees = angle };
    }

    private static object ChangeModelTypes(Document doc, JsonElement args)
    {
        var ids = EditableModelIds(doc,args); var typeId = new ElementId(HostedReadId(args,"typeId"));
        if (doc.GetElement(typeId) is not ElementType) throw new ArgumentException("typeId must identify an element type.");
        var result = new List<object>();
        foreach (var id in ids)
        {
            var e = doc.GetElement(id);
            if (!e.IsValidType(typeId)) throw new ArgumentException($"Type is incompatible with element {id.Value}.");
            var oldType = e.GetTypeId().Value;
            var replacement = e.ChangeTypeId(typeId);
            var finalId = replacement == ElementId.InvalidElementId ? id : replacement;
            doc.Regenerate();
            if (doc.GetElement(finalId)?.GetTypeId() != typeId) throw new InvalidOperationException("Revit did not retain the requested type.");
            result.Add(new { oldElementId = id.Value, elementId = finalId.Value, oldTypeId = oldType, typeId = typeId.Value });
        }
        return new { elements = result };
    }

    private static ViewSchedule RequireSchedule(Document doc, JsonElement args) =>
        doc.GetElement(new ElementId(HostedReadId(args,"scheduleId"))) as ViewSchedule ?? throw new ArgumentException("scheduleId must identify a schedule.");

    private static object GetSchedulableFields(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        if (args.TryGetProperty("scheduleId",out _)) return ScheduleAvailableFields(doc,RequireSchedule(doc,args));
        RequireWritableProject(doc);
        return SimulateOperation(doc,args,"Доступные поля спецификации",(d,a) => {
            var schedule = ViewSchedule.CreateSchedule(d,new ElementId(a.GetProperty("categoryId").GetInt64()));
            return ScheduleAvailableFields(d,schedule);
        });
    }

    private static object ScheduleAvailableFields(Document doc, ViewSchedule schedule) =>
        new { categoryId = schedule.Definition.CategoryId.Value,
            fields = schedule.Definition.GetSchedulableFields().Select(f => new {
                parameterId = f.ParameterId.Value, fieldType = f.FieldType.ToString(), name = f.GetName(doc)
            }).ToArray() };

    private static object CreateModelSchedule(Document doc, JsonElement args)
    {
        var schedule = ViewSchedule.CreateSchedule(doc,new ElementId(args.GetProperty("categoryId").GetInt64()));
        schedule.Name = JsonTools.GetString(args,"name");
        var available = schedule.Definition.GetSchedulableFields();
        foreach (var requested in args.GetProperty("fields").EnumerateArray())
        {
            var pid = requested.GetProperty("parameterId").GetInt64();
            var kind = JsonTools.GetString(requested,"fieldType");
            var field = available.SingleOrDefault(f => f.ParameterId.Value == pid && f.FieldType.ToString() == kind)
                ?? throw new ArgumentException($"Field {pid}/{kind} is not schedulable.");
            var added = schedule.Definition.AddField(field);
            if (requested.TryGetProperty("heading",out var heading)) added.ColumnHeading = heading.GetString();
            added.IsHidden = requested.TryGetProperty("hidden",out var hidden) && hidden.GetBoolean();
        }
        doc.Regenerate();
        return new { scheduleId = schedule.Id.Value, definition = DocSchedule(doc,schedule) };
    }

    private static object ModifyModelSchedule(Document doc, JsonElement args)
    {
        var schedule = RequireSchedule(doc,args); var definition = schedule.Definition;
        var before = JsonSerializer.SerializeToElement(DocSchedule(doc,schedule));
        if (args.TryGetProperty("filters",out var filters))
        {
            definition.ClearFilters();
            foreach (var request in filters.EnumerateArray())
            {
                var fieldId = new ScheduleFieldId(request.GetProperty("fieldId").GetInt32());
                var field = definition.GetField(fieldId);
                var kind = Enum.Parse<ScheduleFilterType>(JsonTools.GetString(request,"operator"));
                ScheduleFilter filter;
                if (kind is ScheduleFilterType.HasValue or ScheduleFilterType.HasNoValue) filter = new ScheduleFilter(fieldId,kind);
                else
                {
                    var value = request.GetProperty("value");
                    filter = JsonTools.GetString(request,"valueType") switch {
                        "string" => new ScheduleFilter(fieldId,kind,value.GetString()),
                        "integer" => new ScheduleFilter(fieldId,kind,value.GetInt32()),
                        "elementId" => new ScheduleFilter(fieldId,kind,new ElementId(value.GetInt64())),
                        "double" => new ScheduleFilter(fieldId,kind,InputParameterNumber(field.GetSpecTypeId(),value.GetDouble(),request)),
                        _ => throw new ArgumentException("Specify valueType for the schedule filter.")
                    };
                }
                definition.AddFilter(filter);
            }
        }
        if (args.TryGetProperty("sortGroups",out var sorting))
        {
            definition.ClearSortGroupFields();
            foreach (var request in sorting.EnumerateArray())
            {
                var field = new ScheduleSortGroupField(new ScheduleFieldId(request.GetProperty("fieldId").GetInt32()),
                    Enum.Parse<ScheduleSortOrder>(JsonTools.GetString(request,"order","Ascending")));
                field.ShowHeader = request.TryGetProperty("showHeader",out var header) && header.GetBoolean();
                field.ShowFooter = request.TryGetProperty("showFooter",out var footer) && footer.GetBoolean();
                field.ShowBlankLine = request.TryGetProperty("showBlankLine",out var blank) && blank.GetBoolean();
                definition.AddSortGroupField(field);
            }
        }
        if (args.TryGetProperty("isItemized",out var itemized)) definition.IsItemized = itemized.GetBoolean();
        if (args.TryGetProperty("showGrandTotal",out var total)) definition.ShowGrandTotal = total.GetBoolean();
        doc.Regenerate();
        return new { scheduleId = schedule.Id.Value, before, after = DocSchedule(doc,schedule) };
    }

    private static object EditModelSheet(Document doc, JsonElement args)
    {
        var sheet = doc.GetElement(new ElementId(HostedReadId(args,"sheetId"))) as ViewSheet ?? throw new ArgumentException("sheetId must identify a sheet.");
        var before = new { name = sheet.Name, number = sheet.SheetNumber, collectionId = sheet.SheetCollectionId.Value };
        if (args.TryGetProperty("name",out var name)) sheet.Name = name.GetString();
        if (args.TryGetProperty("number",out var number)) sheet.SheetNumber = number.GetString();
        if (args.TryGetProperty("collectionId",out var collection)) sheet.SheetCollectionId = collection.ValueKind == JsonValueKind.Null ? ElementId.InvalidElementId : new ElementId(collection.GetInt64());
        return new { sheetId = sheet.Id.Value, before, after = new { name = sheet.Name, number = sheet.SheetNumber, collectionId = sheet.SheetCollectionId.Value } };
    }
}
