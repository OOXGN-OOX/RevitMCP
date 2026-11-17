using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private sealed record RoomSource(Document Document, RevitLinkInstance? Link);
    private static string SpatialName(Element element) => element.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
    private static ElementId SpatialPhaseId(Element element) => element.get_Parameter(BuiltInParameter.ROOM_PHASE_ID)?.AsElementId() ?? ElementId.InvalidElementId;
    private static object? RoomContours(Room room, Transform transform)
    {
        using var options = new SpatialElementBoundaryOptions();
        return room.GetBoundarySegments(options)?.Select(loop => loop.Select(segment => {
            using var curve = segment.GetCurve();
            return new { elementId = segment.ElementId.Value, kind = curve.GetType().Name,
                points = curve.Tessellate().Select(p => DocPoint(transform.OfPoint(p))).ToArray() };
        }).ToArray()).ToArray();
    }

    private static RoomSource ResolveRoomSource(Document host, JsonElement args)
    {
        if (!args.TryGetProperty("linkInstanceId", out _)) return new(host, null);
        var id = HostedReadId(args, "linkInstanceId");
        if (host.GetElement(new ElementId(id)) is not RevitLinkInstance link)
            throw new ArgumentException("linkInstanceId must identify a Revit link instance in the active project.");
        var linked = link.GetLinkDocument()
            ?? throw new InvalidOperationException($"Link instance {id} is unloaded or its document is unavailable.");
        return new(linked, link);
    }

    private static object ListRooms(UIApplication app, JsonElement args)
    {
        var host = RequireDocument(app);
        var includeLinks = args.TryGetProperty("includeLinks", out var flag) && flag.GetBoolean();
        if (includeLinks && args.TryGetProperty("linkInstanceId", out _))
            throw new ArgumentException("Choose linkInstanceId or includeLinks=true.");
        var sources = new List<RoomSource>();
        var links = new List<object>();
        if (includeLinks)
        {
            sources.Add(new(host, null));
            foreach (var link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>().OrderBy(l => l.Id.Value))
            {
                var linked = link.GetLinkDocument();
                links.Add(RoomLinkRecord(link, linked));
                // Keep each instance: the same document can be placed several times in the host.
                if (linked is not null) sources.Add(new(linked, link));
            }
        }
        else
        {
            var source = ResolveRoomSource(host, args);
            sources.Add(source);
            if (source.Link is not null) links.Add(RoomLinkRecord(source.Link, source.Document));
        }

        var levelFilter = JsonTools.GetString(args, "levelName");
        var limit = Math.Clamp(JsonTools.GetInt(args, "limit", 200), 1, 500);
        var offset = Math.Max(0, JsonTools.GetInt(args, "offset", 0));
        var rooms = new List<(Room Room, RoomSource Source)>();
        foreach (var source in sources)
            foreach (var room in new FilteredElementCollector(source.Document).OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType().OfType<Room>().OrderBy(r => r.Id.Value))
            {
                var levelName = source.Document.GetElement(room.LevelId)?.Name;
                if (levelFilter.Length > 0 && !string.Equals(levelName, levelFilter, StringComparison.OrdinalIgnoreCase)) continue;
                rooms.Add((room, source));
            }
        return new {
            matchingCount = rooms.Count, offset, returnedCount = Math.Min(Math.Max(0, rooms.Count - offset), limit),
            hasMore = offset + limit < rooms.Count, links,
            coordinateSystem = "host_internal_meters",
            rooms = rooms.Skip(offset).Take(limit).Select(row => {
                using var transform = row.Source.Link?.GetTransform() ?? Transform.Identity;
                var point = (row.Room.Location as LocationPoint)?.Point;
                var phaseId = SpatialPhaseId(row.Room);
                return new {
                id = row.Room.Id.Value, uniqueId = row.Room.UniqueId, number = row.Room.Number, name = SpatialName(row.Room),
                level = row.Source.Document.GetElement(row.Room.LevelId)?.Name, levelId = row.Room.LevelId.Value,
                phaseId = phaseId.Value, phase = row.Source.Document.GetElement(phaseId)?.Name,
                locationMeters = point is null ? null : DocPoint(transform.OfPoint(point)),
                sourceLocationMeters = point is null ? null : DocPoint(point), sourceCoordinateSystem = "source_internal_meters",
                contours = RoomContours(row.Room, transform),
                linkInstanceId = row.Source.Link?.Id.Value, linkInstanceName = row.Source.Link?.Name,
                linkInstanceUniqueId = row.Source.Link?.UniqueId,
                documentTitle = row.Source.Document.Title, documentKey = row.Source.Document.ProjectInformation.UniqueId,
                areaSquareMeters = UnitUtils.ConvertFromInternalUnits(row.Room.Area, UnitTypeId.SquareMeters),
                perimeterMillimeters = UnitUtils.ConvertFromInternalUnits(row.Room.Perimeter, UnitTypeId.Millimeters),
                volumeCubicMeters = UnitUtils.ConvertFromInternalUnits(row.Room.Volume, UnitTypeId.CubicMeters)
            }; }).ToArray()
        };
    }

    private static object RoomLinkRecord(RevitLinkInstance link, Document? linked) => new {
        linkInstanceId = link.Id.Value, name = link.Name, isLoaded = linked is not null,
        documentTitle = linked?.Title, documentKey = linked?.ProjectInformation.UniqueId
    };

    private static object GetRoomBoundaries(UIApplication app, JsonElement args)
    {
        var source = ResolveRoomSource(RequireDocument(app), args);
        using var transform = source.Link?.GetTransform() ?? Transform.Identity;
        using var options = new SpatialElementBoundaryOptions {
            SpatialElementBoundaryLocation = Enum.Parse<SpatialElementBoundaryLocation>(JsonTools.GetString(args, "location", "Finish"))
        };
        return new {
            coordinateSystem = "host_internal_meters",
            linkInstanceId = source.Link?.Id.Value, documentTitle = source.Document.Title,
            documentKey = source.Document.ProjectInformation.UniqueId,
            transformToHost = new { originMeters = DocPoint(transform.Origin), xAxis = DocVector(transform.BasisX),
                yAxis = DocVector(transform.BasisY), zAxis = DocVector(transform.BasisZ) },
            rooms = GraphicsReadIds(args, false).Select(id => {
                var room = source.Document.GetElement(id) as Room
                    ?? throw new ArgumentException($"{id.Value} is not a room in the selected document.");
                var loops = room.GetBoundarySegments(options);
                var phaseId = SpatialPhaseId(room);
                return new { id = id.Value, uniqueId = room.UniqueId, name = SpatialName(room), number = room.Number, closed = room.Area > 0,
                    phaseId = phaseId.Value, phase = source.Document.GetElement(phaseId)?.Name,
                    levelId = room.LevelId.Value, level = source.Document.GetElement(room.LevelId)?.Name,
                    areaSquareMeters = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters),
                    volumeCubicMeters = UnitUtils.ConvertFromInternalUnits(room.Volume, UnitTypeId.CubicMeters),
                    locationMeters = room.Location is LocationPoint point ? DocPoint(transform.OfPoint(point.Point)) : null,
                    linkInstanceId = source.Link?.Id.Value,
                    loops = loops?.Select(loop => loop.Select(segment => {
                        using var curve = segment.GetCurve();
                        return new { elementId = segment.ElementId.Value, kind = curve.GetType().Name,
                            points = curve.Tessellate().Select(p => DocPoint(transform.OfPoint(p))).ToArray() };
                    }).ToArray()).ToArray() };
            }).ToArray()
        };
    }
}
