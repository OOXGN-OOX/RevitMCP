using System.Text.Json;
using System.Text.Json.Nodes;
using RevitMCP;

var checks=0;
var input=JsonNode.Parse("""{"points":[{"xMeters":0,"yMeters":0,"zMeters":0},{"xMeters":0.3,"yMeters":0.4,"zMeters":0},{"xMeters":0.8,"yMeters":0.4,"zMeters":0}]}""")!;
MepFlexPath Read(JsonNode value)=>MepFlexPath.Read(JsonSerializer.SerializeToElement(value));
void Check(bool passed,string name) { if(!passed) throw new Exception(name);checks++;Console.WriteLine("PASS "+name); }
void Reject(Action<JsonNode> edit,string name)
{
    var value=input.DeepClone();edit(value);
    try {Read(value);} catch(ArgumentException) {checks++;Console.WriteLine("PASS "+name);return;}
    throw new Exception(name);
}
var route=Read(input);
Check(route.Points.Length==3&&Math.Abs(route.ControlPolylineLengthMeters-1)<1e-9,"spatial controls retain order and polyline length");
Check(route.StartTangent is null&&route.EndTangent is null,"omitted tangents retain native automatic directions");
var start=input.DeepClone();start["startTangent"]=JsonNode.Parse("""{"x":0,"y":0,"z":1}""");
Check(Read(start).StartTangent?.Z==1&&Read(start).EndTangent is null,"one end may have an explicit tangent");
start["endTangent"]=JsonNode.Parse("""{"x":-1,"y":0,"z":0}""");
Check(Read(start).EndTangent?.X==-1,"negative tangent directions are retained");
var minimum=JsonNode.Parse("""{"points":[{"xMeters":0,"yMeters":0,"zMeters":0},{"xMeters":0.01,"yMeters":0,"zMeters":0}]}""")!;
Check(Read(minimum).ControlPolylineLengthMeters==.01,"minimum length two-point path is valid");
var longest=minimum.DeepClone();longest["points"]![1]!["xMeters"]=300;
Check(Read(longest).ControlPolylineLengthMeters==300,"maximum control-polyline length is valid");
Reject(n=>n["points"]=new JsonArray(),"empty route is rejected");
Reject(n=>{n["points"]!.AsArray().RemoveAt(2);n["points"]!.AsArray().RemoveAt(1);},"one point cannot define a flex pipe");
Reject(n=>n["points"]![2]=n["points"]![0]!.DeepClone(),"closed paths and repeated nonadjacent points are rejected");
Reject(n=>n["points"]![1]=n["points"]![0]!.DeepClone(),"duplicate adjacent points are rejected");
Reject(n=>n["points"]![1]!["xMeters"]=100001,"coordinates outside the supported model extent are rejected");
Reject(n=>{n["points"]![1]!["xMeters"]=.001;n["points"]![1]!["yMeters"]=0;},"short adjacent spans are rejected");
Reject(n=>n["points"]![1]!["xMeters"]=500,"control polyline longer than 300 m is rejected");
Reject(n=>n["points"]![1]!["zMeters"]="NaN","nonnumeric coordinates are rejected");
Reject(n=>n["points"]![1]=null,"null route points are rejected");
Reject(n=>n["startTangent"]=JsonNode.Parse("""{"x":0,"y":0,"z":0}"""),"zero tangents are rejected rather than silently ignored");
Reject(n=>n["endTangent"]=JsonNode.Parse("""{"x":1000001,"y":0,"z":0}"""),"unbounded tangent components are rejected");
Reject(n=>n["endTangent"]=null,"null tangents are rejected");
Reject(n=>{for(var i=0;i<100;i++)n["points"]!.AsArray().Add(new JsonObject{{"xMeters",1+i},{"yMeters",1},{"zMeters",1}});},"point count is bounded");
Console.WriteLine($"PASS: {checks} flexible pipe path checks");
