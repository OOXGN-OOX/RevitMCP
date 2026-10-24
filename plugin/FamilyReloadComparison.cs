using System.Text.Json;

namespace RevitMCP;

internal static class FamilyReloadComparison
{
    internal sealed record FamilyIdChange(long? InstanceId,long Before,long After);
    internal sealed record InstanceChange(long InstanceId,string[] Fields,JsonElement Before,JsonElement After);
    internal sealed record TypeChange(long TypeId,string[] Fields,JsonElement Before,JsonElement After);
    internal sealed record Report(bool Passed,FamilyIdChange[] FamilyIdChanges,JsonElement[] AddedInstances,
        JsonElement[] RemovedInstances,InstanceChange[] ChangedInstances,JsonElement[] AddedTypes,
        JsonElement[] RemovedTypes,TypeChange[] ChangedTypes,long[] UnexpectedAddedInstanceIds,string[] Issues);

    internal static bool Preserved(JsonElement before,JsonElement after) => Compare(before,after).Passed;

    internal static Report Compare(JsonElement before,JsonElement after)
    {
        var issues=new List<string>();
        Dictionary<long,JsonElement> Index(JsonElement state,string list,string key)
        {
            var result=new Dictionary<long,JsonElement>();
            foreach(var row in state.GetProperty(list).EnumerateArray())
                if(!result.TryAdd(row.GetProperty(key).GetInt64(),row)) issues.Add($"Duplicate {key} in {list}.");
            var uniqueIds=result.Values.Where(r=>r.TryGetProperty("uniqueId",out var u)&&u.ValueKind==JsonValueKind.String).Select(r=>r.GetProperty("uniqueId").GetString()).ToArray();
            if(uniqueIds.Distinct(StringComparer.Ordinal).Count()!=uniqueIds.Length) issues.Add($"Duplicate UniqueId in {list}.");
            return result;
        }
        var oldTypes=Index(before,"types","id"); var newTypes=Index(after,"types","id");
        var oldInstances=Index(before,"instances","instanceId"); var newInstances=Index(after,"instances","instanceId");
        if(!Equivalent(before.GetProperty("family"),after.GetProperty("family"))) issues.Add("Family name changed.");
        var familyIds=new List<FamilyIdChange>();
        void FamilyIdChanged(JsonElement a,JsonElement b,long? instanceId)
        {
            if(a.TryGetProperty("familyId",out var left)&&b.TryGetProperty("familyId",out var right)&&left.GetInt64()!=right.GetInt64())
                familyIds.Add(new(instanceId,left.GetInt64(),right.GetInt64()));
        }
        FamilyIdChanged(before,after,null);
        foreach(var (id,row) in oldInstances)
            if(newInstances.TryGetValue(id,out var actual)) FamilyIdChanged(row,actual,id);

        var added=newInstances.Where(p=>!oldInstances.ContainsKey(p.Key)).OrderBy(p=>p.Key).Select(p=>p.Value).ToArray();
        var removed=oldInstances.Where(p=>!newInstances.ContainsKey(p.Key)).OrderBy(p=>p.Key).Select(p=>p.Value).ToArray();
        var changed=oldInstances.Where(p=>newInstances.ContainsKey(p.Key)).Select(p=>new InstanceChange(p.Key,
            ChangedFields(p.Value,newInstances[p.Key],"familyId"),p.Value,newInstances[p.Key])).Where(p=>p.Fields.Length>0).OrderBy(p=>p.InstanceId).ToArray();
        var addedTypes=newTypes.Where(p=>!oldTypes.ContainsKey(p.Key)).OrderBy(p=>p.Key).Select(p=>p.Value).ToArray();
        var removedTypes=oldTypes.Where(p=>!newTypes.ContainsKey(p.Key)).OrderBy(p=>p.Key).Select(p=>p.Value).ToArray();
        var changedTypes=oldTypes.Where(p=>newTypes.ContainsKey(p.Key)).Select(p=>new TypeChange(p.Key,
            ChangedFields(p.Value,newTypes[p.Key]),p.Value,newTypes[p.Key])).Where(p=>p.Fields.Length>0).OrderBy(p=>p.TypeId).ToArray();

        long? Parent(JsonElement row) => row.TryGetProperty("superComponentId",out var parent)&&parent.ValueKind==JsonValueKind.Number ? parent.GetInt64() : null;
        var roots=oldInstances.Where(p=>Parent(p.Value) is not long parent || !oldInstances.ContainsKey(parent)).Select(p=>p.Key).ToHashSet();
        bool ExpectedNested(JsonElement row)
        {
            var seen=new HashSet<long>{row.GetProperty("instanceId").GetInt64()};
            for(var parent=Parent(row);parent.HasValue;)
            {
                if(!seen.Add(parent.Value)||!newInstances.TryGetValue(parent.Value,out var ancestor)) return false;
                if(roots.Contains(parent.Value)) return true;
                parent=Parent(ancestor);
            }
            return false;
        }
        var unexpected=added.Where(row=>!ExpectedNested(row)).Select(row=>row.GetProperty("instanceId").GetInt64()).ToArray();
        return new(issues.Count==0&&removed.Length==0&&changed.Length==0&&removedTypes.Length==0&&changedTypes.Length==0&&unexpected.Length==0,
            familyIds.ToArray(),added,removed,changed,addedTypes,removedTypes,changedTypes,unexpected,issues.ToArray());
    }

    private static string[] ChangedFields(JsonElement before,JsonElement after,params string[] ignored) =>
        before.EnumerateObject().Select(p=>p.Name).Union(after.EnumerateObject().Select(p=>p.Name)).Except(ignored,StringComparer.Ordinal)
            .Where(key=>!before.TryGetProperty(key,out var a)||!after.TryGetProperty(key,out var b)||!Equivalent(a,b)).Order(StringComparer.Ordinal).ToArray();

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
