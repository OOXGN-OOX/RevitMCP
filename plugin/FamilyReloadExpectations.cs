using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RevitMCP;

// A bounded before -> after contract, never a general field-ignore list.
internal static class FamilyReloadExpectations
{
    internal sealed record TypeIdentity(string Family,long TypeId,string Type);
    internal sealed record TypeTarget(string Family,string Type);
    internal sealed record NestedType(TypeIdentity Before,TypeTarget After);
    internal sealed record TextChange(string Before,string After);
    internal sealed record Classification(int ConnectorId,string Before,string After);
    internal sealed record LogicalReference(int ConnectorId,long SystemId);
    internal sealed record Expected(long InstanceId,string UniqueId,NestedType? NestedType=null,
        TextChange? SystemClassification=null,Classification[]? ConnectorClassifications=null,
        LogicalReference[]? LogicalSystemReferences=null,int[]? RemoveUnconnectedElectricalConnectors=null);
    internal sealed record Match(long InstanceId,bool Passed,Expected Expected,string[] Issues);
    internal sealed record Result(Match[] Matches,string[] Issues);
    private const long SystemClassificationParameterId=-1140325; // BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM
    private static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web) { UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };

    internal static Result Check(JsonElement before,JsonElement after,JsonElement? expectations)
    {
        if(!expectations.HasValue) return new([],[]);
        Expected[] entries;
        try { entries=expectations.Value.Deserialize<Expected[]>(Options) ?? throw new JsonException("Expected an array."); }
        catch(JsonException e) { return new([], ["Invalid expectedChanges: "+e.Message]); }
        if(entries.Length is <1 or >500 || entries.Any(e=>e is null)) return new([], ["expectedChanges must contain 1 to 500 instance contracts."]);
        if(entries.Any(e=>(e.ConnectorClassifications??[]).Any(c=>c is null)||(e.LogicalSystemReferences??[]).Any(r=>r is null)))
            return new([], ["Connector contracts cannot contain null entries."]);
        if(entries.Select(e=>e.InstanceId).Distinct().Count()!=entries.Length) return new([], ["Duplicate instanceId in expectedChanges."]);
        // Compare() already rejects duplicate native IDs; avoid dictionary exceptions when reporting that failure.
        var oldRows=before.GetProperty("instances").EnumerateArray().GroupBy(r=>r.GetProperty("instanceId").GetInt64()).ToDictionary(g=>g.Key,g=>g.First());
        var newRows=after.GetProperty("instances").EnumerateArray().GroupBy(r=>r.GetProperty("instanceId").GetInt64()).ToDictionary(g=>g.Key,g=>g.First());
        bool Root(JsonElement row) => !row.TryGetProperty("superComponentId",out var p)||p.ValueKind!=JsonValueKind.Number||!oldRows.ContainsKey(p.GetInt64());
        var matches=new List<Match>();
        foreach(var entry in entries)
        {
            var issues=new List<string>();
            void Require(bool passed,string message) { if(!passed) issues.Add(message); }
            if(!oldRows.TryGetValue(entry.InstanceId,out var old)||!newRows.TryGetValue(entry.InstanceId,out var actual))
            { matches.Add(new(entry.InstanceId,false,entry,["The existing instance must be present before and after reload."])); continue; }
            var wanted=JsonNode.Parse(old.GetRawText())!.AsObject();
            var result=JsonNode.Parse(actual.GetRawText())!.AsObject();
            string? Text(JsonObject obj,string key)=>obj[key]?.GetValue<string>();
            Require(!string.IsNullOrWhiteSpace(entry.UniqueId)&&Text(wanted,"uniqueId")==entry.UniqueId&&Text(result,"uniqueId")==entry.UniqueId,"Instance UniqueId does not match the contract.");
            wanted["familyId"]=result["familyId"]?.DeepClone();
            Require(entry.NestedType is not null||entry.SystemClassification is not null||(entry.ConnectorClassifications?.Length??0)>0
                ||(entry.LogicalSystemReferences?.Length??0)>0||(entry.RemoveUnconnectedElectricalConnectors?.Length??0)>0,"An instance contract must specify an expected change.");

            if(entry.NestedType is { } type)
            {
                Require(!Root(old),"Root family/symbol identity cannot be changed by expectedChanges.");
                Require(type.Before is not null&&type.After is not null,"Both nested type identities are required.");
                if(type.Before is { } from&&type.After is { } to)
                {
                    Require(!string.IsNullOrWhiteSpace(to.Family)&&!string.IsNullOrWhiteSpace(to.Type),"The target nested family and type must be exact nonempty names.");
                    Require(Text(wanted,"family")==from.Family&&wanted["typeId"]!.GetValue<long>()==from.TypeId&&Text(wanted,"type")==from.Type,"Nested type before reload differs from the contract.");
                    Require(Text(result,"family")==to.Family&&Text(result,"type")==to.Type,"Nested type after reload differs from the contract.");
                    // Newly loaded symbol IDs are temporary in preview. Match its exact family/type, report the native ID unchanged.
                    wanted["family"]=to.Family; wanted["type"]=to.Type; wanted["typeId"]=result["typeId"]?.DeepClone();
                }
            }
            if(entry.SystemClassification is { } classification)
            {
                var parameters=wanted["nativeSystems"]!.AsArray().OfType<JsonObject>().Where(p=>p["id"]!.GetValue<long>()==SystemClassificationParameterId).ToArray();
                Require(parameters.Length==1,"Expected exactly one native system classification parameter.");
                if(parameters.Length==1)
                {
                    Require(classification.Before!=classification.After&&Text(parameters[0],"rawValue")==classification.Before&&Text(parameters[0],"displayValue")==classification.Before,"Native classification before reload differs from the contract or is unchanged.");
                    parameters[0]["rawValue"]=classification.After; parameters[0]["displayValue"]=classification.After;
                }
            }

            var ports=wanted["connectors"]!.AsArray(); var actualPorts=result["connectors"]!.AsArray();
            JsonObject? Port(JsonArray rows,int id)
            {
                var found=rows.OfType<JsonObject>().Where(p=>p["connectorId"]!.GetValue<int>()==id).ToArray();
                Require(found.Length==1,$"Connector {id} must exist exactly once.");
                return found.Length==1 ? found[0] : null;
            }
            void Unique(IEnumerable<int> ids,string label) { var values=ids.ToArray(); Require(values.Length<=200&&values.All(id=>id>=0)&&values.Distinct().Count()==values.Length,"Invalid or duplicate connector IDs in "+label+"."); }
            Unique((entry.ConnectorClassifications??[]).Select(c=>c.ConnectorId),"connectorClassifications");
            foreach(var change in entry.ConnectorClassifications??[])
            {
                var port=Port(ports,change.ConnectorId);
                if(port is null) continue;
                Require(Text(port,"domain")=="DomainHvac"&&change.Before!=change.After&&Text(port,"classification")==change.Before,"HVAC classification before reload differs from the contract or is unchanged.");
                Require(new[]{"SupplyAir","ReturnAir","ExhaustAir","OtherAir","UndefinedSystemType","Global"}.Contains(change.After),"Unsupported target HVAC classification.");
                port["classification"]=change.After;
            }
            Unique((entry.LogicalSystemReferences??[]).Select(c=>c.ConnectorId),"logicalSystemReferences");
            foreach(var reference in entry.LogicalSystemReferences??[])
            {
                var port=Port(ports,reference.ConnectorId); var target=Port(actualPorts,reference.ConnectorId);
                if(port is null||target is null) continue;
                Require(reference.SystemId>0&&port["systemId"]?.GetValue<long>()==reference.SystemId&&target["systemId"]?.GetValue<long>()==reference.SystemId,"Logical reference must belong to the same existing MEPSystem.");
                var left=port["allRefs"]!.AsArray().OfType<JsonObject>().Where(r=>Text(r,"connectorType")=="Logical"&&r["elementId"]!.GetValue<long>()==reference.SystemId).ToArray();
                var right=target["allRefs"]!.AsArray().OfType<JsonObject>().Where(r=>Text(r,"connectorType")=="Logical"&&r["elementId"]!.GetValue<long>()==reference.SystemId).ToArray();
                Require(left.Length==1&&right.Length==1,"Exactly one logical reference to the specified system must survive.");
                if(left.Length==1&&right.Length==1) left[0]["connectorId"]=right[0]["connectorId"]?.DeepClone();
            }
            Unique(entry.RemoveUnconnectedElectricalConnectors??[],"removeUnconnectedElectricalConnectors");
            foreach(var connectorId in entry.RemoveUnconnectedElectricalConnectors??[])
            {
                var port=Port(ports,connectorId);
                if(port is null) continue;
                Require(Text(port,"domain")=="DomainElectrical"&&port["isConnected"]?.GetValue<bool>()==false&&port["systemId"] is null&&port["allRefs"]!.AsArray().Count==0,"Only an unconnected electrical port without a system or references may be removed.");
                Require(!actualPorts.OfType<JsonObject>().Any(p=>p["connectorId"]!.GetValue<int>()==connectorId),"The expected electrical connector removal did not occur.");
                ports.Remove(port);
            }
            Require(FamilyReloadComparison.Equivalent(JsonSerializer.SerializeToElement(wanted),actual),"Unexpected instance fields changed; identity, category, placement, systems, connector geometry and physical references remain mandatory invariants.");
            matches.Add(new(entry.InstanceId,issues.Count==0,entry,issues.ToArray()));
        }
        return new(matches.ToArray(),matches.Where(m=>!m.Passed).SelectMany(m=>m.Issues.Select(issue=>$"Instance {m.InstanceId}: {issue}")).ToArray());
    }
}
