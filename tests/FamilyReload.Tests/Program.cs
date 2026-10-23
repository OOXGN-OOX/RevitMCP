using System.Text.Json;
using System.Text.Json.Nodes;
using RevitMCP;

var before=JsonNode.Parse("""
{"familyId":10000000,"family":"Fixture","types":[{"id":20000000,"name":"A"}],"instances":[
  {"instanceId":30000000,"typeId":20000000,"transform":{"origin":{"x":1.2345,"y":0,"z":0}},"nativeSystems":[{"rawValue":"Supply 1"}],
  "connectors":[{"connectorId":1,"systemId":40000000,"allRefs":[{"elementId":50000000,"connectorId":0,"physicalConnection":true}]}]}]}
""")!;
var checks=0;
void Check(bool condition,string name) { if(!condition) throw new Exception(name); checks++; Console.WriteLine("PASS "+name); }
JsonElement Json(JsonNode n)=>JsonSerializer.SerializeToElement(n);
void Reject(Action<JsonNode> edit,string name)
{
    var after=before.DeepClone(); edit(after);
    Check(!FamilyReloadComparison.Preserved(Json(before),Json(after)),name);
}
Check(FamilyReloadComparison.Preserved(Json(before),Json(before)),"unchanged connected family passes");
var added=before.DeepClone();added["types"]!.AsArray().Add(new JsonObject{{"id",2},{"name","New"}});
Check(FamilyReloadComparison.Preserved(Json(before),Json(added)),"adding an unplaced type preserves existing identities");
Reject(n=>n["familyId"]=10000001,"nearby family IDs must not compare equal");
Reject(n=>n["types"]![0]!["id"]=20000001,"type replacement is detected");
Reject(n=>n["types"]!.AsArray().Clear(),"missing existing type is detected");
Reject(n=>n["instances"]![0]!["instanceId"]=30000001,"instance replacement is detected");
Reject(n=>n["instances"]![0]!["transform"]!["origin"]!["x"]=1.4,"placement change is detected");
Reject(n=>n["instances"]![0]!["nativeSystems"]![0]!["rawValue"]="","lost native system is detected");
Reject(n=>n["instances"]![0]!["connectors"]![0]!["systemId"]=40000001,"nearby system IDs must not compare equal");
Reject(n=>n["instances"]![0]!["connectors"]![0]!["allRefs"]!.AsArray().Clear(),"lost external connector link is detected");
Reject(n=>n["instances"]![0]!["connectors"]![0]!["allRefs"]![0]!["elementId"]=50000001,"rewired external connector is detected");
Reject(n=>n["instances"]!.AsArray().Clear(),"deleted instances are detected");
var roundoff=before.DeepClone();roundoff["instances"]![0]!["transform"]!["origin"]!["x"]=1.23450000001;
Check(FamilyReloadComparison.Preserved(Json(before),Json(roundoff)),"floating point regeneration roundoff is tolerated");
Check(!FamilyReloadComparison.Equivalent(JsonSerializer.SerializeToElement(9007199254740992L),JsonSerializer.SerializeToElement(9007199254740993L)),"integer comparison does not round large IDs");
Console.WriteLine($"PASS: {checks} family reload comparison checks");
