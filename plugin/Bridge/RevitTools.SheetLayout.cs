using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;
namespace RevitMCP;
internal static partial class RevitTools
{
    private static object PreviewFinishSheet(UIApplication app,JsonElement args)=>PreviewOperation(app,args,"finishSheet","Оформление листа",FinishSheet);
    private static object ApplyFinishSheet(UIApplication app,JsonElement args,Func<bool>? cancelled)=>ApplyOperation(app,args,"finishSheet","Применить оформление листа?",FinishSheet,cancelled);
    private static object FinishSheet(Document doc,JsonElement args)
    {
        var sheet=doc.GetElement(new ElementId(HostedReadId(args,"sheetId"))) as ViewSheet??throw new ArgumentException("sheetId must identify a sheet.");
        var sheetWidth=SheetMm(sheet.Outline.Max.U-sheet.Outline.Min.U);
        var sheetHeight=SheetMm(sheet.Outline.Max.V-sheet.Outline.Min.V);
        XYZ Position(double x,double y)=>new(sheet.Outline.Min.U+SheetUnits(x),sheet.Outline.Min.V+SheetUnits(y),0);
        var moved=new List<long>();var notes=new List<long>();
        if(args.TryGetProperty("viewports",out var placements))
        {
            if(placements.GetArrayLength()>10)throw new ArgumentException("Maximum 10 viewports.");
            foreach(var p in placements.EnumerateArray())
            {
                var vp=doc.GetElement(new ElementId(HostedReadId(p,"viewportId"))) as Viewport;
                if(vp is null||vp.SheetId!=sheet.Id)throw new ArgumentException("Viewport belongs to another sheet.");
                var view=(View)doc.GetElement(vp.ViewId);
                view.Scale=(int)BuildingNumber(p,"scale",1,1000);
                doc.Regenerate();vp.SetBoxCenter(Position(BuildingNumber(p,"xMm",0,sheetWidth),BuildingNumber(p,"yMm",0,sheetHeight)));doc.Regenerate();
                var box=vp.GetBoxOutline();
                if(box.MinimumPoint.X<sheet.Outline.Min.U+SheetUnits(20)||box.MaximumPoint.X>sheet.Outline.Max.U-SheetUnits(5)||box.MinimumPoint.Y<sheet.Outline.Min.V+SheetUnits(5)||box.MaximumPoint.Y>sheet.Outline.Max.V-SheetUnits(5))throw new InvalidOperationException("Viewport exceeds sheet frame.");
                moved.Add(vp.Id.Value);
            }
        }
        if(args.TryGetProperty("notes",out var texts))
        {
            if(texts.GetArrayLength()>20)throw new ArgumentException("Maximum 20 notes.");
            foreach(var n in texts.EnumerateArray())
            {
                var h=BuildingNumber(n,"heightMm",1.8,7);var name="Revit MCP — Arial "+h.ToString(System.Globalization.CultureInfo.InvariantCulture)+" мм";
                var types=new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToArray();
                var type=types.FirstOrDefault(t=>t.Name==name);
                if(type is null){type=(TextNoteType)types.First().Duplicate(name);type.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(SheetUnits(h));type.get_Parameter(BuiltInParameter.TEXT_FONT).Set("Arial");}
                var text=JsonTools.GetString(n,"text");if(text.Length is <1 or >4000)throw new ArgumentException("Note text length out of range.");
                var x=BuildingNumber(n,"xMm",0,sheetWidth);var y=BuildingNumber(n,"yMm",0,sheetHeight);var width=BuildingNumber(n,"widthMm",1,sheetWidth);
                if(x+width>sheetWidth-5)throw new ArgumentException("Note exceeds right frame.");
                var note=TextNote.Create(doc,sheet.Id,Position(x,y),SheetUnits(width),text,new TextNoteOptions(type.Id){HorizontalAlignment=HorizontalTextAlignment.Left});notes.Add(note.Id.Value);
            }
        }
        return new {sheetId=sheet.Id.Value,viewportIds=moved,textNoteIds=notes};
    }
}
