using System.Text.Json;
using RevitMCP;

var checks=0;
ParameterValueInput Read(string json) { using var document=JsonDocument.Parse(json);return ParameterValueInput.Read(document.RootElement); }
void Check(bool valid,string message) { if(!valid) throw new Exception(message);checks++;Console.WriteLine("PASS "+message); }
void Reject(string json,bool reference=false)
{
    try { var value=Read(json);if(reference) value.ReferenceId(); }
    catch(ArgumentException) { checks++;return; }
    throw new Exception("Invalid parameter input accepted: "+json);
}
Check(Read("""{"elementIdValue":123}""").ReferenceId()==123,"typed IDs use the ElementId path");
Check(Read("""{"value":" 123 "}""").ReferenceId()==123,"existing string input also accepts exact reference IDs");
Check(Read("""{"elementIdValue":-1}""").ReferenceId()==-1,"InvalidElementId is preserved for native validation");
Check(Read("""{"elementIdValue":-2}""").ReferenceId()==-2,"negative Revit enum IDs remain available for native validation");
Check(Read("""{"elementIdValue":9007199254740991}""").ReferenceId()==9007199254740991,"safe numeric IDs retain all integer bits");
Check(Read("""{"value":"9223372036854775807"}""").ReferenceId()==long.MaxValue,"Int64 reference text does not pass through double precision");
Check(Read("""{"value":"230 В"}""").Text=="230 В","ordinary text parameters retain display text");
Check(Read("""{"value":""}""").Text=="","empty string values remain supported");
foreach(var json in new[]{"{}","""{"value":"1","elementIdValue":1}""","""{"elementIdValue":0}""","""{"elementIdValue":1.5}""","""{"elementIdValue":"1"}""","""{"elementIdValue":9007199254740992}""","""{"elementIdValue":null}""","""{"value":123}"""}) Reject(json);
foreach(var text in new[]{"0","1.5","1e3","230 В","1 000","9223372036854775808",""}) Reject(JsonSerializer.Serialize(new {value=text}),true);
Console.WriteLine($"PASS: {checks} parameter value checks");
