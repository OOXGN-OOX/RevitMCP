using Autodesk.Revit.DB;
using System.Globalization;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static FamilyParameter FParameter(Document doc, JsonElement reference)
    {
        var matches = doc.FamilyManager.GetParameters().Where(p => reference.TryGetProperty("guid", out var guid)
            ? p.IsShared && p.GUID == Guid.Parse(guid.GetString()!) : p.Definition.Name == FString(reference, "name")).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException("Family parameter is missing or ambiguous: " + reference);
    }
    private static FamilyType FType(Document doc, string name) => doc.FamilyManager.Types.Cast<FamilyType>().SingleOrDefault(t => t.Name == name)
        ?? throw new ArgumentException("Unknown family type: " + name);
    private static Material FMaterial(Document doc, string name) => new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().SingleOrDefault(m => m.Name == name)
        ?? throw new ArgumentException("Material not found: " + name);
    private static void FSetValue(Document doc, FamilyParameter parameter, JsonElement value)
    {
        if (parameter.IsReadOnly || parameter.IsDeterminedByFormula || parameter.IsReporting) throw new ArgumentException("Parameter is read-only, reporting or formula-driven: " + parameter.Definition.Name);
        var fm = doc.FamilyManager;
        if (fm.CurrentType is null) throw new ArgumentException("Create a family type before assigning values.");
        switch (parameter.StorageType)
        {
            case StorageType.String: fm.Set(parameter, value.GetProperty("value").GetString()); break;
            case StorageType.Integer:
                var raw = value.GetProperty("value");
                fm.Set(parameter, raw.ValueKind is JsonValueKind.True or JsonValueKind.False ? (raw.GetBoolean() ? 1 : 0) : raw.GetInt32()); break;
            case StorageType.Double: fm.Set(parameter, FNumericValue(parameter, value)); break;
            case StorageType.ElementId:
                var id = value.TryGetProperty("materialName", out var material) ? FMaterial(doc, material.GetString()!).Id : new ElementId(value.GetProperty("elementId").GetInt64());
                if (doc.GetElement(id) is null) throw new ArgumentException("Parameter elementId does not exist.");
                if (parameter.Definition.GetDataType() == SpecTypeId.Reference.Material && doc.GetElement(id) is not Material) throw new ArgumentException("Material parameter requires a material.");
                fm.Set(parameter, id); break;
            default: throw new ArgumentException("Unsupported parameter storage type.");
        }
        doc.Regenerate();
        FCheckValue(doc, parameter, value);
    }
    private static double FNumericValue(FamilyParameter parameter, JsonElement value)
    {
        var number = value.GetProperty("value").GetDouble();
        if (!double.IsFinite(number)) throw new ArgumentException("Parameter value must be finite.");
        var spec = parameter.Definition.GetDataType();
        if (!UnitUtils.IsMeasurableSpec(spec)) return number;
        var unit = new ForgeTypeId(FString(value, "unitId"));
        if (!UnitUtils.IsValidUnit(spec, unit)) throw new ArgumentException("Supply a valid unitId for " + parameter.Definition.Name);
        return UnitUtils.ConvertToInternalUnits(number, unit);
    }
    private static void FCheckValue(Document doc, FamilyParameter p, JsonElement v)
    {
        var t = doc.FamilyManager.CurrentType;
        var valid = t.HasValue(p) && p.StorageType switch
        {
            StorageType.String => t.AsString(p) == v.GetProperty("value").GetString(),
            StorageType.Integer => t.AsInteger(p) == (v.GetProperty("value").ValueKind is JsonValueKind.True or JsonValueKind.False ? (v.GetProperty("value").GetBoolean() ? 1 : 0) : v.GetProperty("value").GetInt32()),
            StorageType.Double => Math.Abs(t.AsDouble(p)!.Value - FNumericValue(p, v)) <= Math.Max(1e-8, Math.Abs(FNumericValue(p, v)) * 1e-8),
            StorageType.ElementId => t.AsElementId(p) == (v.TryGetProperty("materialName", out var m) ? FMaterial(doc, m.GetString()!).Id : new ElementId(v.GetProperty("elementId").GetInt64())),
            _ => false
        };
        if (!valid) throw new InvalidOperationException($"Value verification failed for {t.Name}/{p.Definition.Name}.");
    }
    private static void FValues(Document doc, JsonElement op)
    {
        foreach (var value in FArray(op, "values")) FSetValue(doc, FParameter(doc, value.GetProperty("parameter")), value);
    }
    private static void FAddParameter(FamilyBuildContext ctx, JsonElement op)
    {
        var doc = ctx.Document;
        var spec = new ForgeTypeId(FString(op, "dataTypeId"));
        var group = new ForgeTypeId(FString(op, "groupId"));
        FamilyParameter p;
        if (op.TryGetProperty("guid", out var guid))
        {
            var app = doc.Application;
            var previous = app.SharedParametersFilename;
            var path = Path.Combine(ctx.Scratch, "shared-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(path, "# This is a Revit shared parameter file.\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n");
                app.SharedParametersFilename = path;
                var definitions = app.OpenSharedParameterFile() ?? throw new InvalidOperationException("Cannot open temporary shared parameter definitions.");
                using var options = new ExternalDefinitionCreationOptions(FString(op, "name"), spec) { GUID = Guid.Parse(guid.GetString()!), Description = FString(op, "description") };
                var definition = (ExternalDefinition)definitions.Groups.Create("Family").Definitions.Create(options);
                p = doc.FamilyManager.AddParameter(definition, group, FBool(op, "isInstance"));
            }
            finally { app.SharedParametersFilename = previous; }
        }
        else p = doc.FamilyManager.AddParameter(FString(op, "name"), group, spec, FBool(op, "isInstance"));
        if (op.TryGetProperty("description", out var description)) doc.FamilyManager.SetDescription(p, description.GetString());
    }
    private static void FImportCatalog(FamilyBuildContext ctx, JsonElement op)
    {
        var catalog = FamilyCatalog.Read(FPath(op, "path", ".txt"), FString(op, "encoding", "utf8"));
        var row = catalog.ExactRow(FString(op, "rowName"));
        var doc = ctx.Document; var fm = doc.FamilyManager;
        var assignments = new List<(FamilyParameter Parameter, JsonElement Value)>();
        fm.NewType(FString(op, "newTypeName", row.Name));
        for (var i = 0; i < catalog.Columns.Length; i++)
        {
            var column = catalog.Columns[i]; var raw = row.Values[i];
            var matches = fm.GetParameters().Where(p => p.Definition.Name == column.Name).ToArray();
            var p = matches.Length == 1 ? matches[0] : null;
            string? skip = p is null ? "missing_or_ambiguous_parameter" : p.IsDeterminedByFormula ? "formula" : p.IsReadOnly || p.IsReporting ? "read_only" : raw.Length == 0 ? "empty_field" : null;
            if (skip is not null) { ctx.Fields.Add(new { type = row.Name, parameter = column.Name, status = "skipped", reason = skip, requested = raw, formula = p?.Formula }); continue; }
            var value = new Dictionary<string, object?> { ["parameter"] = new { name = column.Name } };
            if (p!.StorageType == StorageType.Double)
            {
                var spec = p.Definition.GetDataType();
                var catalogType = UnitUtils.IsMeasurableSpec(spec) ? UnitUtils.GetTypeCatalogStringForSpec(spec) : "OTHER";
                if (!string.Equals(catalogType, column.DataType, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Catalog data type mismatch: " + column.Name);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) || !double.IsFinite(numeric)) throw new ArgumentException("Invalid catalog number: " + column.Name);
                value["value"] = numeric;
                if (UnitUtils.IsMeasurableSpec(spec))
                {
                    var units = UnitUtils.GetValidUnits(spec).Where(u => string.Equals(UnitUtils.GetTypeCatalogStringForUnit(u), column.Units, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (units.Length != 1) throw new ArgumentException("Unknown or ambiguous catalog units: " + column.Units);
                    value["unitId"] = units[0].TypeId;
                }
                else if (column.Units.Length != 0) throw new ArgumentException("Dimensionless catalog value must have empty units: " + column.Name);
            }
            else
            {
                if (column.DataType != "OTHER" || column.Units.Length != 0) throw new ArgumentException("Expected OTHER with empty units for " + column.Name);
                if (p.StorageType == StorageType.String) value["value"] = raw;
                else if (p.StorageType == StorageType.Integer && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) value["value"] = integer;
                else if (p.StorageType == StorageType.ElementId && p.Definition.GetDataType() == SpecTypeId.Reference.Material) value["materialName"] = raw;
                else throw new ArgumentException("Unsupported catalog field value: " + column.Name);
            }
            var assignment = JsonSerializer.SerializeToElement(value);
            FSetValue(doc, p, assignment);
            assignments.Add((p, assignment));
            ctx.Fields.Add(new { type = row.Name, parameter = column.Name, status = "applied", requested = raw, actual = FParameterValue(fm.CurrentType, p) });
        }
        doc.Regenerate();
        foreach (var assignment in assignments) FCheckValue(doc, assignment.Parameter, assignment.Value);
    }
    private static Parameter FElementParameter(Element element, JsonElement op)
    {
        if (op.TryGetProperty("builtInParameter", out var id))
            return element.get_Parameter(Enum.Parse<BuiltInParameter>(id.GetString()!, false)) ?? throw new ArgumentException("Element has no such built-in parameter.");
        var matches = element.GetParameters(FString(op, "parameterName"));
        return matches.Count == 1 ? matches[0] : throw new ArgumentException("Element parameter is missing or ambiguous.");
    }
    private static void FAssociate(Document doc, Element element, JsonElement op)
    {
        var p = FElementParameter(element, op);
        var target = op.GetProperty("parameter");
        doc.FamilyManager.AssociateElementParameterToFamilyParameter(p, target.ValueKind == JsonValueKind.Null ? null : FParameter(doc, target));
    }
    private static void FApplyOperation(FamilyBuildContext ctx, JsonElement op)
    {
        var doc = ctx.Document; var fm = doc.FamilyManager;
        switch (FString(op, "action"))
        {
            case "addParameter": FAddParameter(ctx, op); break;
            case "setFormula": fm.SetFormula(FParameter(doc, op.GetProperty("parameter")), op.GetProperty("formula").GetString()); break;
            case "addType": fm.NewType(FString(op, "name")); FValues(doc, op); break;
            case "setValues": fm.CurrentType = FType(doc, FString(op, "typeName")); FValues(doc, op); break;
            case "renameType": fm.CurrentType = FType(doc, FString(op, "typeName")); fm.RenameCurrentType(FString(op, "name")); break;
            case "removeParameter": fm.RemoveParameter(FParameter(doc, op.GetProperty("parameter"))); break;
            case "renameParameter": fm.RenameParameter(FParameter(doc, op.GetProperty("parameter")), FString(op, "name")); break;
            case "importCatalogType": FImportCatalog(ctx, op); break;
            case "associateParameter": FAssociate(doc, ctx.Get(op.GetProperty("element")), op); break;
            case "setFamilyShared": doc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED).Set(FBool(op, "shared") ? 1 : 0); break;
            case "deleteElement":
                var element = ctx.Get(op.GetProperty("element")); var deleted = doc.Delete(element.Id);
                if (!FBool(op, "allowDependentDeletion") && deleted.Count != 1) throw new ArgumentException("Deletion affects dependent elements; allowDependentDeletion is required.");
                ctx.Fields.Add(new { status = "deleted", ids = deleted.Select(i => i.Value).ToArray() }); break;
            default: FGeometryOperation(ctx, op); break;
        }
    }
}
