using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static string ModelExportDirectory(JsonElement args)
    {
        var root = JsonTools.GetString(args,"directory");
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\") || root.Length > 170)
            throw new ArgumentException("Use a fully qualified local directory of at most 170 characters.");
        var directory = Path.Combine(Path.GetFullPath(root),"export-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static object ExportViews(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var format = JsonTools.GetString(args,"format"); var scope = JsonTools.GetString(args,"scope","views");
        var views = scope == "views" ? args.GetProperty("viewIds").EnumerateArray().Select(v=>new ElementId(v.GetInt64())).ToList()
            : new List<ElementId>{app.ActiveUIDocument!.ActiveView.Id};
        if (views.Count is <1 or >20) throw new ArgumentException("Export 1 to 20 views.");
        foreach (var id in views)
            if (doc.GetElement(id) is not View view || view.IsTemplate || !view.CanBePrinted || (format=="csv" && view is not ViewSchedule))
                throw new ArgumentException($"View {id.Value} cannot be exported in the selected format.");
        if (scope=="visibleRegion" && format is "pdf" or "csv") throw new ArgumentException("Visible-region export requires an image format.");
        var directory = ModelExportDirectory(args);
        if (format == "csv")
        {
            foreach (var id in views)
                ((ViewSchedule)doc.GetElement(id)).Export(directory,$"schedule-{id.Value}.csv",
                    new ViewScheduleExportOptions { FieldDelimiter=",",TextQualifier=ExportTextQualifier.DoubleQuote });
        }
        else if (format == "pdf")
        {
            foreach (var id in views)
                if (!doc.Export(directory,new List<ElementId>{id},new PDFExportOptions{Combine=true,FileName=$"view-{id.Value}"}))
                    throw new InvalidOperationException("Revit PDF export failed.");
        }
        else
        {
            var type = format switch { "png"=>ImageFileType.PNG, "jpg"=>ImageFileType.JPEGLossless, "bmp"=>ImageFileType.BMP, "tiff"=>ImageFileType.TIFF, _=>throw new ArgumentException("Unsupported image format.") };
            using var options = new ImageExportOptions {
                FilePath=Path.Combine(directory,"view"), HLRandWFViewsFileType=type, ShadowViewsFileType=type,
                ExportRange=scope=="visibleRegion"?ExportRange.VisibleRegionOfCurrentView:scope=="activeView"?ExportRange.CurrentView:ExportRange.SetOfViews,
                ZoomType=ZoomFitType.FitToPage, PixelSize=JsonTools.GetInt(args,"pixelSize",2000), ImageResolution=ImageResolution.DPI_150
            };
            if (scope=="views") options.SetViewsAndSheets(views);
            doc.ExportImage(options);
        }
        var files = Directory.GetFiles(directory);
        if (files.Length==0) throw new InvalidOperationException("No export files were generated.");
        return new { files, modelChanged=false };
    }

    private static object ExportModel(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app); RequireWritableProject(doc);
        var format = JsonTools.GetString(args,"format");
        if (format is not ("ifc" or "gbxml")) throw new ArgumentException("Unsupported model export format.");
        var directory = ModelExportDirectory(args);
        // IFC exporters may modify the document. Retain only files, never their document side effects.
        SimulationDepth++;
        try
        {
            using var group = new TransactionGroup(doc,"Revit MCP: экспорт");
            group.Start();
            try
            {
                bool exported;
                using var transaction = new Transaction(doc,"Model export");
                BeginCheckedTransaction(transaction);
                exported = format=="ifc"
                    ? doc.Export(directory,"model.ifc",new IFCExportOptions())
                    : doc.Export(directory,"model.xml",new GBXMLExportOptions());
                transaction.RollBack();
                if (!exported) throw new InvalidOperationException("Revit did not complete the model export.");
            }
            finally { if (group.GetStatus()==TransactionStatus.Started) group.RollBack(); }
        }
        finally { SimulationDepth--; }
        var files=Directory.GetFiles(directory);
        if(files.Length==0) throw new InvalidOperationException("No model export files were generated.");
        return new { files, modelChanged=false };
    }
}
