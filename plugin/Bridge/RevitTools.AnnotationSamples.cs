using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static object ValidateAnnotationFamily(UIApplication app,JsonElement args,Func<bool>? cancelled)
    {
        var path=FPath(args,"path",".rfa");var projectPath=AuthoringFilePath(args,"sampleProjectPath",".rvt",true);
        if(app.Application.Documents.Cast<Document>().Any(d=>string.Equals(d.PathName,projectPath,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Close the sample project before isolated annotation validation.");
        var hashes=new Dictionary<string,string>{{path,FHash(path)},{projectPath,FHash(projectPath)}};
        var samples=FArray(args,"samples").ToArray();if(samples.Length is <1 or >20)throw new ArgumentException("Provide 1 to 20 annotation samples.");
        Document? doc=null;string? directory=null;
        try
        {
            doc=app.Application.OpenDocumentFile(projectPath);
            if(doc.IsFamilyDocument||doc.IsWorkshared||doc.IsReadOnly)throw new ArgumentException("The sample must be a standalone writable RVT without worksharing.");
            Family? family;long[] symbols;
            using(var tx=new Transaction(doc,"Load sample annotation family"))
            {
                BeginCheckedTransaction(tx);var loaded=doc.LoadFamily(path,new FamilyLoadPolicy("overwrite","file"),out family);
                family??=new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().SingleOrDefault(f=>f.Name==Path.GetFileNameWithoutExtension(path));
                if(family is null)throw new InvalidOperationException("Cannot load sample annotation family.");
                symbols=family.GetFamilySymbolIds().Select(id=>id.Value).ToArray();
                CommitCheckedTransaction(tx,cancelled);
            }
            family=symbols.Select(id=>doc.GetElement(new ElementId(id))).OfType<FamilySymbol>().Select(s=>s.Family).FirstOrDefault()
                ??throw new InvalidOperationException("The sample family has no usable symbols after commit.");
            directory=ModelExportDirectory(args);var results=new List<object>();
            for(var sampleIndex=0;sampleIndex<samples.Length;sampleIndex++)
            {
                if(cancelled?.Invoke()==true)throw new OperationCanceledException();
                var sample=samples[sampleIndex];using var group=new TransactionGroup(doc,"Annotation sample");if(group.Start()!=TransactionStatus.Started)throw new InvalidOperationException("Cannot start annotation sample group.");
                try
                {
                    var view=AView(doc,sample);var rendered=new List<object>();var tagIds=new List<(long TagId,long ElementId)>();
                    using(var tx=new Transaction(doc,"Build annotation sample"))
                    {
                        BeginCheckedTransaction(tx);view.Scale=sample.GetProperty("scale").GetInt32();
                        foreach(var assignment in FArray(sample,"parameters"))
                        {
                            var element=doc.GetElement(new ElementId(assignment.GetProperty("elementId").GetInt64()))??throw new ArgumentException("Sample parameter target not found.");
                            var p=SelectParameter(doc,element,assignment);if(p.IsReadOnly)throw new ArgumentException("Sample parameter is read-only.");
                            SetCheckedParameter(doc,p,new ParameterValueInput(FString(assignment,"value"),null));
                        }
                        var symbol=family.GetFamilySymbolIds().Select(id=>(FamilySymbol)doc.GetElement(id)).SingleOrDefault(s=>s.Name==FString(sample,"typeName"))??throw new ArgumentException("Sample annotation type not found.");
                        if(!symbol.IsActive){symbol.Activate();doc.Regenerate();}
                        foreach(var id in FArray(sample,"elementIds"))
                        {
                            var element=doc.GetElement(new ElementId(id.GetInt64()))??throw new ArgumentException("Sample tagged element not found.");
                            var box=element.get_BoundingBox(view)??throw new ArgumentException("Sample element is not visible in the target view.");var point=(box.Min+box.Max)/2;
                            var tag=IndependentTag.Create(doc,symbol.Id,view.Id,new Reference(element),false,TagOrientation.Horizontal,point);
                            tagIds.Add((tag.Id.Value,element.Id.Value));
                        }
                        CommitCheckedTransaction(tx,cancelled);
                    }
                    foreach(var (tagId,elementId) in tagIds)
                    {
                        var tag=doc.GetElement(new ElementId(tagId)) as IndependentTag??throw new InvalidOperationException("A sample tag disappeared during commit.");
                        rendered.Add(new {elementId,tag=ATagState(tag),empty=string.IsNullOrWhiteSpace(tag.TagText),unresolved=tag.TagText.Contains('?'),bounds=DocBox(tag.get_BoundingBox(view))});
                    }
                    using(var options=new ImageExportOptions {FilePath=Path.Combine(directory,"sample-"+sampleIndex),HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG,
                        ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=(int)FNumber(args,"pixelSize",1600),ImageResolution=ImageResolution.DPI_150})
                    {options.SetViewsAndSheets(new List<ElementId>{view.Id});doc.ExportImage(options);}
                    results.Add(new {sampleIndex,viewId=view.Id.Value,scale=view.Scale,typeName=FString(sample,"typeName"),rendered});
                }
                finally{if(group.GetStatus()==TransactionStatus.Started)group.RollBack();}
            }
            FClose(doc);doc=null;FVerifyDependencies(hashes);
            var images=Directory.GetFiles(directory,"*.png").Order().ToArray();if(images.Length<samples.Length)throw new InvalidOperationException("Revit did not export every requested annotation sample image.");
            return new {sourceUnchanged=true,sampleProjectUnchanged=true,directory,images,samples=results,
                note="Images are actual Revit exports. Empty/unresolved text is reported, not inferred to be a successful label composition check. Visual inspection is required."};
        }
        finally{FClose(doc);}
    }
}
