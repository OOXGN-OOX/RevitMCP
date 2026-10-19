using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static ConnectorManager? MepConnectors(Element element) => element switch
    {
        MEPCurve curve => curve.ConnectorManager,
        FamilyInstance family => family.MEPModel?.ConnectorManager,
        _ => null
    };

    private static bool PhysicalMepEnd(Connector connector) => connector.ConnectorType == ConnectorType.End
        && connector.Domain is Domain.DomainHvac or Domain.DomainPiping;

    private static object ListMepConnectors(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var values = args.GetProperty("elementIds");
        if (values.GetArrayLength() is < 1 or > 50) throw new ArgumentException("Provide 1 to 50 element IDs.");
        var ids = values.EnumerateArray().Select(value => value.GetInt64()).ToArray();
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length) throw new ArgumentException("Element IDs must be positive and unique.");
        var unusedOnly = args.TryGetProperty("unusedOnly", out var unused) && unused.GetBoolean();
        return new { elements = ids.Select(id =>
        {
            var element = doc.GetElement(new ElementId(id)) ?? throw new ArgumentException($"Element {id} does not exist.");
            var manager = MepConnectors(element);
            var connectors = manager?.Connectors.Cast<Connector>().Where(PhysicalMepEnd).OrderBy(x => x.Id).ToArray() ?? [];
            return new { elementId = id, name = element.Name, supported = manager is not null,
                connectorCount = connectors.Length,
                connectors = connectors.Where(x => !unusedOnly || !x.IsConnected).Select(MepConnectorInfo).ToArray() };
        }).ToArray() };
    }

    private static object MepConnectorInfo(Connector connector)
    {
        var origin = connector.Origin;
        var normal = connector.CoordinateSystem.BasisZ;
        var shape = connector.Shape;
        double Meters(double value) => UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Meters);
        return new
        {
            elementId = connector.Owner.Id.Value, connectorId = connector.Id,
            domain = connector.Domain.ToString(), shape = shape.ToString(),
            systemClassification = connector.Domain == Domain.DomainHvac ? connector.DuctSystemType.ToString() : connector.PipeSystemType.ToString(),
            systemId = connector.MEPSystem?.Id.Value,
            origin = new { xMeters = Meters(origin.X), yMeters = Meters(origin.Y), zMeters = Meters(origin.Z) },
            direction = new { x = normal.X, y = normal.Y, z = normal.Z },
            diameterMeters = shape == ConnectorProfileType.Round ? (double?)Meters(connector.Radius * 2) : null,
            widthMeters = shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval ? (double?)Meters(connector.Width) : null,
            heightMeters = shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval ? (double?)Meters(connector.Height) : null,
            isConnected = connector.IsConnected,
            connectedTo = connector.AllRefs.Cast<Connector>()
                .Where(other => PhysicalMepEnd(other) && other.Owner.Id != connector.Owner.Id && connector.IsConnectedTo(other))
                .Select(other => new { elementId = other.Owner.Id.Value, connectorId = other.Id }).ToArray()
        };
    }

    private static object PreviewConnectMep(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "connectMep", "Соединение MEP-коннекторов", ConnectMep);

    private static object ApplyConnectMep(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "connectMep", "Соединить выбранные MEP-коннекторы?", ConnectMep, cancelled);

    private static Connector ReadMepConnector(Document doc, JsonElement reference)
    {
        var owner = doc.GetElement(new ElementId(HostedReadId(reference, "elementId")))
            ?? throw new ArgumentException("Connector owner no longer exists.");
        if (owner.Pinned || owner.GroupId != ElementId.InvalidElementId)
            throw new InvalidOperationException("Unpin the element and edit grouped elements outside this operation before connecting.");
        var connectorId = reference.GetProperty("connectorId").GetInt32();
        if (connectorId < 0) throw new ArgumentException("connectorId must be non-negative.");
        var connector = MepConnectors(owner)?.Lookup(connectorId)
            ?? throw new ArgumentException("Connector not found. Read list_mep_connectors again.");
        if (!PhysicalMepEnd(connector)) throw new ArgumentException("Only physical HVAC and piping end connectors are supported.");
        return connector;
    }

    private static object ConnectMep(Document doc, JsonElement args)
    {
        var firstRef = args.GetProperty("first");
        var secondRef = args.GetProperty("second");
        var first = ReadMepConnector(doc, firstRef);
        var second = ReadMepConnector(doc, secondRef);
        if (first.Owner.Id == second.Owner.Id) throw new ArgumentException("Connectors must belong to different elements.");
        if (first.Domain != second.Domain) throw new ArgumentException("Connector domains must match.");
        if (first.IsConnected || second.IsConnected) throw new InvalidOperationException("Both connectors must be free. Existing connections are not disconnected automatically.");
        var kind = JsonTools.GetString(args, "fitting");
        if (kind is not ("direct" or "elbow" or "union" or "transition")) throw new ArgumentException("Unknown fitting kind.");
        var before = new { first = MepConnectorInfo(first), second = MepConnectorInfo(second) };
        if (kind != "transition" && !SameMepSize(first, second))
            throw new ArgumentException("Connector sizes and shapes must match. Use a transition for different sizes or shapes.");
        FamilyInstance? fitting = null;
        if (kind == "direct")
        {
            if (first.Origin.DistanceTo(second.Origin) > BuildingFeet(0.0001)
                || first.CoordinateSystem.BasisZ.Normalize().DotProduct(second.CoordinateSystem.BasisZ.Normalize()) > -0.999999)
                throw new ArgumentException("Direct connection requires coincident ends facing in opposite directions. Position elements first or select a fitting.");
            if (first.Shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval
                && Math.Abs(first.CoordinateSystem.BasisX.Normalize().DotProduct(second.CoordinateSystem.BasisX.Normalize())) < 0.999999)
                throw new ArgumentException("Profile axes must align for a direct connection. Rotate the elements before connecting.");
            first.ConnectTo(second);
        }
        else
        {
            // Revit requires a pipe/duct as the first owner for unions and transitions.
            if (first.Owner is not MEPCurve && second.Owner is MEPCurve) (first, second) = (second, first);
            fitting = kind switch
            {
                "elbow" => doc.Create.NewElbowFitting(first, second),
                "union" => doc.Create.NewUnionFitting(first, second),
                _ => doc.Create.NewTransitionFitting(first, second)
            };
            if (fitting is null) throw new InvalidOperationException("Revit did not return the requested fitting. Check routing preferences and available families.");
        }
        doc.Regenerate();
        first = ReadMepConnector(doc, firstRef);
        second = ReadMepConnector(doc, secondRef);
        bool ConnectedToFitting(Connector end) => fitting is not null && end.AllRefs.Cast<Connector>()
            .Any(other => PhysicalMepEnd(other) && other.Owner.Id == fitting.Id && end.IsConnectedTo(other));
        if (fitting is null ? !first.IsConnectedTo(second) : !ConnectedToFitting(first) || !ConnectedToFitting(second))
            throw new InvalidOperationException("Revit did not establish the requested physical connection. The operation was rolled back.");
        return new { fittingKind = kind, fittingId = fitting?.Id.Value, fittingTypeId = fitting?.GetTypeId().Value,
            before, after = new { first = MepConnectorInfo(first), second = MepConnectorInfo(second) } };
    }

    private static bool SameMepSize(Connector first, Connector second)
    {
        if (first.Shape != second.Shape) return false;
        var tolerance = BuildingFeet(0.00001);
        return first.Shape switch
        {
            ConnectorProfileType.Round => Math.Abs(first.Radius - second.Radius) <= tolerance,
            ConnectorProfileType.Rectangular or ConnectorProfileType.Oval => Math.Abs(first.Width - second.Width) <= tolerance && Math.Abs(first.Height - second.Height) <= tolerance,
            _ => false
        };
    }
}
