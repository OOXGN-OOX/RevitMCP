using RevitMCP;
using System.Text;

var checks = 0;
void Check(bool success, string message) { if (!success) throw new Exception(message); checks++; }
void Reject(Action action, string message)
{
    try { action(); } catch (ArgumentException) { checks++; return; }
    throw new Exception(message);
}
var catalog = FamilyCatalog.Parse(",Length##length##millimeters,Material##OTHER##,Comment##OTHER##\r\n" +
    "\"Type, 100\",100,Steel,\"two\r\nlines\"\r\n48\"\" x 60\"\",200,,\"quoted \"\"text\"\"\"\r\n");
Check(catalog.Columns[0] == new CatalogColumn("Length", "LENGTH", "MILLIMETERS"), "Header normalization");
Check(catalog.Rows.Length == 2, "Multiline quoted cells must not create types");
Check(catalog.ExactRow("Type, 100").Values[2] == "two\r\nlines", "Quoted delimiter and newline");
Check(catalog.ExactRow("48\" x 60\"").Values[2] == "quoted \"text\"", "Autodesk inch marks and CSV quotes");
Check(catalog.ExactRow("48\" x 60\"").Values[1] == "", "Blank material remains explicit");
Reject(() => catalog.ExactRow("type, 100"), "Type selection must be exact");
Reject(() => FamilyCatalog.Parse(",A##OTHER##\nSame,x\nSame,y").ExactRow("Same"), "Duplicate names must not select arbitrary rows");
foreach (var malformed in new[] { "", "Type,A\nx,y", ",A\nx,y", ",A##OTHER##,A##OTHER##\nx,1,2", ",A##OTHER##\nx,1,2", ",A##OTHER##\n,1", ",A##OTHER##\nx,\"unclosed", ",A##OTHER##\nx,\"value\"junk", ",A##OTHER##\nx,un\"escaped" })
    Reject(() => FamilyCatalog.Parse(malformed), "Malformed catalog accepted: " + malformed);
Check(FamilyCatalog.Parse(";A##OTHER##\nType;1").ExactRow("Type").Values[0] == "1", "Delimiter defined by first character");
Check(FamilyCatalog.Parse("\uFEFF,A##OTHER##\nType,1\n\n").Rows.Length == 1, "BOM and trailing empty lines");
var root = Path.Combine(Path.GetTempPath(), "RevitMCP-Catalog-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var contents = ",Ширина##LENGTH##MILLIMETERS\nРазмер 100,100";
    foreach (var (name, encoding) in new[] { ("utf8", (Encoding)new UTF8Encoding(false)), ("utf16", Encoding.Unicode), ("windows1251", Encoding.GetEncoding(1251)) })
    {
        var path = Path.Combine(root, name + ".txt"); File.WriteAllText(path, contents, encoding);
        Check(FamilyCatalog.Read(path, name).ExactRow("Размер 100").Values[0] == "100", "Decode " + name);
    }
    var bad = Path.Combine(root, "invalid.txt"); File.WriteAllBytes(bad, [0xff, 0xff, 0xff]);
    Reject(() => FamilyCatalog.Read(bad, "utf8"), "Corrupt encoding cannot silently replace characters");
    var large = Path.Combine(root, "large.txt"); using (var stream = File.Create(large)) stream.SetLength(16 * 1024 * 1024 + 1);
    Reject(() => FamilyCatalog.Read(large, "utf8"), "Bound catalog size");
    Console.WriteLine($"PASS: {checks} family catalog checks");
}
finally { Directory.Delete(root, recursive: true); }
