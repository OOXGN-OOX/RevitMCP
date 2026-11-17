using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static object LinkTransformRecord(Transform transform) => new {
        coordinateSystem = "source_internal_to_host_internal", originMeters = DocPoint(transform.Origin),
        xAxis = DocVector(transform.BasisX), yAxis = DocVector(transform.BasisY), zAxis = DocVector(transform.BasisZ)
    };

    private static object ListRevitLinks(UIApplication app)
    {
        var host = RequireDocument(app);
        return new { links = new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>().OrderBy(l => l.Id.Value).Select(link => {
                var linked = link.GetLinkDocument();
                var type = (RevitLinkType)host.GetElement(link.GetTypeId());
                using var transform = link.GetTransform();
                var errors = new List<string>();
                object? phaseMap = null;
                if (linked is not null)
                    try { phaseMap = type.GetPhaseMap().OrderBy(p => p.Key.Value).Select(p => new {
                        hostPhaseId = p.Key.Value, hostPhase = host.GetElement(p.Key)?.Name,
                        sourcePhaseId = p.Value.Value, sourcePhase = linked.GetElement(p.Value)?.Name }).ToArray(); }
                    catch (Exception e) { errors.Add("phaseMap: " + e.Message); }
                return new {
                    id = link.Id.Value, uniqueId = link.UniqueId, name = link.Name,
                    typeId = type.Id.Value, typeUniqueId = type.UniqueId, typeName = type.Name,
                    documentTitle = linked?.Title ?? type.Name, documentKey = linked?.ProjectInformation.UniqueId,
                    isLoaded = linked is not null, available = linked is not null,
                    unavailableReason = linked is null ? "Link document is unloaded or unavailable." : null,
                    roomBounding = type.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.AsInteger() == 1,
                    roomBoundingScope = "link_type_all_instances", phaseMap, transformToHost = LinkTransformRecord(transform),
                    levels = linked is null ? null : new FilteredElementCollector(linked).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => l.ProjectElevation).ThenBy(l => l.Id.Value).Select(l => new {
                            id = l.Id.Value, uniqueId = l.UniqueId, name = l.Name,
                            sourceElevationMeters = UnitUtils.ConvertFromInternalUnits(l.ProjectElevation, UnitTypeId.Meters),
                            hostOriginMeters = DocPoint(transform.OfPoint(new XYZ(0, 0, l.ProjectElevation))) }).ToArray(), errors
                };
            }).ToArray() };
    }
}
