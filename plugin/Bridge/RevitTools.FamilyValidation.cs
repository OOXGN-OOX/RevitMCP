using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static object ValidateFamilyFile(UIApplication app, JsonElement args, Func<bool>? cancelled) => FValidate(app, FPath(args, "path", ".rfa"), args, null, cancelled);
    private static object FValidate(UIApplication app, string path, JsonElement validation, JsonElement? before, Func<bool>? cancelled)
    {
        Document? doc = null; JsonElement snapshot;
        try
        {
            doc = FOpen(app, path);
            snapshot = FSnapshot(doc);
            if (before.HasValue) FCompareSaved(before.Value, snapshot);
            using var tx = new Transaction(doc, "Validate family values"); BeginCheckedTransaction(tx);
            foreach (var expected in FArray(validation, "expected"))
            {
                if (cancelled?.Invoke() == true) throw new OperationCanceledException();
                var typeName = FString(expected, "typeName");
                doc.FamilyManager.CurrentType = FType(doc, typeName); doc.Regenerate();
                foreach (var value in FArray(expected, "values")) FCheckValue(doc, FParameter(doc, value.GetProperty("parameter")), value);
                var type = snapshot.GetProperty("types").EnumerateArray().Single(t => FString(t, "name") == typeName);
                if (expected.TryGetProperty("connectorCount", out var count) && type.GetProperty("connectors").EnumerateArray().Count(c => FString(c, "domain") == "DomainHvac") != count.GetInt32()) throw new InvalidOperationException("HVAC connector count mismatch for " + typeName);
                if (expected.TryGetProperty("sizeMeters", out var size))
                {
                    var bounds = type.GetProperty("geometry").GetProperty("bounds");
                    if (bounds.ValueKind == JsonValueKind.Null || (FVector(bounds.GetProperty("size")) - FVector(size)).GetLength() > FNumber(expected, "toleranceMeters", .0001)) throw new InvalidOperationException("Geometry dimensions mismatch for " + typeName);
                }
            }
            tx.RollBack();
        }
        finally { FClose(doc); }
        var connections = FBool(validation, "testConnections") ? FTestConnections(app, path, validation, snapshot, cancelled) : null;
        return new { passed = true, reopened = true, roundTripCompared = before.HasValue, expectedChecks = FArray(validation, "expected").Count(), connectionsTested = connections is not null, connections, family = snapshot };
    }
    private static void FCompareSaved(JsonElement before, JsonElement after)
    {
        // Element ids are stable across SaveAs; tolerate numeric round-off in regenerated geometry.
        foreach (var key in new[] { "category", "categoryId", "placement", "shared" })
            if (!FJsonEquivalent(before.GetProperty(key), after.GetProperty(key))) throw new InvalidOperationException("Family metadata changed after saving: " + key);
        if (before.GetProperty("parameters").GetRawText() != after.GetProperty("parameters").GetRawText()) throw new InvalidOperationException("Parameter definitions changed after saving/reopening.");
        var types = after.GetProperty("types").EnumerateArray().ToDictionary(t => FString(t, "name"));
        if (types.Count != before.GetProperty("types").GetArrayLength()) throw new InvalidOperationException("Type count changed after saving.");
        foreach (var original in before.GetProperty("types").EnumerateArray())
        {
            if (!types.TryGetValue(FString(original, "name"), out var actual)) throw new InvalidOperationException("Type disappeared after saving.");
            if (!FJsonEquivalent(original.GetProperty("values"), actual.GetProperty("values")) || !FJsonEquivalent(original.GetProperty("connectors"), actual.GetProperty("connectors"))
                || !FJsonEquivalent(original.GetProperty("geometry"), actual.GetProperty("geometry"))) throw new InvalidOperationException("Values, geometry or connectors changed after saving type " + FString(original, "name"));
        }
    }
    private static bool FJsonEquivalent(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        if (a.ValueKind == JsonValueKind.Number) return Math.Abs(a.GetDouble() - b.GetDouble()) <= 1e-8 * Math.Max(1, Math.Abs(a.GetDouble()));
        if (a.ValueKind == JsonValueKind.Array) return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => FJsonEquivalent(p.First, p.Second));
        if (a.ValueKind == JsonValueKind.Object) return a.EnumerateObject().Count() == b.EnumerateObject().Count() && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var v) && FJsonEquivalent(p.Value, v));
        return a.GetRawText() == b.GetRawText();
    }
    private static object FTestConnections(UIApplication app, string path, JsonElement validation, JsonElement familySnapshot, Func<bool>? cancelled)
    {
        Document? project = null;
        try
        {
            project = validation.TryGetProperty("projectTemplatePath", out _) ? app.Application.NewProjectDocument(FPath(validation, "projectTemplatePath", ".rte")) : app.Application.NewProjectDocument(UnitSystem.Metric);
            using var tx = new Transaction(project, "Validate family connectors"); BeginCheckedTransaction(tx);
            if (!project.LoadFamily(path, new FamilyLoadPolicy("rejectExisting", "project"), out var family) || family is null) throw new InvalidOperationException("Cannot load family into the isolated project.");
            if (family.GetFamilySymbolIds().Count == 0) throw new InvalidOperationException("Connection test requires at least one family type.");
            var level = new FilteredElementCollector(project).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault() ?? Level.Create(project, 0);
            var results = new List<object>();
            foreach (var symbolId in family.GetFamilySymbolIds())
            {
                if (cancelled?.Invoke() == true) throw new OperationCanceledException();
                var symbol = (FamilySymbol)project.GetElement(symbolId); symbol.Activate(); project.Regenerate();
                var sourceType = familySnapshot.GetProperty("types").EnumerateArray().Single(t => FString(t, "name") == symbol.Name);
                var placement = family.FamilyPlacementType;
                FamilyInstance instance;
                if (placement is FamilyPlacementType.OneLevelBased or FamilyPlacementType.TwoLevelsBased)
                    instance = project.Create.NewFamilyInstance(XYZ.Zero, symbol, level, StructuralType.NonStructural);
                else if (placement == FamilyPlacementType.WorkPlaneBased)
                {
                    var view = new FilteredElementCollector(project).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate);
                    if (view is null) throw new ArgumentException("Work-plane based connection test needs a projectTemplatePath with a plan view.");
                    var plane = project.Create.NewReferencePlane(XYZ.Zero, XYZ.BasisX * 10, XYZ.BasisY, view);
                    instance = project.Create.NewFamilyInstance(plane.GetReference(), XYZ.Zero, XYZ.BasisX, symbol);
                }
                else throw new ArgumentException("Connection test currently supports level- and work-plane-based families. Hosted/curve/adaptive families need a dedicated test fixture: " + placement);
                project.Regenerate();
                var manager = MepConnectors(instance) ?? throw new InvalidOperationException("Loaded family has no MEP connector manager.");
                var connectors = manager.Connectors.Cast<Connector>().Where(c => c.Domain == Domain.DomainHvac && c.ConnectorType == ConnectorType.End).ToArray();
                var sources = sourceType.GetProperty("connectors").EnumerateArray().Where(c => FString(c, "domain") == "DomainHvac").ToArray();
                if (sources.Length == 0) throw new InvalidOperationException("Connection test requires at least one HVAC connector in type " + symbol.Name);
                if (connectors.Length != sources.Length) throw new InvalidOperationException("Loaded connector count differs for " + symbol.Name);
                var unmatched = connectors.ToList();
                foreach (var source in sources)
                {
                    if (cancelled?.Invoke() == true) throw new OperationCanceledException();
                    var sourceFrame = source.GetProperty("frame"); var transform = instance.GetTransform();
                    var origin = transform.OfPoint(FPoint(sourceFrame.GetProperty("origin")));
                    var normal = transform.OfVector(FVector(sourceFrame.GetProperty("basisZ")));
                    var xAxis = transform.OfVector(FVector(sourceFrame.GetProperty("basisX")));
                    var matches = unmatched.Where(c => c.Origin.DistanceTo(origin) < FFeet(.0001) && c.CoordinateSystem.BasisZ.DotProduct(normal) > 1 - 1e-6 && c.CoordinateSystem.BasisX.DotProduct(xAxis) > 1 - 1e-6).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("Loaded connector coordinate system is missing or ambiguous.");
                    var connector = matches[0]; unmatched.Remove(connector);
                    var sizeMatches = connector.Shape.ToString() == FString(source, "shape") && (connector.Shape == ConnectorProfileType.Round
                        ? Math.Abs(FMeters(connector.Radius * 2) - FNumber(source, "diameterMeters")) < .0001
                        : Math.Abs(FMeters(connector.Width) - FNumber(source, "widthMeters")) < .0001 && Math.Abs(FMeters(connector.Height) - FNumber(source, "heightMeters")) < .0001);
                    if (!sizeMatches) throw new InvalidOperationException("Loaded connector shape or dimensions differ from the family.");
                    var type = new FilteredElementCollector(project).OfClass(typeof(DuctType)).Cast<DuctType>().FirstOrDefault(t => t.Shape == connector.Shape)
                        ?? throw new ArgumentException("Temporary project has no matching duct shape. Provide projectTemplatePath.");
                    var classification = connector.DuctSystemType switch { DuctSystemType.SupplyAir => MEPSystemClassification.SupplyAir, DuctSystemType.ReturnAir => MEPSystemClassification.ReturnAir, DuctSystemType.ExhaustAir => MEPSystemClassification.ExhaustAir, _ => MEPSystemClassification.OtherAir };
                    var system = new FilteredElementCollector(project).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().FirstOrDefault(t => t.SystemClassification == classification)
                        ?? MechanicalSystemType.Create(project, classification, "MCP " + classification);
                    var duct = Duct.Create(project, system.Id, type.Id, level.Id, origin, origin + normal * FFeet(1));
                    if (connector.Shape == ConnectorProfileType.Round) duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM).Set(connector.Radius * 2);
                    else { duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM).Set(connector.Width); duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM).Set(connector.Height); }
                    project.Regenerate();
                    var end = duct.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(origin)).First();
                    var angle = Math.Atan2(normal.DotProduct(end.CoordinateSystem.BasisX.CrossProduct(xAxis)), end.CoordinateSystem.BasisX.DotProduct(xAxis));
                    if (Math.Abs(angle) > 1e-8) ElementTransformUtils.RotateElement(project, duct.Id, Line.CreateUnbound(origin, normal), angle);
                    project.Regenerate(); end = duct.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(origin)).First();
                    if (end.Origin.DistanceTo(origin) > FFeet(.0001) || end.CoordinateSystem.BasisZ.DotProduct(normal) > -1 + 1e-6) throw new InvalidOperationException("Test duct connector is misaligned.");
                    if (connector.Shape != ConnectorProfileType.Round && end.CoordinateSystem.BasisX.DotProduct(xAxis) < 1 - 1e-6) throw new InvalidOperationException("Test duct cross section is rotated incorrectly.");
                    connector.ConnectTo(end); project.Regenerate();
                    if (!connector.IsConnectedTo(end)) throw new InvalidOperationException("ConnectTo did not establish a physical connection.");
                    if (connector.Origin.DistanceTo(origin) > FFeet(.0001) || connector.CoordinateSystem.BasisZ.DotProduct(normal) < 1 - 1e-6)
                        throw new InvalidOperationException("ConnectTo changed the family connector position or orientation.");
                    results.Add(new { type = symbol.Name, familyConnectorId = source.GetProperty("id").GetInt64(), connected = true, frame = FFrameInfo(connector.CoordinateSystem) });
                    connector.DisconnectFrom(end); project.Delete(duct.Id);
                }
                project.Delete(instance.Id);
            }
            CommitCheckedTransaction(tx, cancelled);
            return new { passed = true, checkedConnectors = results.Count, results };
        }
        finally { FClose(project); }
    }
}
