using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static Transform FFrame(JsonElement frame)
    {
        var x = FVector(frame.GetProperty("basisX")).Normalize(); var z = FVector(frame.GetProperty("basisZ")).Normalize();
        if (x.IsZeroLength() || z.IsZeroLength() || Math.Abs(x.DotProduct(z)) > 1e-6) throw new ArgumentException("Frame axes must be nonzero and perpendicular.");
        var transform = Transform.Identity; transform.Origin = FPoint(frame.GetProperty("origin"));
        transform.BasisX = x; transform.BasisZ = z; transform.BasisY = z.CrossProduct(x); return transform;
    }
    private static object FFrameInfo(Transform t) => new { origin = FCoordinates(t.Origin), basisX = FCoordinates(t.BasisX, false), basisY = FCoordinates(t.BasisY, false), basisZ = FCoordinates(t.BasisZ, false) };
    private static object[] FConnectors(Document doc) => new FilteredElementCollector(doc).OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>().OrderBy(c => c.Id.Value).Select(c => (object)new
    {
        id = c.Id.Value, domain = c.Domain.ToString(), shape = c.Shape.ToString(), system = c.SystemClassification.ToString(),
        direction = FCoordinates(c.Direction, false), flowDirection = c.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM) is Parameter flow ? ((FlowDirectionType)flow.AsInteger()).ToString() : null,
        primary = c.IsPrimary, frame = FFrameInfo(c.CoordinateSystem), diameterMeters = c.Shape == ConnectorProfileType.Round ? (double?)FMeters(c.Radius * 2) : null,
        widthMeters = c.Shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval ? (double?)FMeters(c.Width) : null, heightMeters = c.Shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval ? (double?)FMeters(c.Height) : null,
        parameters = c.Parameters.Cast<Parameter>().OrderBy(p => p.Id.Value).Select(p => new { id = p.Id.Value, name = p.Definition.Name,
            familyParameter = doc.FamilyManager.GetAssociatedFamilyParameter(p)?.Definition.Name }).ToArray()
    }).ToArray();
    private static IEnumerable<Solid> FSolids(GeometryElement geometry)
    {
        foreach (var item in geometry)
        {
            if (item is Solid solid && solid.Faces.Size > 0) yield return solid;
            if (item is GeometryInstance instance)
                foreach (var nested in FSolids(instance.GetInstanceGeometry())) yield return nested;
        }
    }
    private static object FGeometry(Document doc, bool mesh, int triangleLimit)
    {
        var elements = new List<object>(); var remaining = triangleLimit; var truncated = false;
        XYZ? min = null, max = null;
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e => e is GenericForm or FamilyInstance or GeomCombination or ImportInstance or DirectShape).OrderBy(e => e.Id.Value))
        {
            using var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, DetailLevel = ViewDetailLevel.Fine };
            var geometry = e.get_Geometry(options);
            if (geometry is null) continue;
            var solids = FSolids(geometry).ToArray(); var faces = new List<object>(); var triangles = new List<object>();
            var contributesToBounds = e is not GenericForm { IsSolid: false } && (e is not CombinableElement combinable || combinable.Combinations.Size == 0);
            foreach (var solid in solids)
            foreach (Face face in solid.Faces)
            {
                if (face is PlanarFace planar) faces.Add(new { origin = FCoordinates(planar.Origin), normal = FCoordinates(planar.FaceNormal, false), areaSquareMeters = planar.Area * Math.Pow(FMeters(1), 2) });
                var tessellation = face.Triangulate();
                for (var i = 0; i < tessellation.NumTriangles; i++)
                {
                    var triangle = tessellation.get_Triangle(i); var points = Enumerable.Range(0, 3).Select(triangle.get_Vertex).ToArray();
                    if (contributesToBounds) foreach (var p in points) { min = min is null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z)); max = max is null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z)); }
                    if (mesh) { if (remaining-- > 0) triangles.Add(points.Select(p => FCoordinates(p)).ToArray()); else truncated = true; }
                }
            }
            elements.Add(new { id = e.Id.Value, kind = e.GetType().Name, name = e.Name, contributesToBounds, solidCount = solids.Length,
                volumeCubicMeters = solids.Sum(s => s.Volume) * Math.Pow(FMeters(1), 3), planarFaces = faces, triangles = mesh ? triangles : null });
        }
        return new { elements, bounds = min is null ? null : new { min = FCoordinates(min), max = FCoordinates(max!), size = FCoordinates(max! - min) }, meshTruncated = truncated };
    }
    private static PlanarFace FHostFace(FamilyBuildContext ctx, JsonElement selector)
    {
        var element = ctx.Get(selector.GetProperty("element"));
        using var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
        var geometry = element.get_Geometry(options) ?? throw new ArgumentException("Host has no geometry.");
        var normal = FVector(selector.GetProperty("normal")).Normalize();
        var faces = geometry.OfType<Solid>().SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>()
            .Where(f => f.Reference is not null && f.FaceNormal.DotProduct(normal) > 1 - 1e-6).ToArray();
        if (selector.TryGetProperty("origin", out var origin))
        {
            var point = FPoint(origin);
            faces = faces.Where(f => { var projection = f.Project(point); return projection is not null && projection.Distance < FFeet(0.0001) && f.IsInside(projection.UVPoint); }).ToArray();
        }
        return faces.Length == 1 ? faces[0] : throw new ArgumentException("Host face is missing or ambiguous; provide element, normal and a point on the face.");
    }
    private static void FVisibility(Element element, JsonElement value)
    {
        if (value.TryGetProperty("visible", out var visible))
        {
            var p = element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            if (p is null || p.IsReadOnly || !p.Set(visible.GetBoolean() ? 1 : 0)) throw new ArgumentException("Element has no writable visibility parameter.");
        }
        if (element is GenericForm form)
        {
            var flags = form.GetVisibility();
            if (value.TryGetProperty("coarse", out var coarse)) flags.IsShownInCoarse = coarse.GetBoolean();
            if (value.TryGetProperty("medium", out var medium)) flags.IsShownInMedium = medium.GetBoolean();
            if (value.TryGetProperty("fine", out var fine)) flags.IsShownInFine = fine.GetBoolean();
            if (value.TryGetProperty("plan", out var plan)) flags.IsShownInTopBottom = plan.GetBoolean();
            if (value.TryGetProperty("frontBack", out var front)) flags.IsShownInFrontBack = front.GetBoolean();
            if (value.TryGetProperty("leftRight", out var left)) flags.IsShownInLeftRight = left.GetBoolean();
            form.SetVisibility(flags);
        }
        else if (value.EnumerateObject().Any(p => p.Name != "visible")) throw new ArgumentException("Detail/direction visibility flags require a geometric form.");
    }
    private static void FHvacConnector(FamilyBuildContext ctx, JsonElement op, bool create)
    {
        var doc = ctx.Document; ConnectorElement connector;
        var shape = Enum.Parse<ConnectorProfileType>(FString(op, "shape"));
        var system = Enum.Parse<DuctSystemType>(FString(op, "system"));
        if (create)
        {
            connector = ConnectorElement.CreateDuctConnector(doc, system, shape, FHostFace(ctx, op.GetProperty("hostFace")).Reference);
            ctx.Add(op, connector);
        }
        else
        {
            connector = ctx.Get(op.GetProperty("element")) as ConnectorElement ?? throw new ArgumentException("Expected a connector element.");
            if (connector.Domain != Domain.DomainHvac) throw new ArgumentException("Expected an HVAC connector.");
            if (connector.Shape != shape) throw new ArgumentException("Changing connector shape requires deleteElement and createHvacConnector with explicit new bindings.");
            if (op.TryGetProperty("hostFace", out var face)) connector.ChangeHostReference(FHostFace(ctx, face).Reference);
            connector.SystemClassification = system switch { DuctSystemType.SupplyAir => MEPSystemClassification.SupplyAir, DuctSystemType.ReturnAir => MEPSystemClassification.ReturnAir, DuctSystemType.ExhaustAir => MEPSystemClassification.ExhaustAir, _ => MEPSystemClassification.OtherAir };
        }
        doc.Regenerate();
        var desired = FFrame(op.GetProperty("frame")); var current = connector.CoordinateSystem;
        if (current.BasisZ.DotProduct(desired.BasisZ) < -1 + 1e-6) { connector.FlipDirection(); doc.Regenerate(); current = connector.CoordinateSystem; }
        if (current.BasisZ.DotProduct(desired.BasisZ) < 1 - 1e-6) throw new ArgumentException("Connector normal must be perpendicular to its host face. Select another host face to reorient it.");
        ElementTransformUtils.MoveElement(doc, connector.Id, desired.Origin - current.Origin);
        var angle = Math.Atan2(desired.BasisZ.DotProduct(current.BasisX.CrossProduct(desired.BasisX)), current.BasisX.DotProduct(desired.BasisX));
        if (Math.Abs(angle) > 1e-8) ElementTransformUtils.RotateElement(doc, connector.Id, Line.CreateUnbound(desired.Origin, desired.BasisZ), angle);
        void Set(BuiltInParameter id, double meters)
        {
            var p = connector.get_Parameter(id);
            if (p is null || p.IsReadOnly || !p.Set(FFeet(meters))) throw new ArgumentException("Connector dimension cannot be assigned: " + id);
        }
        if (shape == ConnectorProfileType.Round) Set(BuiltInParameter.CONNECTOR_RADIUS, FNumber(op, "diameterMeters") / 2);
        else { Set(BuiltInParameter.CONNECTOR_WIDTH, FNumber(op, "widthMeters")); Set(BuiltInParameter.CONNECTOR_HEIGHT, FNumber(op, "heightMeters")); }
        var flow = connector.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM);
        if (flow is null || flow.IsReadOnly || !flow.Set((int)Enum.Parse<FlowDirectionType>(FString(op, "flowDirection")))) throw new ArgumentException("Cannot assign HVAC flow direction.");
        foreach (var binding in FArray(op, "bindings")) FAssociate(doc, connector, binding);
        if (FBool(op, "primary")) connector.AssignAsPrimary();
        doc.Regenerate(); current = connector.CoordinateSystem;
        if (current.Origin.DistanceTo(desired.Origin) > FFeet(0.0001) || current.BasisZ.DotProduct(desired.BasisZ) < 1 - 1e-6 || current.BasisX.DotProduct(desired.BasisX) < 1 - 1e-6)
            throw new InvalidOperationException("Revit did not retain the requested connector coordinate system.");
        var sizesMatch = shape == ConnectorProfileType.Round ? Math.Abs(FMeters(connector.Radius * 2) - FNumber(op, "diameterMeters")) < .0001
            : Math.Abs(FMeters(connector.Width) - FNumber(op, "widthMeters")) < .0001 && Math.Abs(FMeters(connector.Height) - FNumber(op, "heightMeters")) < .0001;
        if (!sizesMatch || flow.AsInteger() != (int)Enum.Parse<FlowDirectionType>(FString(op, "flowDirection"))) throw new InvalidOperationException("Connector dimensions or flow direction differ after regeneration/binding.");
    }
    private sealed class FamilyLoadPolicy(string parameterPolicy, string sharedSource) : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = parameterPolicy == "overwrite"; return parameterPolicy != "rejectExisting"; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = sharedSource == "file" ? FamilySource.Family : FamilySource.Project; overwriteParameterValues = parameterPolicy == "overwrite"; return parameterPolicy != "rejectExisting"; }
    }
    private static void FLoadNested(FamilyBuildContext ctx, JsonElement op)
    {
        var path = FPath(op, "path", ".rfa");
        if (op.TryGetProperty("shared", out var shared))
        {
            if (ctx.Document.Application.Documents.Cast<Document>().Any(d => string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Nested source is already open.");
            Document? nested = null;
            try
            {
                nested = ctx.Document.Application.OpenDocumentFile(path);
                if (!nested.IsFamilyDocument) throw new ArgumentException("Nested source is not a family.");
                using (var tx = new Transaction(nested, "Set shared family"))
                {
                    BeginCheckedTransaction(tx);
                    if (!nested.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED).Set(shared.GetBoolean() ? 1 : 0)) throw new InvalidOperationException("Cannot set shared family flag.");
                    CommitCheckedTransaction(tx, null);
                }
                var dir = Path.Combine(ctx.Scratch, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                path = Path.Combine(dir, Path.GetFileName(path)); nested.SaveAs(path);
            }
            finally { FClose(nested); }
        }
        if (!ctx.Document.LoadFamily(path, new FamilyLoadPolicy("rejectExisting", "project"), out var family) || family is null)
            throw new ArgumentException("Cannot load nested family; existing nested definitions are not overwritten.");
        ctx.Add(op, family);
    }
    private static FamilySymbol FNestedSymbol(FamilyBuildContext ctx, JsonElement op)
    {
        if (!ctx.Elements.TryGetValue(FString(op, "familyKey"), out var id) || ctx.Document.GetElement(id) is not Family family) throw new ArgumentException("Unknown nested family key.");
        return family.GetFamilySymbolIds().Select(i => (FamilySymbol)ctx.Document.GetElement(i)).SingleOrDefault(s => s.Name == FString(op, "typeName")) ?? throw new ArgumentException("Unknown nested type.");
    }
    private static void FGeometryOperation(FamilyBuildContext ctx, JsonElement op)
    {
        var doc = ctx.Document;
        switch (FString(op, "action"))
        {
            case "material":
                var material = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().SingleOrDefault(m => m.Name == FString(op, "name")) ?? (Material)doc.GetElement(Material.Create(doc, FString(op, "name")));
                if (op.TryGetProperty("color", out var color)) material.Color = new Color(color[0].GetByte(), color[1].GetByte(), color[2].GetByte());
                if (op.TryGetProperty("transparency", out var transparency)) material.Transparency = transparency.GetInt32(); break;
            case "subcategory":
                var parent = doc.OwnerFamily.FamilyCategory;
                var category = parent.SubCategories.Cast<Category>().SingleOrDefault(c => c.Name == FString(op, "name")) ?? doc.Settings.Categories.NewSubcategory(parent, FString(op, "name"));
                if (op.TryGetProperty("materialName", out var mat)) category.Material = FMaterial(doc, mat.GetString()!); break;
            case "extrusion":
                var frame = FFrame(op.GetProperty("frame")); var loops = new CurveArrArray();
                foreach (var loop in FArray(op, "loops"))
                {
                    var points = loop.EnumerateArray().Select(p => frame.OfPoint(new XYZ(FFeet(FNumber(p, "x")), FFeet(FNumber(p, "y")), 0))).ToArray(); var curves = new CurveArray();
                    if (points.Length < 3) throw new ArgumentException("A closed contour needs at least three vertices.");
                    for (var i = 0; i < points.Length; i++) curves.Append(Line.CreateBound(points[i], points[(i + 1) % points.Length]));
                    loops.Append(curves);
                }
                var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(frame.BasisZ, frame.Origin));
                var form = doc.FamilyCreate.NewExtrusion(FBool(op, "solid"), loops, plane, FFeet(FNumber(op, "depthMeters"))); ctx.Add(op, form);
                if (op.TryGetProperty("materialName", out var materialName)) form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM).Set(FMaterial(doc, materialName.GetString()!).Id);
                if (op.TryGetProperty("subcategory", out var subcategory)) form.Subcategory = doc.OwnerFamily.FamilyCategory.SubCategories.Cast<Category>().Single(c => c.Name == subcategory.GetString());
                if (op.TryGetProperty("visibility", out var visibility)) FVisibility(form, visibility); break;
            case "referencePlane":
                var view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.ViewType is ViewType.FloorPlan or ViewType.Elevation or ViewType.Section) ?? throw new ArgumentException("Family has no suitable view for a reference plane.");
                var reference = doc.FamilyCreate.NewReferencePlane(FPoint(op.GetProperty("bubble")), FPoint(op.GetProperty("free")), FVector(op.GetProperty("cutVector")), view); reference.Name = FString(op, "name"); ctx.Add(op, reference); break;
            case "cut":
                var solid = ctx.Get(op.GetProperty("solid")) as GenericForm; var hole = ctx.Get(op.GetProperty("void")) as GenericForm;
                if (solid?.IsSolid != true || hole?.IsSolid != false) throw new ArgumentException("cut requires a solid and a void form.");
                var combined = new CombinableElementArray(); combined.Append(solid); combined.Append(hole); doc.CombineElements(combined); break;
            case "setVisibility": FVisibility(ctx.Get(op.GetProperty("element")), op.GetProperty("visibility")); break;
            case "createHvacConnector": FHvacConnector(ctx, op, true); break;
            case "editHvacConnector": FHvacConnector(ctx, op, false); break;
            case "loadNestedFamily": FLoadNested(ctx, op); break;
            case "useNestedFamily":
                if (doc.GetElement(new ElementId(op.GetProperty("familyId").GetInt64())) is not Family existing) throw new ArgumentException("familyId must identify an existing nested family.");
                ctx.Add(op, existing); break;
            case "placeNestedFamily":
                var symbol = FNestedSymbol(ctx, op); if (!symbol.IsActive) symbol.Activate(); doc.Regenerate();
                var position = FPoint(op.GetProperty("position"));
                FamilyInstance instance;
                if (op.TryGetProperty("hostFace", out var face))
                {
                    var hostFace = FHostFace(ctx, face);
                    instance = doc.FamilyCreate.NewFamilyInstance(hostFace.Reference, position, hostFace.XVector, symbol);
                }
                else instance = doc.FamilyCreate.NewFamilyInstance(position, symbol, StructuralType.NonStructural);
                ctx.Add(op, instance);
                var angle = FNumber(op, "rotationDegrees") * Math.PI / 180;
                if (Math.Abs(angle) > 1e-9) ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateUnbound(position, XYZ.BasisZ), angle);
                if (op.TryGetProperty("visibility", out var visible)) FVisibility(instance, visible);
                foreach (var binding in FArray(op, "bindings")) FAssociate(doc, instance, binding); break;
            case "setNestedType":
                if (ctx.Get(op.GetProperty("element")) is not FamilyInstance nested) throw new ArgumentException("Expected nested family instance.");
                var newSymbol = FNestedSymbol(ctx, op); if (!newSymbol.IsActive) newSymbol.Activate(); nested.Symbol = newSymbol; break;
            default: throw new ArgumentException("Unknown family operation: " + FString(op, "action"));
        }
    }
}
