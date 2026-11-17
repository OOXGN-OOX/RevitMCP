using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCP;
using System.Text.Json;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
void Near(double value, double expected, string message) => Check(Math.Abs(value-expected)<1e-9, message);
void Reject(Action action, string message) { try { action(); } catch (ArgumentException) { checks++; return; } catch (InvalidOperationException) { checks++; return; } throw new Exception(message); }
Document Doc(string title) => new() { Title=title, ProjectInformation=new Element { UniqueId=title+"-key" } };
Room Room(long id, string number, double area=100) => new() { Id=new(id), Name=number+" "+number, ParameterName=number, UniqueId="room-"+id, Number=number, LevelId=new(5), Area=area,
    Perimeter=40, Volume=1000, Loops=area==0?null:new List<IList<BoundarySegment>> { new List<BoundarySegment> {
        new() { ElementId=new(200), Curve=new() { Points=[new(1,2,0),new(3,2,0),new(3,4,1)] } } } } };
var host=Doc("host"); var linked=Doc("linked");
host.Elements.AddRange([new Element { Id=new(5), Name="Host level" },Room(10,"H")]);
linked.Elements.AddRange([new Element { Id=new(5), Name="Linked level" },Room(10,"L"),Room(20,"Unplaced",0)]);
host.Elements.AddRange([
    new RevitLinkInstance { Id=new(100), Name="rotated", LinkedDocument=linked,
        Placement=new() { Origin=new(10,20,30), BasisX=new(0,1,0), BasisY=new(-1,0,0) } },
    new RevitLinkInstance { Id=new(101), Name="mirrored", LinkedDocument=linked,
        Placement=new() { Origin=new(-10,0,0), BasisX=new(-1,0,0) } },
    new RevitLinkInstance { Id=new(102), Name="unloaded" }
]);
var app=new UIApplication { Document=host };
var original=RevitTools.ReadRooms(app,new { });
Check(original.GetProperty("matchingCount").GetInt32()==1,"Default remains host-only");
Check(original.GetProperty("rooms")[0].GetProperty("linkInstanceId").ValueKind==JsonValueKind.Null,"Host IDs have no link");
var one=RevitTools.ReadRooms(app,new { linkInstanceId=100 });
Check(one.GetProperty("matchingCount").GetInt32()==2,"Read rooms from link document");
var room=one.GetProperty("rooms")[0];
Check(room.GetProperty("number").GetString()=="L","Colliding room IDs resolve inside the selected document");
Check(room.GetProperty("name").GetString()=="L","Read name parameter without appending the display number");
Check(room.GetProperty("uniqueId").GetString()=="room-10","Return stable source Room identity");
Check(room.GetProperty("level").GetString()=="Linked level","Resolve levels in link document");
Check(room.GetProperty("linkInstanceId").GetInt64()==100,"Return instance identity");
Near(room.GetProperty("areaSquareMeters").GetDouble(),9.290304,"Convert area to square metres");
Near(room.GetProperty("perimeterMillimeters").GetDouble(),12192,"Keep perimeter units");
var all=RevitTools.ReadRooms(app,new { includeLinks=true });
Check(all.GetProperty("matchingCount").GetInt32()==5,"Keep distinct instances of the same document");
Check(all.GetProperty("links").GetArrayLength()==3,"Report every direct link");
Check(!all.GetProperty("links")[2].GetProperty("isLoaded").GetBoolean(),"Report unloaded links explicitly");
var page=RevitTools.ReadRooms(app,new { includeLinks=true, offset=2, limit=2 });
Check(page.GetProperty("rooms")[0].GetProperty("id").GetInt64()==20,"Page across combined sources");
Check(page.GetProperty("rooms")[1].GetProperty("linkInstanceId").GetInt64()==101,"Page can cross an instance boundary");
Check(page.GetProperty("hasMore").GetBoolean(),"Combined pagination has more");
Check(RevitTools.ReadRooms(app,new { includeLinks=true, levelName="linked LEVEL" }).GetProperty("matchingCount").GetInt32()==4,"Filter levels inside each document ignoring case");
Check(RevitTools.ReadRooms(app,new { includeLinks=true, offset=20 }).GetProperty("returnedCount").GetInt32()==0,"Empty page beyond end");
Reject(()=>RevitTools.ReadRooms(app,new { linkInstanceId=10 }),"Reject non-link IDs");
Reject(()=>RevitTools.ReadRooms(app,new { linkInstanceId=102 }),"Explicit unloaded link must fail");
Reject(()=>RevitTools.ReadRooms(app,new { includeLinks=true, linkInstanceId=100 }),"Reject ambiguous selector");
var rotated=RevitTools.ReadBoundaries(app,new { linkInstanceId=100, elementIds=new[] {10} });
var segment=rotated.GetProperty("rooms")[0].GetProperty("loops")[0][0];
Check(segment.GetProperty("elementId").GetInt64()==200,"Boundary IDs retain source document identity");
Check(segment.GetProperty("points").GetArrayLength()==3,"Transform every tessellated point");
var p=segment.GetProperty("points")[0];
Near(p.GetProperty("x").GetDouble(),8*.3048,"Rotation and translation X");
Near(p.GetProperty("y").GetDouble(),21*.3048,"Rotation and translation Y");
Near(p.GetProperty("z").GetDouble(),30*.3048,"Translation Z");
var mirrored=RevitTools.ReadBoundaries(app,new { linkInstanceId=101, elementIds=new[] {10} });
p=mirrored.GetProperty("rooms")[0].GetProperty("loops")[0][0].GetProperty("points")[0];
Near(p.GetProperty("x").GetDouble(),-11*.3048,"Reflect X before conversion to metres");
Near(p.GetProperty("y").GetDouble(),2*.3048,"Preserve Y under reflection");
var unchanged=RevitTools.ReadBoundaries(app,new { elementIds=new[] {10} });
Near(unchanged.GetProperty("rooms")[0].GetProperty("loops")[0][0].GetProperty("points")[0].GetProperty("x").GetDouble(),.3048,"Host coordinates remain unchanged");
var unplaced=RevitTools.ReadBoundaries(app,new { linkInstanceId=100, elementIds=new[] {20} }).GetProperty("rooms")[0];
Check(!unplaced.GetProperty("closed").GetBoolean() && unplaced.GetProperty("loops").ValueKind==JsonValueKind.Null,"Unplaced rooms preserve empty boundaries");
Reject(()=>RevitTools.ReadBoundaries(app,new { linkInstanceId=100, elementIds=new[] {5} }),"Reject non-room IDs in link document");
Console.WriteLine($"PASS: {checks} linked room reader checks");
