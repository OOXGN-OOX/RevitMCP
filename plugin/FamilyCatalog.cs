using System.Text;

namespace RevitMCP;

internal sealed record CatalogColumn(string Name, string DataType, string Units);
internal sealed record CatalogRow(string Name, string[] Values);
internal sealed record FamilyCatalog(CatalogColumn[] Columns, CatalogRow[] Rows)
{
    public static FamilyCatalog Read(string path, string encoding)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new ArgumentException("Type catalog exceeds 16 MB.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codec = encoding switch { "utf8" => new UTF8Encoding(false, true), "utf16" => new UnicodeEncoding(false, true, true),
            "windows1251" => Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), _ => throw new ArgumentException("Unknown catalog encoding.") };
        using var reader = new StreamReader(path, codec, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    public static FamilyCatalog Parse(string text)
    {
        text = text.TrimStart('\uFEFF');
        if (text.Length == 0 || char.IsLetterOrDigit(text[0]) || text[0] is '"' or '#' or '\r' or '\n' or ' ')
            throw new ArgumentException("Catalog must begin with its column delimiter.");
        var delimiter = text[0];
        var records = new List<string[]>(); var fields = new List<string>(); var field = new StringBuilder();
        bool quoted = false, endedQuote = false;
        void FinishField() { fields.Add(field.ToString()); field.Clear(); endedQuote = false; }
        void FinishRecord()
        {
            FinishField();
            if (fields.Any(value => value.Length > 0)) records.Add(fields.ToArray());
            fields.Clear();
            if (records.Count > 10001) throw new ArgumentException("Catalog exceeds 10000 types.");
        }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else { quoted = false; endedQuote = true; } }
                else field.Append(ch);
            }
            else if (ch == delimiter) FinishField();
            else if (ch is '\r' or '\n') { if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; FinishRecord(); }
            else if (ch == '"' && field.Length == 0 && !endedQuote) quoted = true;
            else if (ch == '"' && !endedQuote && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
            else if (endedQuote || ch == '"') throw new ArgumentException("Malformed quoted catalog field.");
            else field.Append(ch);
        }
        if (quoted) throw new ArgumentException("Unclosed catalog quote.");
        if (field.Length > 0 || fields.Count > 0 || endedQuote) FinishRecord();
        if (records.Count < 2 || records[0][0].Trim('\uFEFF', ' ', '\t').Length != 0) throw new ArgumentException("Catalog needs an empty first header cell and at least one type row.");
        if (records[0].Length is < 2 or > 513) throw new ArgumentException("Catalog needs 1 to 512 parameter columns.");
        var columns = records[0].Skip(1).Select(value =>
        {
            var parts = value.Split("##", StringSplitOptions.None);
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0])) throw new ArgumentException("Expected parameter##type##units header.");
            return new CatalogColumn(parts[0].Trim(), parts[1].Trim().ToUpperInvariant(), parts[2].Trim().ToUpperInvariant());
        }).ToArray();
        if (columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != columns.Length) throw new ArgumentException("Duplicate catalog parameter name.");
        var rows = records.Skip(1).Select(values =>
        {
            if (values.Length != columns.Length + 1 || string.IsNullOrWhiteSpace(values[0])) throw new ArgumentException("Catalog row has an empty type name or wrong field count.");
            return new CatalogRow(values[0], values.Skip(1).ToArray());
        }).ToArray();
        return new(columns, rows);
    }

    public CatalogRow ExactRow(string name)
    {
        var matches = Rows.Where(row => string.Equals(row.Name, name, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException($"Catalog type '{name}' is missing or duplicated; choose an exact unique row.");
    }
}
