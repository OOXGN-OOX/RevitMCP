using Autodesk.Revit.DB;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    // DTOs are serialized while the native objects are valid, before any rollback/close.
    private static JsonElement FamilyInstanceState(Document doc, Family family)
    {
        var instances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(i => i.Symbol.Family.Id == family.Id).Select(i => i.Id).ToHashSet();
        var pending = new Queue<ElementId>(instances);
        while (pending.TryDequeue(out var id))
            foreach (var child in ((FamilyInstance)doc.GetElement(id)).GetSubComponentIds())
                if (doc.GetElement(child) is FamilyInstance && instances.Add(child)) pending.Enqueue(child);
        var rows = instances.OrderBy(i=>i.Value).Select(id => {
            var i = (FamilyInstance)doc.GetElement(id);
            return new { instanceId=id.Value, uniqueId=i.UniqueId, familyId=i.Symbol.Family.Id.Value, family=i.Symbol.FamilyName,
                typeId=i.Symbol.Id.Value, type=i.Symbol.Name, transform=FFrameInfo(i.GetTransform()),
                hostId=i.Host?.Id.Value, levelId=i.LevelId.Value, superComponentId=i.SuperComponent?.Id.Value,
                i.HandFlipped, i.FacingFlipped, i.Mirrored,
                nativeSystems=FamilyNativeSystemParameters(i),
                connectors=FamilyConnectorState(i) };
        }).ToArray();
        return JsonSerializer.SerializeToElement(new { familyId=family.Id.Value, family=family.Name,
            types=family.GetFamilySymbolIds().OrderBy(id=>id.Value).Select(id=>new { id=id.Value, name=doc.GetElement(id).Name }).ToArray(), instances=rows });
    }

    private static object[] FamilyNativeSystemParameters(Element element) => new[] {
        BuiltInParameter.RBS_SYSTEM_NAME_PARAM, BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM,
        BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM, BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM
    }.Select(id=>element.get_Parameter(id)).Where(p=>p is not null).Select(p=>FamilyParameterInfo(p!)).ToArray();

    private static object FamilyParameterInfo(Parameter p) => new {
        id=p.Id.Value, name=p.Definition.Name, storageType=p.StorageType.ToString(), hasValue=p.HasValue,
        rawValue=!p.HasValue ? null : p.StorageType switch {
            StorageType.Double => (object)p.AsDouble(), StorageType.Integer => p.AsInteger(),
            StorageType.ElementId => p.AsElementId().Value, StorageType.String => p.AsString(), _ => null },
        displayValue=p.HasValue ? DisplayParameterValue(p) : null,
        dataTypeId=p.Definition.GetDataType().TypeId,
        unitId=p.StorageType==StorageType.Double && UnitUtils.IsMeasurableSpec(p.Definition.GetDataType()) ? p.GetUnitTypeId().TypeId : null
    };

    private static object[] FamilyConnectorState(Element element) => MepConnectors(element)?.Connectors.Cast<Connector>().OrderBy(c=>c.Id).Select(c => {
        var physical=c.ConnectorType != ConnectorType.Logical;
        var system=c.MEPSystem;
        return (object)new { connectorId=c.Id, domain=c.Domain.ToString(), connectorType=c.ConnectorType.ToString(),
            frame=physical ? FFrameInfo(c.CoordinateSystem) : null,
            shape=physical ? c.Shape.ToString() : null,
            diameterMeters=physical && c.Shape==ConnectorProfileType.Round ? (double?)FMeters(c.Radius*2) : null,
            widthMeters=physical && c.Shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval ? (double?)FMeters(c.Width) : null,
            heightMeters=physical && c.Shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval ? (double?)FMeters(c.Height) : null,
            systemId=system?.Id.Value, systemName=system?.Name, systemTypeId=system?.GetTypeId().Value,
            classification=c.Domain==Domain.DomainHvac ? c.DuctSystemType.ToString() : c.Domain==Domain.DomainPiping ? c.PipeSystemType.ToString() : null,
            flowDirection=physical && c.Domain is Domain.DomainHvac or Domain.DomainPiping ? c.Direction.ToString() : null,
            isConnected=physical ? (bool?)c.IsConnected : null,
            allRefs=c.AllRefs.Cast<Connector>().OrderBy(r=>r.Owner.Id.Value).ThenBy(r=>r.Id).Select(r=>new {
                elementId=r.Owner.Id.Value, connectorId=r.Id, connectorType=r.ConnectorType.ToString(),
                physicalConnection=physical && r.ConnectorType!=ConnectorType.Logical && r.Owner.Id!=c.Owner.Id ? c.IsConnectedTo(r) : (bool?)null
            }).ToArray() };
    }).ToArray() ?? [];

    private static void RequireFamilyStatePreserved(JsonElement before, JsonElement after)
    {
        // New types are allowed on ordinary reload; all previously existing types must keep their IDs/names.
        if (!FamilyReloadComparison.Preserved(before,after))
            throw new RevitOperationException("Reload changed existing family/type/instance IDs, placement, native systems or connector references. The operation was rolled back.",
                new { before, after, preservationPassed=false });
    }
}
