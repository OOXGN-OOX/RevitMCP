using System.Text.Json;
using System.Text.Json.Nodes;
using RevitMCP;

var before=JsonNode.Parse("""
{"familyId":10000000,"family":"Fixture","types":[{"id":20000000,"uniqueId":"symbol-a","name":"A"}],"instances":[
  {"instanceId":30000000,"uniqueId":"instance-a","familyId":10000000,"family":"Fixture","typeId":20000000,"superComponentId":null,"transform":{"origin":{"x":1.2345,"y":0,"z":0}},"nativeSystems":[{"rawValue":"Supply 1"}],
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
var remapped=before.DeepClone(); remapped["familyId"]=11000000;remapped["instances"]![0]!["familyId"]=11000000;
var remapReport=FamilyReloadComparison.Compare(Json(before),Json(remapped));
Check(remapReport.Passed&&remapReport.FamilyIdChanges.Length==2,"service FamilyId remapping is reported without rejecting stable symbols and instances");
Reject(n=>n["family"]="Different family","a different family name is rejected despite stable IDs");
Reject(n=>n["types"]![0]!["id"]=20000001,"type replacement is detected");
Reject(n=>n["types"]!.AsArray().Clear(),"missing existing type is detected");
Reject(n=>n["instances"]![0]!["instanceId"]=30000001,"instance replacement is detected");
Reject(n=>n["instances"]![0]!["uniqueId"]="replacement-uid","instance UniqueId replacement is detected");
Reject(n=>n["types"]![0]!["uniqueId"]="replacement-symbol","symbol UniqueId replacement is detected");
Reject(n=>n["instances"]![0]!["transform"]!["origin"]!["x"]=1.4,"placement change is detected");
Reject(n=>n["instances"]![0]!["nativeSystems"]![0]!["rawValue"]="","lost native system is detected");
Reject(n=>n["instances"]![0]!["connectors"]![0]!["systemId"]=40000001,"nearby system IDs must not compare equal");
Reject(n=>n["instances"]![0]!["connectors"]![0]!["allRefs"]!.AsArray().Clear(),"lost external connector link is detected");
Reject(n=>n["instances"]![0]!["connectors"]![0]!["allRefs"]![0]!["elementId"]=50000001,"rewired external connector is detected");
Reject(n=>n["instances"]!.AsArray().Clear(),"deleted instances are detected");
var roundoff=before.DeepClone();roundoff["instances"]![0]!["transform"]!["origin"]!["x"]=1.23450000001;
Check(FamilyReloadComparison.Preserved(Json(before),Json(roundoff)),"floating point regeneration roundoff is tolerated");
Check(!FamilyReloadComparison.Equivalent(JsonSerializer.SerializeToElement(9007199254740992L),JsonSerializer.SerializeToElement(9007199254740993L)),"integer comparison does not round large IDs");

JsonObject Nested(long id,long? parent)=>new(){{"instanceId",id},{"uniqueId","nested-"+id},{"familyId",60},{"family","Nested"},{"typeId",70},{"superComponentId",parent},{"connectors",new JsonArray()}};
var nested=remapped.DeepClone();for(var i=1;i<=7;i++) nested["instances"]!.AsArray().Add(Nested(30000000+i,30000000));
var nestedReport=FamilyReloadComparison.Compare(Json(before),Json(nested));
Check(nestedReport.Passed&&nestedReport.AddedInstances.Length==7&&nestedReport.ChangedInstances.Length==0,"seven new shared nested components are additions, not existing instance changes");
nested["instances"]!.AsArray().Add(Nested(30000008,30000001));
Check(FamilyReloadComparison.Preserved(Json(before),Json(nested)),"new nested descendants may themselves contain new nested instances");
var reordered=nested.DeepClone();reordered["instances"]=new JsonArray(reordered["instances"]!.AsArray().Reverse().Select(n=>n!.DeepClone()).ToArray());
Check(FamilyReloadComparison.Preserved(Json(before),Json(reordered)),"instance enumeration order is irrelevant");
var badRoot=before.DeepClone();badRoot["instances"]!.AsArray().Add(Nested(30000001,null));
var badReport=FamilyReloadComparison.Compare(Json(before),Json(badRoot));
Check(!badReport.Passed&&badReport.UnexpectedAddedInstanceIds.SequenceEqual(new[]{30000001L}),"unrelated root additions are rejected and identified");
var dangling=before.DeepClone();dangling["instances"]!.AsArray().Add(Nested(30000001,999));
Check(!FamilyReloadComparison.Preserved(Json(before),Json(dangling)),"added nested component with a missing parent is rejected");
var cycle=before.DeepClone();cycle["instances"]!.AsArray().Add(Nested(30000001,30000002));cycle["instances"]!.AsArray().Add(Nested(30000002,30000001));
Check(!FamilyReloadComparison.Preserved(Json(before),Json(cycle)),"added parent cycles are rejected");
var movedWithAdditions=nested.DeepClone();movedWithAdditions["instances"]![0]!["transform"]!["origin"]!["x"]=9;
var movedReport=FamilyReloadComparison.Compare(Json(before),Json(movedWithAdditions));
Check(!movedReport.Passed&&movedReport.ChangedInstances.Single().Fields.Contains("transform"),"new components do not mask an existing placement change");
var withOldNested=before.DeepClone();withOldNested["instances"]!.AsArray().Add(Nested(30000001,30000000));
var removedOld=FamilyReloadComparison.Compare(Json(withOldNested),Json(before));
Check(!removedOld.Passed&&removedOld.RemovedInstances.Single().GetProperty("instanceId").GetInt64()==30000001,"removing a pre-existing nested instance is explicitly rejected");
var changedOld=withOldNested.DeepClone();changedOld["instances"]![1]!["typeId"]=71;
Check(!FamilyReloadComparison.Preserved(Json(withOldNested),Json(changedOld)),"changing a pre-existing nested type is rejected");
var duplicate=before.DeepClone();duplicate["instances"]!.AsArray().Add(duplicate["instances"]![0]!.DeepClone());
Check(!FamilyReloadComparison.Preserved(Json(before),Json(duplicate)),"duplicate instance IDs cannot mask replacement");
var outerNested=before.DeepClone();outerNested["instances"]![0]!["superComponentId"]=999;
var outerWithAddition=outerNested.DeepClone();outerWithAddition["instances"]!.AsArray().Add(Nested(30000001,30000000));
Check(FamilyReloadComparison.Preserved(Json(outerNested),Json(outerWithAddition)),"a selected shared family nested under an unaudited parent is an audit subtree root");

// Optional replay reads private diagnostics in place; no project data is copied into the repository.
foreach(var file in args)
{
    using var document=JsonDocument.Parse(File.ReadAllText(file));
    var text=document.RootElement.GetProperty("content").EnumerateArray().First(c=>c.GetProperty("type").GetString()=="text").GetProperty("text").GetString()!;
    using var error=JsonDocument.Parse(text);
    var diagnostic=error.RootElement.GetProperty("diagnostics");
    var report=FamilyReloadComparison.Compare(diagnostic.GetProperty("before"),diagnostic.GetProperty("after"));
    Check(report.Passed,$"supplied diagnostic replay: {report.FamilyIdChanges.Length} FamilyId remappings, {report.AddedInstances.Length} additions");
}
Console.WriteLine($"PASS: {checks} family reload comparison checks");
