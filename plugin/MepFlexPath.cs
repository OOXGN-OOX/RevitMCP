using System.Text.Json;

namespace RevitMCP;

internal sealed record MepFlexPath(MepFlexPath.Vector[] Points,MepFlexPath.Vector? StartTangent,MepFlexPath.Vector? EndTangent,double ControlPolylineLengthMeters)
{
    internal sealed record Vector(double X,double Y,double Z)
    {
        internal double Length=>Math.Sqrt(X*X+Y*Y+Z*Z);
        internal double Distance(Vector other)=>new Vector(X-other.X,Y-other.Y,Z-other.Z).Length;
    }
    internal static MepFlexPath Read(JsonElement request)
    {
        Vector ReadVector(JsonElement value,bool point)
        {
            if(value.ValueKind!=JsonValueKind.Object) throw new ArgumentException("A flex pipe point or tangent must be an object.");
            double Read(string key)
            {
                if(!value.TryGetProperty(key,out var raw)||raw.ValueKind!=JsonValueKind.Number||!raw.TryGetDouble(out var number)
                    ||!double.IsFinite(number)||Math.Abs(number)>(point ? 100000 : 1000000)) throw new ArgumentException("Invalid flex pipe coordinate or tangent component.");
                return number;
            }
            return point ? new(Read("xMeters"),Read("yMeters"),Read("zMeters")) : new(Read("x"),Read("y"),Read("z"));
        }
        if(!request.TryGetProperty("points",out var points)||points.ValueKind!=JsonValueKind.Array||points.GetArrayLength() is <2 or >100)
            throw new ArgumentException("Flex pipe needs 2 to 100 path points.");
        var vertices=points.EnumerateArray().Select(p=>ReadVector(p,true)).ToArray();
        var length=0d;
        for(var i=0;i<vertices.Length;i++)
        {
            for(var j=0;j<i;j++) if(vertices[i].Distance(vertices[j])<0.000001) throw new ArgumentException("Flex pipe path must not repeat points.");
            if(i==0) continue;
            var distance=vertices[i].Distance(vertices[i-1]);
            if(distance<0.01-1e-9) throw new ArgumentException("Adjacent flex pipe points must be at least 0.01 m apart.");
            length+=distance;
        }
        if(length>300+1e-9||vertices[0].Distance(vertices[^1])<0.01-1e-9)
            throw new ArgumentException("Flex pipe endpoints must be distinct and its control polyline must not exceed 300 m.");
        Vector? Tangent(string name)
        {
            if(!request.TryGetProperty(name,out var value)) return null;
            var result=ReadVector(value,false);
            return result.Length>1e-9 ? result : throw new ArgumentException("Flex pipe tangent must be nonzero.");
        }
        return new(vertices,Tangent("startTangent"),Tangent("endTangent"),length);
    }
}
