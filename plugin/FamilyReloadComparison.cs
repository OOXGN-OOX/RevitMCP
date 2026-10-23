using System.Text.Json;

namespace RevitMCP;

internal static class FamilyReloadComparison
{
    internal static bool Preserved(JsonElement before,JsonElement after)
    {
        var types=after.GetProperty("types").EnumerateArray().ToArray();
        return before.GetProperty("types").EnumerateArray().All(t=>types.Any(a=>Equivalent(t,a)))
            && Equivalent(before.GetProperty("familyId"),after.GetProperty("familyId"))
            && Equivalent(before.GetProperty("family"),after.GetProperty("family"))
            && Equivalent(before.GetProperty("instances"),after.GetProperty("instances"));
    }

    internal static bool Equivalent(JsonElement a,JsonElement b)
    {
        if(a.ValueKind!=b.ValueKind) return false;
        if(a.ValueKind==JsonValueKind.Number)
        {
            // Preserve integer IDs exactly, including values above double's exact integer range.
            if(a.TryGetInt64(out var left) && b.TryGetInt64(out var right)) return left==right;
            return Math.Abs(a.GetDouble()-b.GetDouble())<=1e-8*Math.Max(1,Math.Abs(a.GetDouble()));
        }
        if(a.ValueKind==JsonValueKind.Array) return a.GetArrayLength()==b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p=>Equivalent(p.First,p.Second));
        if(a.ValueKind==JsonValueKind.Object) return a.EnumerateObject().Count()==b.EnumerateObject().Count() && a.EnumerateObject().All(p=>b.TryGetProperty(p.Name,out var v)&&Equivalent(p.Value,v));
        return a.GetRawText()==b.GetRawText();
    }
}
