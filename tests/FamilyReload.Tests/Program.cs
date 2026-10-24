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
using var escaped=JsonDocument.Parse("\"\\u0410\""); using var literal=JsonDocument.Parse("\"А\"");
Check(FamilyReloadComparison.Equivalent(escaped.RootElement,literal.RootElement),"Unicode JSON escaping does not change string identity");

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

var contractBefore=before.DeepClone();
contractBefore["instances"]![0]!["categoryId"]=-2001140;
contractBefore["instances"]![0]!["nativeSystems"]=JsonNode.Parse("""[{"id":-1140324,"rawValue":"Supply 1","displayValue":"Supply 1"},{"id":-1140325,"rawValue":"SupplyAir","displayValue":"SupplyAir"}]""");
contractBefore["instances"]![0]!["connectors"]![0]!["domain"]="DomainHvac";
contractBefore["instances"]![0]!["connectors"]![0]!["classification"]="SupplyAir";
contractBefore["instances"]![0]!["connectors"]![0]!["allRefs"]!.AsArray().Add(JsonNode.Parse("""{"elementId":40000000,"connectorId":1,"connectorType":"Logical","physicalConnection":null}"""));
var oldNested=Nested(30000001,30000000); oldNested["categoryId"]=-2001140;oldNested["nativeSystems"]=JsonNode.Parse("""[{"id":-1140325,"rawValue":"Power","displayValue":"Power"}]""");
oldNested["connectors"]!.AsArray().Add(JsonNode.Parse("""{"connectorId":9,"domain":"DomainElectrical","isConnected":false,"systemId":null,"allRefs":[]}"""));
contractBefore["instances"]!.AsArray().Add(oldNested);
var contractAfter=contractBefore.DeepClone();
contractAfter["instances"]![0]!["nativeSystems"]![1]!["rawValue"]="UndefinedSystemType";
contractAfter["instances"]![0]!["nativeSystems"]![1]!["displayValue"]="UndefinedSystemType";
contractAfter["instances"]![0]!["connectors"]![0]!["classification"]="UndefinedSystemType";
contractAfter["instances"]![0]!["connectors"]![0]!["allRefs"]![1]!["connectorId"]=99;
contractAfter["instances"]![1]!["family"]="Prepared";contractAfter["instances"]![1]!["typeId"]=80;contractAfter["instances"]![1]!["type"]="A";
contractBefore["instances"]![1]!["type"]="A";
contractAfter["instances"]![1]!["connectors"]!.AsArray().Clear();
contractAfter["instances"]![1]!["nativeSystems"]![0]!["rawValue"]="";contractAfter["instances"]![1]!["nativeSystems"]![0]!["displayValue"]="";
var contracts=JsonNode.Parse("""
[{"instanceId":30000000,"uniqueId":"instance-a","systemClassification":{"before":"SupplyAir","after":"UndefinedSystemType"},
  "connectorClassifications":[{"connectorId":1,"before":"SupplyAir","after":"UndefinedSystemType"}],"logicalSystemReferences":[{"connectorId":1,"systemId":40000000}]},
 {"instanceId":30000001,"uniqueId":"nested-30000001","nestedType":{"before":{"family":"Nested","typeId":70,"type":"A"},"after":{"family":"Prepared","type":"A"}},"removeUnconnectedElectricalConnectors":[9],"systemClassification":{"before":"Power","after":""}}]
""")!;
FamilyReloadComparison.Report Contract(JsonNode? next=null,JsonNode? expected=null,JsonNode? previous=null)=>FamilyReloadComparison.Compare(Json(previous??contractBefore),Json(next??contractAfter),Json(expected??contracts));
Check(!FamilyReloadComparison.Preserved(Json(contractBefore),Json(contractAfter)),"intentional changes still fail without an explicit contract");
var allowed=Contract();
Check(allowed.Passed&&allowed.ChangedInstances.Length==2&&allowed.ExpectedChanges.All(e=>e.Passed)&&allowed.UnexpectedChangedInstanceIds.Length==0,"exact contracts allow changes and retain the complete native diff");
void RejectContract(Action<JsonNode> edit,string label)
{ var next=contractAfter.DeepClone();edit(next);Check(!Contract(next).Passed,label); }
void RejectExpected(Action<JsonNode> edit,string label)
{ var expected=contracts.DeepClone();edit(expected);Check(!Contract(expected:expected).Passed,label); }
RejectExpected(n=>n.AsArray().RemoveAt(1),"an unlisted nested change is rejected");
RejectExpected(n=>n[0]!["uniqueId"]="stale","stale contract UniqueId is rejected");
RejectExpected(n=>n[0]!["connectorClassifications"]![0]!["before"]="OtherAir","stale connector classification before value is rejected");
RejectExpected(n=>n[0]!["systemClassification"]!["after"]="OtherAir","incorrect native classification after value is rejected");
RejectExpected(n=>n[1]!["nestedType"]!["after"]!["family"]="Wrong target","incorrect nested target is rejected");
RejectExpected(n=>n.AsArray().Add(n[0]!.DeepClone()),"duplicate instance contracts are rejected");
RejectExpected(n=>n[0]!["ignorePlacement"]=true,"unknown permissive contract fields are rejected");
RejectExpected(n=>n[0]!["nestedType"]=n[1]!["nestedType"]!.DeepClone(),"root type replacement cannot be authorized");
RejectContract(n=>n["instances"]![0]!["typeId"]=20000001,"root symbol ID remains immutable");
RejectContract(n=>n["types"]![0]!["uniqueId"]="replacement","root symbol UniqueId remains immutable");
RejectContract(n=>n["instances"]![0]!["transform"]!["origin"]!["x"]=99,"expected changes never authorize moving an instance");
RejectContract(n=>n["instances"]![1]!["categoryId"]=-999,"nested category must be preserved");
RejectContract(n=>n["instances"]![1]!["superComponentId"]=99,"nested parent must be preserved");
RejectContract(n=>n["instances"]![0]!["nativeSystems"]![0]!["rawValue"]="Other system","system name remains immutable");
RejectContract(n=>n["instances"]![0]!["connectors"]![0]!["systemId"]=99,"system ID remains immutable");
RejectContract(n=>n["instances"]![0]!["connectors"]![0]!["widthMeters"]=.7,"connector dimensions remain immutable");
RejectContract(n=>n["instances"]![0]!["connectors"]![0]!["allRefs"]![0]!["elementId"]=99,"physical connection owner remains immutable");
RejectContract(n=>n["instances"]![0]!["connectors"]![0]!["allRefs"]![0]!["connectorId"]=99,"physical reference port ID remains immutable");
RejectContract(n=>n["instances"]![0]!["connectors"]![0]!["allRefs"]!.AsArray().RemoveAt(0),"physical reference cannot disappear");
RejectContract(n=>n["instances"]![0]!["connectors"]![0]!["allRefs"]![1]!["elementId"]=99,"logical reference cannot move to another system");
RejectContract(n=>n["instances"]![0]!["connectors"]![0]!["allRefs"]!.AsArray().RemoveAt(1),"logical reference count must be preserved");
RejectContract(n=>n["instances"]!.AsArray().RemoveAt(1),"a contracted existing instance cannot disappear");
var alternateIds=contractAfter.DeepClone();alternateIds["instances"]![1]!["typeId"]=8000;
Check(Contract(alternateIds).Passed,"apply may receive different newly allocated symbol IDs for the exact target family/type");
var extraInstance=contractAfter.DeepClone();extraInstance["instances"]!.AsArray().Add(Nested(30000002,30000000));
Check(!Contract(extraInstance).Passed,"explicit existing-instance contracts also preserve instance counts");
var connectedBefore=contractBefore.DeepClone();connectedBefore["instances"]![1]!["connectors"]![0]!["isConnected"]=true;
Check(!Contract(previous:connectedBefore).Passed,"connected electrical ports cannot be removed by a contract");
var referencedBefore=contractBefore.DeepClone();referencedBefore["instances"]![1]!["connectors"]![0]!["allRefs"]!.AsArray().Add(JsonNode.Parse("""{"elementId":99,"connectorId":1}"""));
Check(!Contract(previous:referencedBefore).Passed,"electrical ports with references cannot be removed even if marked unconnected");
var hvacBefore=contractBefore.DeepClone();hvacBefore["instances"]![1]!["connectors"]![0]!["domain"]="DomainHvac";
Check(!Contract(previous:hvacBefore).Passed,"the removal exception is restricted to electrical ports");
var notRemoved=contractAfter.DeepClone();notRemoved["instances"]![1]!["connectors"]!.AsArray().Add(contractBefore["instances"]![1]!["connectors"]![0]!.DeepClone());
Check(!Contract(notRemoved).Passed,"expected electrical port deletion must actually occur");

