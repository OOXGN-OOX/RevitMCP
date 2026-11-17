using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static readonly Guid SpaceSourceSchemaId = new("3b56a377-6499-4f68-8a98-e3f071179260");
    private sealed record SpaceSource(string LinkInstanceUniqueId, string RoomUniqueId, string DocumentKey);
    private sealed class SpaceBatchException(object result) : Exception("Space validation failed; no changes were retained.")
    { public object Result { get; } = result; }
    private static double SpaceMeters(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    private static double SpaceFeet(double meters) => UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
    private static double SpaceArea(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.SquareMeters);

    private static SpaceSource? ReadSpaceSource(Space space)
    {
        var schema = Schema.Lookup(SpaceSourceSchemaId);
        if (schema is null) return null;
        var entity = space.GetEntity(schema);
        return !entity.IsValid() ? null : new(entity.Get<string>("LinkInstanceUniqueId"),
            entity.Get<string>("RoomUniqueId"), entity.Get<string>("DocumentKey"));
    }

    private static void WriteSpaceSource(Space space, SpaceSource source)
    {
        var schema = Schema.Lookup(SpaceSourceSchemaId);
        if (schema is null)
        {
            var builder = new SchemaBuilder(SpaceSourceSchemaId);
            builder.SetSchemaName("RevitMCPLinkedRoomSpace");
            builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
            foreach (var name in new[] { "LinkInstanceUniqueId", "RoomUniqueId", "DocumentKey" }) builder.AddSimpleField(name, typeof(string));
            schema = builder.Finish();
        }
        var entity = new Entity(schema);
        entity.Set("LinkInstanceUniqueId", source.LinkInstanceUniqueId);
        entity.Set("RoomUniqueId", source.RoomUniqueId); entity.Set("DocumentKey", source.DocumentKey);
        space.SetEntity(entity);
    }

    private static object SpaceRecord(Document doc, Space space) => new {
        id = space.Id.Value, uniqueId = space.UniqueId, number = space.Number, name = SpatialName(space),
        source = ReadSpaceSource(space), levelId = space.LevelId.Value, level = doc.GetElement(space.LevelId)?.Name,
        phaseId = SpatialPhaseId(space).Value, phase = doc.GetElement(SpatialPhaseId(space))?.Name,
        areaSquareMeters = SpaceArea(space.Area), volumeCubicMeters = UnitUtils.ConvertFromInternalUnits(space.Volume, UnitTypeId.CubicMeters),
        locationMeters = space.Location is LocationPoint point ? DocPoint(point.Point) : null,
        lowerOffsetMeters = SpaceMeters(space.BaseOffset), upperLevelId = space.UpperLimit?.Id.Value,
        upperOffsetMeters = SpaceMeters(space.LimitOffset)
    };

    private static object ListSpaces(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var spaces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces)
            .WhereElementIsNotElementType().OfType<Space>().Where(s =>
                (!args.TryGetProperty("phaseId", out var phase) || SpatialPhaseId(s).Value == phase.GetInt64()) &&
                (!args.TryGetProperty("levelId", out var level) || s.LevelId.Value == level.GetInt64()))
            .OrderBy(s => s.Id.Value).ToArray();
        var offset = Math.Max(0, JsonTools.GetInt(args, "offset", 0));
        var limit = Math.Clamp(JsonTools.GetInt(args, "limit", 100), 1, 500);
        return new { coordinateSystem = "host_internal_meters", matchingCount = spaces.Length, offset,
            returnedCount = Math.Min(Math.Max(0, spaces.Length-offset), limit), hasMore = offset+limit < spaces.Length,
            spaces = spaces.Skip(offset).Take(limit).Select(s => SpaceRecord(doc, s)).ToArray() };
    }

    // Include all linked elements: wall edits can change Room geometry without changing Room parameters.
    private static string SpaceSourceFingerprint(Document host, JsonElement args)
    {
        var source = ResolveRoomSource(host, args);
        if (source.Link is null) throw new ArgumentException("linkInstanceId is required.");
        var type = (RevitLinkType)host.GetElement(source.Link.GetTypeId());
        using var transform = source.Link.GetTransform();
        var state = new { linkUniqueId = source.Link.UniqueId, typeUniqueId = type.UniqueId,
            documentKey = source.Document.ProjectInformation.UniqueId, source.Document.Title, source.Document.IsModified,
            transform = LinkTransformRecord(transform), phaseMap = type.GetPhaseMap().OrderBy(p => p.Key.Value)
                .Select(p => new { host = p.Key.Value, source = p.Value.Value }).ToArray(),
            elements = new FilteredElementCollector(source.Document).OrderBy(e => e.Id.Value)
                .Select(e => new { e.UniqueId, e.VersionGuid }).ToArray() };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonTools.SerializerOptions))));
    }

    private static void CheckSpaceSourceFingerprint(Document doc, JsonElement args)
    {
        if (!args.TryGetProperty("_spaceSourceFingerprint", out var expected) || expected.GetString() != SpaceSourceFingerprint(doc, args))
            throw new InvalidOperationException("The linked document, phase map, source values or link transform changed. Prepare a new preview.");
    }

    private static object PreviewSpacesFromLink(UIApplication app, JsonElement args)
    {
        try {
            var bound = JsonNode.Parse(args.GetRawText())!.AsObject();
            bound["_spaceSourceFingerprint"] = SpaceSourceFingerprint(RequireDocument(app), args);
            return PreviewOperation(app, JsonSerializer.SerializeToElement(bound), "spacesFromLink", "Пространства по помещениям связи", CreateSpacesFromLink, CheckSpacesAfterCommit);
        }
        catch (SpaceBatchException error) { return new { planCreated = false, modelChanged = false, result = error.Result, error = error.Message }; }
        catch (RevitOperationException error) { return new { planCreated = false, modelChanged = false, error = error.Message, diagnostics = error.Details }; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) {
            return new { planCreated = false, modelChanged = false, result = new {
                rows = args.GetProperty("roomIds").EnumerateArray().Select(id => new { roomId = id.GetInt64(), action = "error", error = error.Message }).ToArray()
            }, error = error.Message };
        }
    }

    private static object ApplySpacesFromLink(UIApplication app, JsonElement args, Func<bool>? cancelled)
    {
        var doc = RequireDocument(app);
        var plan = GetOperationPlan(doc, JsonTools.GetString(args, "planId"), "spacesFromLink");
        CheckSpaceSourceFingerprint(doc, plan.Arguments);
        return ApplyOperation(app, args, "spacesFromLink", "Создать пространства по помещениям связи?", CreateSpacesFromLink, cancelled, CheckSpacesAfterCommit);
    }

    private static void CheckSpatialEnclosure(SpatialElement spatial)
    {
        using var options = new SpatialElementBoundaryOptions();
        var loops = spatial.GetBoundarySegments(options);
        if (loops is null || loops.Count == 0) throw new InvalidOperationException("No enclosing boundary loops.");
        foreach (var loop in loops)
        {
            if (loop.Count == 0) throw new InvalidOperationException("Empty boundary loop.");
            for (var i = 0; i < loop.Count; i++)
            {
                using var current = loop[i].GetCurve(); using var next = loop[(i+1)%loop.Count].GetCurve();
                if (current.GetEndPoint(1).DistanceTo(next.GetEndPoint(0)) > SpaceFeet(.001))
                    throw new InvalidOperationException("Boundary loop is not closed within 1 mm.");
            }
        }
    }

    private static void ValidateCreatedSpace(Space space, ElementId phaseId, XYZ point)
    {
        if (SpatialPhaseId(space) != phaseId) throw new InvalidOperationException("Created Space phase does not match hostPhaseId.");
        if (space.Area <= 1e-8) throw new InvalidOperationException("Space has zero area or is not enclosed.");
        CheckSpatialEnclosure(space);
        if (!space.IsPointInSpace(point)) throw new InvalidOperationException("Created Space does not contain the verified source point.");
    }

    private static void CopySpaceIdentity(Space space, Room room, bool names, bool numbers)
    {
        void Set(BuiltInParameter key, string value)
        {
            var parameter = space.get_Parameter(key);
            if (parameter is null || parameter.IsReadOnly || (parameter.AsString() != value && !parameter.Set(value)))
                throw new InvalidOperationException($"Cannot write Space {key}.");
        }
        if (names) Set(BuiltInParameter.ROOM_NAME, SpatialName(room));
        if (numbers) Set(BuiltInParameter.ROOM_NUMBER, room.Number);
    }

    private static object CreateSpacesFromLink(Document doc, JsonElement args)
    {
        CheckSpaceSourceFingerprint(doc, args);
        var source = ResolveRoomSource(doc, args);
        var link = source.Link!;
        using var transform = link.GetTransform();
        if (Math.Abs(transform.BasisZ.X)>1e-8 || Math.Abs(transform.BasisZ.Y)>1e-8 || Math.Abs(transform.BasisZ.Z-1)>1e-8)
            throw new ArgumentException("Space creation requires horizontal linked levels without a vertical reflection.");
        var phaseId = new ElementId(HostedReadId(args, "hostPhaseId"));
        var phase = doc.GetElement(phaseId) as Phase ?? throw new ArgumentException("hostPhaseId must identify a host phase.");
        var type = (RevitLinkType)doc.GetElement(link.GetTypeId());
        if (!type.GetPhaseMap().TryGetValue(phaseId, out var sourcePhase)) throw new ArgumentException("Host phase has no mapped link phase.");
        var roomIds = args.GetProperty("roomIds").EnumerateArray().Select(v => new ElementId(v.GetInt64())).ToArray();
        if (roomIds.Length is <1 or >100 || roomIds.Any(id=>id.Value<1||id.Value>9007199254740991L) || roomIds.Distinct().Count()!=roomIds.Length)
            throw new ArgumentException("Provide 1 to 100 distinct positive room IDs.");
        var levelMap = args.GetProperty("levelMap").EnumerateArray().ToDictionary(v => HostedReadId(v, "sourceLevelId"), v => HostedReadId(v, "hostLevelId"));
        var extents = args.GetProperty("verticalExtents").EnumerateArray().ToDictionary(v => HostedReadId(v, "roomId"), v => v.Clone());
        if (extents.Count != roomIds.Length || roomIds.Any(id => !extents.ContainsKey(id.Value))) throw new ArgumentException("Provide vertical extents for every room.");
        var policy = JsonTools.GetString(args, "existingPolicy", "skip");
        if (policy is not ("skip" or "updateNamesAndNumbers" or "error")) throw new ArgumentException("Invalid existingPolicy.");
        bool Flag(string key, bool fallback) => args.TryGetProperty(key, out var flag) ? flag.GetBoolean() : fallback;
        var spaces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType().OfType<Space>().ToList();
        var settings = new List<object>();
        var bounding = type.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
        if (bounding is null) throw new InvalidOperationException("The link type has no Room Bounding parameter.");
        if (bounding.AsInteger() != 1)
        {
            settings.Add(new { setting = "roomBounding", typeId = type.Id.Value, typeUniqueId = type.UniqueId, before = false, after = true,
                affectedInstances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                    .Where(l => l.GetTypeId() == type.Id).Select(l => new { id = l.Id.Value, uniqueId = l.UniqueId }).ToArray() });
            if (!Flag("enableRoomBounding", false)) throw new SpaceBatchException(new { settingsChanges = settings,
                rows = roomIds.Select(id => new { roomId = id.Value, action = "error", error = "Link is not room-bounding. Set enableRoomBounding=true to permit the shown type-wide change." }).ToArray() });
            if (bounding.IsReadOnly || !bounding.Set(1)) throw new InvalidOperationException("Cannot enable Room Bounding on the link type.");
            doc.Regenerate();
        }
        var rows = new List<object>(); var hasErrors = false;
        foreach (var roomId in roomIds)
        {
            using var sub = new SubTransaction(doc); sub.Start();
            try
            {
                var room = source.Document.GetElement(roomId) as Room ?? throw new ArgumentException("Source ID is not a Room.");
                if (SpatialPhaseId(room) != sourcePhase) throw new ArgumentException("Room phase does not match the link phase mapped from hostPhaseId.");
                if (room.Area <= 1e-8 || room.Location is not LocationPoint location) throw new ArgumentException("Source Room is unplaced or has zero area.");
                CheckSpatialEnclosure(room);
                if (!levelMap.TryGetValue(room.LevelId.Value, out var mappedId)) throw new ArgumentException("Missing sourceLevelId in levelMap.");
                var sourceLevel = (Level)source.Document.GetElement(room.LevelId);
                var hostLevel = doc.GetElement(new ElementId(mappedId)) as Level ?? throw new ArgumentException("hostLevelId is not a host Level.");
                var transformedLevel = transform.OfPoint(new XYZ(0, 0, sourceLevel.ProjectElevation)).Z;
                if (Math.Abs(transformedLevel-hostLevel.ProjectElevation)>SpaceFeet(.001)) throw new ArgumentException("Mapped level elevations differ by more than 1 mm after the link transform.");
                var extent = extents[roomId.Value];
                var upperLevel = doc.GetElement(new ElementId(HostedReadId(extent, "upperLevelId"))) as Level ?? throw new ArgumentException("upperLevelId is not a host Level.");
                var lowerOffset = SpaceFeet(BuildingNumber(extent, "lowerOffsetMeters", -1000, 1000));
                var upperOffset = SpaceFeet(BuildingNumber(extent, "upperOffsetMeters", -1000, 1000));
                var bottom = hostLevel.ProjectElevation+lowerOffset; var top = upperLevel.ProjectElevation+upperOffset;
                if (top-bottom<=SpaceFeet(.001)) throw new ArgumentException("Vertical space extent must be positive and exceed 1 mm.");
                var sourceBottom = transform.OfPoint(new XYZ(0, 0, sourceLevel.ProjectElevation+room.BaseOffset)).Z;
                var sourceTop = sourceBottom+room.UnboundedHeight;
                var sharedBottom = Math.Max(bottom, sourceBottom); var sharedTop = Math.Min(top, sourceTop);
                if (sharedTop-sharedBottom<=SpaceFeet(.001)) throw new ArgumentException("Requested Space and source Room vertical extents do not overlap.");
                var hostPoint = transform.OfPoint(location.Point);
                hostPoint = new XYZ(hostPoint.X, hostPoint.Y, (sharedBottom+sharedTop)/2);
                if (!room.IsPointInRoom(transform.Inverse.OfPoint(hostPoint))) throw new ArgumentException("The Room placement point is not inside its actual enclosure.");
                var key = new SpaceSource(link.UniqueId, room.UniqueId, source.Document.ProjectInformation.UniqueId);
                var matches = spaces.Where(s => ReadSpaceSource(s) is { } tracked && tracked.LinkInstanceUniqueId==key.LinkInstanceUniqueId && tracked.RoomUniqueId==key.RoomUniqueId).ToArray();
                if (matches.Length>1) throw new InvalidOperationException("Multiple Spaces have the same stored source; resolve duplicates first.");
                var conflicts = spaces.Where(s => SpatialPhaseId(s)==phaseId && s.Area>1e-8 && s.IsPointInSpace(hostPoint) && !matches.Contains(s)).ToArray();
                if (conflicts.Length>0) throw new InvalidOperationException("Source point overlaps other Spaces (including untracked Spaces): " + string.Join(",", conflicts.Select(s => s.Id.Value)));
                Space space; string action;
                if (matches.Length==1)
                {
                    space = matches[0];
                    if (ReadSpaceSource(space)!.DocumentKey != key.DocumentKey) throw new InvalidOperationException("Stored source document differs.");
                    if (policy=="error") throw new InvalidOperationException("A Space with this stored source already exists.");
                    if (space.LevelId!=hostLevel.Id || space.UpperLimit?.Id!=upperLevel.Id ||
                        Math.Abs(space.BaseOffset-lowerOffset)>SpaceFeet(.001) || Math.Abs(space.LimitOffset-upperOffset)>SpaceFeet(.001))
                        throw new InvalidOperationException("Existing Space has different vertical constraints; metadata-only updates cannot change them.");
                    action = policy=="skip" ? "skip" : "updateNamesAndNumbers";
                    if (action!="skip") CopySpaceIdentity(space, room, Flag("copyNames", true), Flag("copyNumbers", true));
                }
                else
                {
                    action = "create";
                    space = doc.Create.NewSpace(hostLevel, phase, new UV(hostPoint.X, hostPoint.Y)) ?? throw new InvalidOperationException("Revit did not create a Space.");
                    space.UpperLimit = upperLevel; space.LimitOffset = upperOffset; space.BaseOffset = lowerOffset;
                    CopySpaceIdentity(space, room, Flag("copyNames", true), Flag("copyNumbers", true));
                    WriteSpaceSource(space, key);
                }
                doc.Regenerate(); ValidateCreatedSpace(space, phaseId, hostPoint);
                var area = SpaceArea(space.Area); var sourceArea = SpaceArea(room.Area);
                var row = new { roomId = room.Id.Value, roomUniqueId = room.UniqueId, roomNumber = room.Number, roomName = SpatialName(room),
                    action, spaceId = space.Id.Value, sourceAreaSquareMeters = sourceArea, spaceAreaSquareMeters = area,
                    areaDifferenceSquareMeters = area-sourceArea, areaDifferencePercent = (area-sourceArea)/sourceArea*100,
                    sourceBottomMeters = SpaceMeters(sourceBottom), sourceTopMeters = SpaceMeters(sourceTop),
                    bottomMeters = SpaceMeters(bottom), topMeters = SpaceMeters(top), verifiedPointMeters = DocPoint(hostPoint),
                    space = SpaceRecord(doc, space) };
                if (sub.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("Revit rolled back the Space subtransaction.");
                rows.Add(row);
                if (action=="create") spaces.Add(space);
            }
            catch (Exception error) when (error is not Autodesk.Revit.Exceptions.RegenerationFailedException)
            {
                if (sub.GetStatus()==TransactionStatus.Started) sub.RollBack();
                hasErrors = true; rows.Add(new { roomId = roomId.Value, action = "error", error = error.Message });
            }
        }
        var result = new { linkInstanceId = link.Id.Value, linkInstanceUniqueId = link.UniqueId, hostPhaseId = phaseId.Value, rows, settingsChanges = settings };
        if (hasErrors) throw new SpaceBatchException(result);
        return result;
    }

    private static JsonElement CheckSpacesAfterCommit(Document doc, JsonElement args, JsonElement result)
    {
        CheckSpaceSourceFingerprint(doc, args);
        var node = JsonNode.Parse(result.GetRawText())!.AsObject();
        foreach (var row in node["rows"]!.AsArray())
        {
            var space = doc.GetElement(new ElementId(row!["spaceId"]!.GetValue<long>())) as Space
                ?? throw new InvalidOperationException("Space disappeared during commit.");
            var point = row["verifiedPointMeters"]!;
            ValidateCreatedSpace(space, new ElementId(HostedReadId(args, "hostPhaseId")), new XYZ(
                SpaceFeet(point["x"]!.GetValue<double>()), SpaceFeet(point["y"]!.GetValue<double>()), SpaceFeet(point["z"]!.GetValue<double>())));
            if (row["action"]!.GetValue<string>()!="skip")
            {
                if ((!args.TryGetProperty("copyNames",out var names)||names.GetBoolean()) && SpatialName(space)!=row["roomName"]!.GetValue<string>())
                    throw new InvalidOperationException("Space name changed during commit.");
                if ((!args.TryGetProperty("copyNumbers",out var numbers)||numbers.GetBoolean()) && space.Number!=row["roomNumber"]!.GetValue<string>())
                    throw new InvalidOperationException("Space number changed during commit.");
            }
            var area = SpaceArea(space.Area); var sourceArea = row["sourceAreaSquareMeters"]!.GetValue<double>();
            row["spaceAreaSquareMeters"] = area; row["areaDifferenceSquareMeters"] = area-sourceArea;
            row["areaDifferencePercent"] = (area-sourceArea)/sourceArea*100;
            row["space"] = JsonSerializer.SerializeToNode(SpaceRecord(doc, space), JsonTools.SerializerOptions);
        }
        return JsonSerializer.SerializeToElement(node, JsonTools.SerializerOptions);
    }
}
