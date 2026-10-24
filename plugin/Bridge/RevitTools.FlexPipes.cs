using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static XYZ FlexDirection(MepFlexPath.Vector? vector)=>vector is null ? XYZ.Zero : new XYZ(vector.X,vector.Y,vector.Z).Normalize();
    private static object FlexPointInfo(XYZ point)=>new {xMeters=FMeters(point.X),yMeters=FMeters(point.Y),zMeters=FMeters(point.Z)};
    private static object FlexDirectionInfo(XYZ vector)=>new {x=vector.X,y=vector.Y,z=vector.Z};
    private static object CreateFlexPipe(Document doc,JsonElement request)
    {
        var route=MepFlexPath.Read(request);
        var typeId=new ElementId(HostedReadId(request,"typeId"));
        var systemId=new ElementId(HostedReadId(request,"systemTypeId"));
        var levelId=new ElementId(HostedReadId(request,"levelId"));
        if(doc.GetElement(typeId) is not FlexPipeType||doc.GetElement(systemId) is not PipingSystemType||doc.GetElement(levelId) is not Level)
            throw new ArgumentException("Flex pipe requires a FlexPipeType, PipingSystemType and Level from the current project.");
        var points=route.Points.Select(p=>new XYZ(BuildingFeet(p.X),BuildingFeet(p.Y),BuildingFeet(p.Z))).ToList();
        var pipe=route.StartTangent is null&&route.EndTangent is null ? FlexPipe.Create(doc,systemId,typeId,levelId,points)
            : FlexPipe.Create(doc,systemId,typeId,levelId,FlexDirection(route.StartTangent),FlexDirection(route.EndTangent),points);
        doc.Regenerate();
        var diameter=BuildingFeet(BuildingNumber(request,"diameterMeters",0.001,3));
        if(Math.Abs(pipe.Diameter-diameter)>BuildingFeet(0.000001))
        {
            var size=new[]{pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM),pipe.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)}
                .FirstOrDefault(p=>p is not null&&!p.IsReadOnly&&p.StorageType==StorageType.Double);
            if(size is null||!size.Set(diameter)) throw new InvalidOperationException("The flex pipe type has no writable diameter or Revit rejected the requested size.");
        }
        doc.Regenerate();
        return FlexPipeState(pipe,request);
    }
    private static object FlexPipeState(FlexPipe pipe,JsonElement request)
    {
        var route=MepFlexPath.Read(request);var points=pipe.Points.ToArray();
        var tolerance=BuildingFeet(0.0001);
        var ports=pipe.ConnectorManager.Connectors.Cast<Connector>().Where(PhysicalMepEnd).OrderBy(c=>c.Id).ToArray();
        void Require(bool passed,string message) { if(!passed) throw new RevitOperationException(message,new {request,elementId=pipe.Id.Value}); }
        Require(pipe.GetTypeId().Value==request.GetProperty("typeId").GetInt64(),"Revit changed the requested flexible pipe type.");
        Require(pipe.ReferenceLevel?.Id.Value==request.GetProperty("levelId").GetInt64(),"Revit changed the requested flex pipe level.");
        var system=pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
        Require(system?.StorageType==StorageType.ElementId&&system.AsElementId().Value==request.GetProperty("systemTypeId").GetInt64(),"Revit changed the requested piping system type.");
        Require(Math.Abs(FMeters(pipe.Diameter)-request.GetProperty("diameterMeters").GetDouble())<=0.00001,"Flex pipe diameter differs after regeneration or commit.");
        var desired=route.Points.Select(p=>new XYZ(BuildingFeet(p.X),BuildingFeet(p.Y),BuildingFeet(p.Z))).ToArray();
        Require(points.Length>=2&&points[0].DistanceTo(desired[0])<=tolerance&&points[^1].DistanceTo(desired[^1])<=tolerance,"Flex pipe endpoints differ from the requested path.");
        // Revit may insert its own control points; every supplied point must survive in order.
        var cursor=0;
        foreach(var point in desired)
        {
            while(cursor<points.Length&&points[cursor].DistanceTo(point)>tolerance) cursor++;
            Require(cursor<points.Length,"Revit did not retain the supplied flex pipe path points in order.");cursor++;
        }
        void Direction(MepFlexPath.Vector? expected,XYZ actual)
        { if(expected is not null) Require(actual.GetLength()>1e-9&&actual.Normalize().DotProduct(FlexDirection(expected))>=0.999999,"Revit did not retain the requested flex pipe tangent direction."); }
        Direction(route.StartTangent,pipe.StartTangent);Direction(route.EndTangent,pipe.EndTangent);
        Require(ports.Length==2&&ports.All(c=>c.Domain==Domain.DomainPiping&&c.Shape==ConnectorProfileType.Round&&!c.IsConnected),"A new flex pipe must expose two free round piping end connectors.");
        foreach(var end in new[]{desired[0],desired[^1]}) Require(ports.Any(c=>c.Origin.DistanceTo(end)<=tolerance),"A flex pipe connector is not at the requested endpoint.");
        Require(ports.All(c=>Math.Abs(FMeters(c.Radius*2)-request.GetProperty("diameterMeters").GetDouble())<=0.00001),"Flex pipe connector diameter differs from the requested size.");
        var length=pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
        double? lengthMeters=length?.StorageType==StorageType.Double ? FMeters(length.AsDouble()) : null;
        Require(lengthMeters.HasValue&&double.IsFinite(lengthMeters.Value)&&lengthMeters.Value>=0.01-1e-8&&lengthMeters.Value<=300+1e-8,"The native flex pipe length is unavailable or outside 0.01–300 m.");
        return new {id=pipe.Id.Value,uniqueId=pipe.UniqueId,kind="flexPipe",typeId=pipe.GetTypeId().Value,
            systemTypeId=system!.AsElementId().Value,levelId=pipe.ReferenceLevel!.Id.Value,diameterMeters=FMeters(pipe.Diameter),lengthMeters,
            controlPolylineLengthMeters=route.ControlPolylineLengthMeters,points=points.Select(FlexPointInfo).ToArray(),
            startTangent=FlexDirectionInfo(pipe.StartTangent),endTangent=FlexDirectionInfo(pipe.EndTangent),
            connectors=ports.Select(MepConnectorInfo).ToArray()};
    }
    private static JsonElement ValidateCreatedMepAfterCommit(Document doc,JsonElement args,JsonElement result)
    {
        var requests=args.GetProperty("segments").EnumerateArray().ToArray();
        var row=JsonNode.Parse(result.GetRawText())!.AsObject();var segments=row["segments"]!.AsArray();
        for(var i=0;i<requests.Length;i++)
        {
            if(JsonTools.GetString(requests[i],"kind")!="flexPipe") continue;
            var id=segments[i]!["id"]!.GetValue<long>();
            if(doc.GetElement(new ElementId(id)) is not FlexPipe pipe) throw new InvalidOperationException("The created flex pipe disappeared during commit.");
            segments[i]=JsonSerializer.SerializeToNode(FlexPipeState(pipe,requests[i]),JsonTools.SerializerOptions);
        }
        return JsonSerializer.SerializeToElement(row);
    }
}
