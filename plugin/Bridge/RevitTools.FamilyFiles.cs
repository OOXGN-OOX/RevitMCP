using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Security.Cryptography;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private sealed record FamilyBuildPlan(JsonElement Args, DateTime Created, Dictionary<string, string> Hashes);
    private static readonly Dictionary<string, FamilyBuildPlan> FamilyBuildPlans = new();
    private sealed class FamilyBuildContext(Document document, string scratch)
    {
        public Document Document = document;
        public string Scratch = scratch;
        public Dictionary<string, ElementId> Elements = new(StringComparer.Ordinal);
        public List<object> Fields = [];
        public void Add(JsonElement op, Element element)
        {
            if (!Elements.TryAdd(FString(op, "key"), element.Id)) throw new ArgumentException("Duplicate operation key.");
        }
        public Element Get(JsonElement reference) => Document.GetElement(reference.TryGetProperty("id", out var id)
            ? new ElementId(id.GetInt64()) : Elements.TryGetValue(FString(reference, "key"), out var key) ? key
            : throw new ArgumentException("Unknown operation key.")) ?? throw new ArgumentException("Element does not exist.");
    }
    private static string FString(JsonElement args, string name, string fallback = "") => args.TryGetProperty(name, out var p) ? p.GetString() ?? fallback : fallback;
    private static bool FBool(JsonElement args, string name, bool fallback = false) => args.TryGetProperty(name, out var p) ? p.GetBoolean() : fallback;
    private static double FNumber(JsonElement args, string name, double fallback = 0) => args.TryGetProperty(name, out var p) ? p.GetDouble() : fallback;
    private static IEnumerable<JsonElement> FArray(JsonElement args, string name) => args.TryGetProperty(name, out var p) ? p.EnumerateArray().ToArray() : [];
    private static double FMeters(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    private static double FFeet(double meters) => UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
    private static XYZ FVector(JsonElement p) => new(FNumber(p, "x"), FNumber(p, "y"), FNumber(p, "z"));
    private static XYZ FPoint(JsonElement p) => FVector(p) * FFeet(1);
    private static object FCoordinates(XYZ p, bool meters = true) => new { x = p.X * (meters ? FMeters(1) : 1), y = p.Y * (meters ? FMeters(1) : 1), z = p.Z * (meters ? FMeters(1) : 1) };
    private static string FHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string FPath(JsonElement args, string key, string ext, bool exists = true)
    {
        var path = AuthoringFilePath(args, key, ext, exists);
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0 || name.EndsWith(' ') || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid family file name.");
        return path;
    }
    private static Document FOpen(UIApplication app, string path)
    {
        if (app.Application.Documents.Cast<Document>().Any(d => string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This file is already open in Revit. Close it before isolated file operations.");
        var doc = app.Application.OpenDocumentFile(path);
        if (doc.IsFamilyDocument) return doc;
        doc.Close(false);
        throw new ArgumentException("The file is not a family document.");
    }
    private static void FClose(Document? doc) { if (doc?.IsValidObject == true && !doc.Close(false)) throw new InvalidOperationException("Cannot close the isolated family document."); }
    private static object FamilyAuthoringOptions() => new
    {
        dataTypes = SpecUtils.GetAllSpecs().Select(s => new { id = s.TypeId, label = LabelUtils.GetLabelForSpec(s), measurable = UnitUtils.IsMeasurableSpec(s),
            units = UnitUtils.IsMeasurableSpec(s) ? UnitUtils.GetValidUnits(s).Select(u => new { id = u.TypeId, label = LabelUtils.GetLabelForUnit(u), catalog = UnitUtils.GetTypeCatalogStringForUnit(u) }).ToArray() : [] }).ToArray(),
        groups = ParameterUtils.GetAllBuiltInGroups().Select(g => new { id = g.TypeId, label = LabelUtils.GetLabelForGroup(g) }).ToArray(),
        coordinates = "meters", axes = "unit vectors", numericValues = "unitId required for measurable parameters"
    };
    private static object ReadFamilyCatalog(JsonElement args)
    {
        var catalog = FamilyCatalog.Read(FPath(args, "path", ".txt"), FString(args, "encoding", "utf8"));
        var rows = args.TryGetProperty("rowName", out var name) ? [catalog.ExactRow(name.GetString()!)]
            : catalog.Rows.Skip((int)FNumber(args, "offset")).Take((int)FNumber(args, "limit", 50)).ToArray();
        return new { columns = catalog.Columns, total = catalog.Rows.Length, rows };
    }
    private static object ReadFamilyFile(UIApplication app, JsonElement args)
    {
        Document? doc = null;
        try { doc = FOpen(app, FPath(args, "path", ".rfa")); return FSnapshot(doc, FArray(args, "typeNames").Select(x => x.GetString()!).ToArray(), FBool(args, "mesh"), (int)FNumber(args, "maxTriangles", 1000)); }
        finally { FClose(doc); }
    }
    private static JsonElement FSnapshot(Document doc, string[]? names = null, bool mesh = false, int maxTriangles = 1000)
    {
        var fm = doc.FamilyManager;
        var parameters = fm.GetParameters().OrderBy(p => p.Id.Value).Select(p => new { id = p.Id.Value, name = p.Definition.Name,
            guid = p.IsShared ? p.GUID.ToString() : null, dataTypeId = p.Definition.GetDataType().TypeId,
            unitId = UnitUtils.IsMeasurableSpec(p.Definition.GetDataType()) ? p.GetUnitTypeId().TypeId : null,
            storageType = p.StorageType.ToString(), isInstance = p.IsInstance, isReadOnly = p.IsReadOnly,
            isDeterminedByFormula = p.IsDeterminedByFormula, formula = p.Formula, isReporting = p.IsReporting,
            associations = p.AssociatedParameters.Cast<Parameter>().OrderBy(a => a.Element.Id.Value).ThenBy(a => a.Id.Value).Select(a => new { elementId = a.Element.Id.Value, parameterId = a.Id.Value, name = a.Definition.Name }).ToArray() }).ToArray();
        var types = fm.Types.Cast<FamilyType>().ToArray();
        if (names?.Any(n => types.All(t => t.Name != n)) == true) throw new ArgumentException("Unknown family type in typeNames.");
        var snapshots = new List<object>();
        using var tx = new Transaction(doc, "Inspect family types");
        BeginCheckedTransaction(tx);
        foreach (var type in types.Where(t => names is null || names.Length == 0 || names.Contains(t.Name)))
        {
            fm.CurrentType = type; doc.Regenerate();
            snapshots.Add(new { name = type.Name, values = fm.GetParameters().OrderBy(p => p.Id.Value).Select(p => new { name = p.Definition.Name, guid = p.IsShared ? p.GUID.ToString() : null,
                hasValue = type.HasValue(p), value = FParameterValue(type, p), displayValue = type.HasValue(p) ? type.AsValueString(p) : null,
                unitId = UnitUtils.IsMeasurableSpec(p.Definition.GetDataType()) ? p.GetUnitTypeId().TypeId : null }).ToArray(),
                geometry = FGeometry(doc, mesh, maxTriangles), connectors = FConnectors(doc) });
        }
        tx.RollBack();
        return JsonSerializer.SerializeToElement(new { category = doc.OwnerFamily.FamilyCategory?.Name, categoryId = doc.OwnerFamily.FamilyCategory?.Id.Value,
            placement = doc.OwnerFamily.FamilyPlacementType.ToString(), shared = doc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1,
            parameters, types = snapshots, materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Select(m => new { id = m.Id.Value, name = m.Name }).ToArray(),
            subcategories = doc.OwnerFamily.FamilyCategory?.SubCategories.Cast<Category>().Select(c => new { id = c.Id.Value, name = c.Name, materialId = c.Material?.Id.Value }).ToArray(),
            views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).Select(v => new { id = v.Id.Value, name = v.Name, kind = v.ViewType.ToString() }).ToArray(),
            referencePlanes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().Select(p => new { id = p.Id.Value, name = p.Name, bubble = FCoordinates(p.BubbleEnd), free = FCoordinates(p.FreeEnd), normal = FCoordinates(p.Normal, false) }).ToArray(),
            nested = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Select(i => new { id = i.Id.Value, familyId = i.Symbol.Family.Id.Value, symbolId = i.Symbol.Id.Value, family = i.Symbol.FamilyName, type = i.Symbol.Name, frame = FFrameInfo(i.GetTransform()), shared = i.Symbol.Family.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1 }).ToArray() });
    }
    private static object? FParameterValue(FamilyType type, FamilyParameter p)
    {
        if (!type.HasValue(p)) return null;
        return p.StorageType switch { StorageType.Double => UnitUtils.IsMeasurableSpec(p.Definition.GetDataType())
            ? UnitUtils.ConvertFromInternalUnits(type.AsDouble(p)!.Value, p.GetUnitTypeId()) : type.AsDouble(p),
            StorageType.Integer => type.AsInteger(p), StorageType.String => type.AsString(p), StorageType.ElementId => type.AsElementId(p)?.Value, _ => null };
    }
    private static object PreviewBuildFamily(UIApplication app, JsonElement args, Func<bool>? cancelled)
    {
        foreach (var key in FamilyBuildPlans.Where(p => DateTime.UtcNow - p.Value.Created > PlanLifetime).Select(p => p.Key).ToArray()) FamilyBuildPlans.Remove(key);
        if (FamilyBuildPlans.Count >= 30) throw new InvalidOperationException("Too many pending family plans.");
        var hashes = FDependencies(args);
        var preview = FBuild(app, args, false, cancelled);
        FVerifyDependencies(hashes);
        var id = Guid.NewGuid().ToString("N");
        FamilyBuildPlans[id] = new(args.Clone(), DateTime.UtcNow, hashes);
        return new { planId = id, expiresInMinutes = 10, preview, outputCreated = false };
    }
    private static object ApplyBuildFamily(UIApplication app, JsonElement args, Func<bool>? cancelled)
    {
        if (!FamilyBuildPlans.TryGetValue(FString(args, "planId"), out var plan) || DateTime.UtcNow - plan.Created > PlanLifetime)
            throw new ArgumentException("Family plan expired; prepare a new preview.");
        FVerifyDependencies(plan.Hashes);
        using var dialog = new TaskDialog("Revit MCP") { MainInstruction = "Сохранить новое семейство?", MainContent = FString(plan.Args, "outputPath"), CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
        if (cancelled?.Invoke() == true || !ConfirmOperation(dialog) || cancelled?.Invoke() == true) return new { applied = false, cancelled = true };
        FVerifyDependencies(plan.Hashes);
        var result = FBuild(app, plan.Args, true, cancelled, () => FVerifyDependencies(plan.Hashes));
        FamilyBuildPlans.Remove(FString(args, "planId"));
        return new { applied = true, result };
    }
    private static Dictionary<string, string> FDependencies(JsonElement args)
    {
        var paths = new List<string>();
        foreach (var key in new[] { "sourcePath", "templatePath" }) if (args.TryGetProperty(key, out _)) paths.Add(FPath(args, key, key == "sourcePath" ? ".rfa" : ".rft"));
        foreach (var op in FArray(args, "operations")) if (FString(op, "action") is "importCatalogType" or "loadNestedFamily") paths.Add(FPath(op, "path", FString(op, "action") == "importCatalogType" ? ".txt" : ".rfa"));
        if (args.TryGetProperty("validation", out var validation) && validation.TryGetProperty("projectTemplatePath", out _)) paths.Add(FPath(validation, "projectTemplatePath", ".rte"));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, FHash, StringComparer.OrdinalIgnoreCase);
    }
    private static void FVerifyDependencies(Dictionary<string, string> hashes)
    {
        foreach (var (path, hash) in hashes) if (!File.Exists(path) || FHash(path) != hash) throw new InvalidOperationException("An input file changed after preview: " + path);
    }
    private static object FBuild(UIApplication app, JsonElement args, bool publish, Func<bool>? cancelled, Action? verifyDependencies = null)
    {
        var output = FPath(args, "outputPath", ".rfa", false);
        if (args.TryGetProperty("sourcePath", out _) == args.TryGetProperty("templatePath", out _)) throw new ArgumentException("Provide sourcePath XOR templatePath.");
        if (File.Exists(output) || Directory.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output))) throw new ArgumentException("Output must be a new file in an existing directory.");
        var input = args.TryGetProperty("sourcePath", out _) ? FPath(args, "sourcePath", ".rfa") : FPath(args, "templatePath", ".rft");
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must differ from source.");
        // Same-volume staging allows an atomic no-overwrite rename after verification.
        var scratch = Path.Combine(Path.GetDirectoryName(output)!, ".revitmcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Document? doc = null;
        try
        {
            doc = input.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? FOpen(app, input) : app.Application.NewFamilyDocument(input);
            var ctx = new FamilyBuildContext(doc, scratch);
            var operations = FArray(args, "operations").ToArray();
            if (operations.Length is < 1 or > 200) throw new ArgumentException("Expected 1 to 200 family operations.");
            using (var tx = new Transaction(doc, "Build family"))
            {
                BeginCheckedTransaction(tx);
                foreach (var op in operations)
                {
                    if (cancelled?.Invoke() == true) throw new OperationCanceledException();
                    FApplyOperation(ctx, op); doc.Regenerate();
                }
                CommitCheckedTransaction(tx, cancelled);
            }
            if (doc.FamilyManager.Types.Size == 0) throw new ArgumentException("Create at least one family type before saving.");
            var before = FSnapshot(doc);
            var staged = Path.Combine(scratch, Path.GetFileName(output));
            doc.SaveAs(staged, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
            FClose(doc); doc = null;
            var validation = args.TryGetProperty("validation", out var v) ? v : JsonSerializer.SerializeToElement(new { });
            var verified = FValidate(app, staged, validation, before, cancelled);
            if (cancelled?.Invoke() == true) throw new OperationCanceledException();
            verifyDependencies?.Invoke();
            if (publish) File.Move(staged, output, overwrite: false);
            return new { outputPath = output, saved = publish, fields = ctx.Fields, verified };
        }
        finally
        {
            try { FClose(doc); }
            finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
        }
    }
}
