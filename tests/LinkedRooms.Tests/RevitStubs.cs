// Minimal API doubles for executing the production room reader without a Revit process.
using System.Collections;
using System.Text.Json;

namespace Autodesk.Revit.DB
{
    public record ElementId(long Value) { public static ElementId InvalidElementId => new(-1); }
    public enum BuiltInParameter { ROOM_NAME, ROOM_PHASE_ID }
    public class Parameter(string text, ElementId id) { public string AsString() => text; public ElementId AsElementId() => id; }
    public class Location { }
    public class LocationPoint(XYZ point) : Location { public XYZ Point => point; }
    public class Element { public ElementId Id { get; init; } = new(0); public string Name { get; init; } = ""; public string UniqueId { get; init; } = "";
        public string? ParameterName { get; init; } public Location? Location { get; init; }
        public Parameter get_Parameter(BuiltInParameter key) => new(ParameterName??Name,new(50)); }
    public class Document
    {
        public string Title { get; init; } = "";
        public Element ProjectInformation { get; init; } = new();
        public List<Element> Elements { get; } = [];
        public Element? GetElement(ElementId id) => Elements.FirstOrDefault(e => e.Id == id);
    }
    public class RevitLinkInstance : Element
    {
        public Document? LinkedDocument { get; init; }
        public Transform Placement { get; init; } = Transform.Identity;
        public Document? GetLinkDocument() => LinkedDocument;
        public Transform GetTransform() => Placement;
    }
    public enum BuiltInCategory { OST_Rooms }
    public enum SpatialElementBoundaryLocation { Finish, Center, CoreBoundary, CoreCenter }
    public class SpatialElementBoundaryOptions : IDisposable { public SpatialElementBoundaryLocation SpatialElementBoundaryLocation { get; init; } public void Dispose() { } }
    public record XYZ(double X, double Y, double Z);
    public class Transform : IDisposable
    {
        public XYZ Origin { get; init; } = new(0, 0, 0);
        public XYZ BasisX { get; init; } = new(1, 0, 0);
        public XYZ BasisY { get; init; } = new(0, 1, 0);
        public XYZ BasisZ { get; init; } = new(0, 0, 1);
        public static Transform Identity => new();
        public XYZ OfPoint(XYZ p) => new(Origin.X+p.X*BasisX.X+p.Y*BasisY.X+p.Z*BasisZ.X,
            Origin.Y+p.X*BasisX.Y+p.Y*BasisY.Y+p.Z*BasisZ.Y, Origin.Z+p.X*BasisX.Z+p.Y*BasisY.Z+p.Z*BasisZ.Z);
        public void Dispose() { }
    }
    public class Curve : IDisposable
    {
        public List<XYZ> Points { get; init; } = [];
        public IList<XYZ> Tessellate() => Points;
        public void Dispose() { }
    }
    public class BoundarySegment { public ElementId ElementId { get; init; } = new(0); public Curve Curve { get; init; } = new(); public Curve GetCurve() => Curve; }
    public class FilteredElementCollector(Document doc) : IEnumerable<Element>
    {
        private IEnumerable<Element> elements = doc.Elements;
        public FilteredElementCollector OfClass(Type type) { elements = elements.Where(type.IsInstanceOfType); return this; }
        public FilteredElementCollector OfCategory(BuiltInCategory _) { elements = elements.OfType<Architecture.Room>(); return this; }
        public FilteredElementCollector WhereElementIsNotElementType() => this;
        public IEnumerator<Element> GetEnumerator() => elements.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public enum UnitTypeId { SquareMeters, Millimeters, CubicMeters }
    public static class UnitUtils
    {
        public static double ConvertFromInternalUnits(double value, UnitTypeId unit) => value * (unit switch {
            UnitTypeId.SquareMeters => .3048*.3048, UnitTypeId.Millimeters => 304.8, _ => .3048*.3048*.3048 });
    }
}
namespace Autodesk.Revit.DB.Architecture
{
    public class Room : DB.Element
    {
        public string Number { get; init; } = "";
        public DB.ElementId LevelId { get; init; } = new(0);
        public double Area { get; init; }
        public double Perimeter { get; init; }
        public double Volume { get; init; }
        public IList<IList<DB.BoundarySegment>>? Loops { get; init; }
        public IList<IList<DB.BoundarySegment>>? GetBoundarySegments(DB.SpatialElementBoundaryOptions _) => Loops;
    }
}
namespace Autodesk.Revit.UI { public class UIApplication { public DB.Document Document { get; init; } = new(); } }
namespace RevitMCP
{
    internal static class JsonTools
    {
        public static string GetString(JsonElement args, string key, string fallback = "") => args.TryGetProperty(key, out var value) ? value.GetString()! : fallback;
        public static int GetInt(JsonElement args, string key, int fallback) => args.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;
    }
    internal static partial class RevitTools
    {
        public static JsonElement ReadRooms(Autodesk.Revit.UI.UIApplication app, object args) => JsonSerializer.SerializeToElement(ListRooms(app, JsonSerializer.SerializeToElement(args)));
        public static JsonElement ReadBoundaries(Autodesk.Revit.UI.UIApplication app, object args) => JsonSerializer.SerializeToElement(GetRoomBoundaries(app, JsonSerializer.SerializeToElement(args)));
        private static Autodesk.Revit.DB.Document RequireDocument(Autodesk.Revit.UI.UIApplication app) => app.Document;
        private static long HostedReadId(JsonElement args, string key) => args.GetProperty(key).GetInt64();
        private static Autodesk.Revit.DB.ElementId[] GraphicsReadIds(JsonElement args, bool _) => args.GetProperty("elementIds").EnumerateArray().Select(v => new Autodesk.Revit.DB.ElementId(v.GetInt64())).ToArray();
        private static object DocVector(Autodesk.Revit.DB.XYZ p) => new { x=p.X, y=p.Y, z=p.Z };
        private static object DocPoint(Autodesk.Revit.DB.XYZ p) => new { x=p.X*.3048, y=p.Y*.3048, z=p.Z*.3048 };
    }
}
