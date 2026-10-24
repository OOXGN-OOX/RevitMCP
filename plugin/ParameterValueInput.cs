using System.Globalization;
using System.Text.Json;

namespace RevitMCP;

internal sealed record ParameterValueInput(string? Text,long? ElementId)
{
    internal const long MaxSafeId=9007199254740991;
    internal static ParameterValueInput Read(JsonElement args)
    {
        var text=args.TryGetProperty("value",out var value);
        var reference=args.TryGetProperty("elementIdValue",out var id);
        if(text==reference) throw new ArgumentException("Specify exactly one value or elementIdValue.");
        if(text) return value.ValueKind==JsonValueKind.String ? new(value.GetString(),null) : throw new ArgumentException("value must be a string.");
        if(id.ValueKind!=JsonValueKind.Number||!id.TryGetInt64(out var number)) throw new ArgumentException("elementIdValue must be an integer.");
        return new(null,ValidId(number,true));
    }
    internal long ReferenceId() => ElementId ?? (long.TryParse(Text?.Trim(),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var id)
        ? ValidId(id,false) : throw new ArgumentException("An ElementId value must be an exact integer ID, not a display name."));
    private static long ValidId(long id,bool numeric) => id!=0&&(!numeric||id>=-MaxSafeId&&id<=MaxSafeId) ? id : throw new ArgumentException("Numeric ElementId must be a nonzero safe integer; use value for exact Int64 text. -1 denotes InvalidElementId.");
}