// Optional replay reads private diagnostics in place; no project data is copied into the repository.
JsonElement? replayExpected=null;
for(var argument=0;argument<args.Length;argument++)
{
    if(args[argument]=="--expected")
    { using var request=JsonDocument.Parse(File.ReadAllText(args[++argument]));replayExpected=request.RootElement.GetProperty("expectedChanges").Clone();continue; }
    var file=args[argument];
    using var document=JsonDocument.Parse(File.ReadAllText(file));
    var text=document.RootElement.TryGetProperty("content",out var content) ? content.EnumerateArray().First(c=>c.GetProperty("type").GetString()=="text").GetProperty("text").GetString()! : document.RootElement.GetRawText();
    using var error=JsonDocument.Parse(text);
    var diagnostic=error.RootElement.GetProperty("diagnostics");
    var report=FamilyReloadComparison.Compare(diagnostic.GetProperty("before"),diagnostic.GetProperty("after"),replayExpected);
    if(!report.Passed) Console.WriteLine(JsonSerializer.Serialize(new {report.Issues,report.UnexpectedChangedInstanceIds}));
    Check(report.Passed,$"supplied diagnostic replay: {report.FamilyIdChanges.Length} FamilyId remappings, {report.AddedInstances.Length} additions");
}
Console.WriteLine($"PASS: {checks} family reload comparison checks");
