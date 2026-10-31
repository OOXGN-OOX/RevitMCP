using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static View AView(Document doc,JsonElement op)
    {
        if(doc.GetElement(new ElementId(op.GetProperty("viewId").GetInt64())) is not View view||view.IsTemplate
            ||view.ViewType is ViewType.ThreeD or ViewType.Schedule or ViewType.DrawingSheet)
            throw new ArgumentException("Choose an existing planar annotation view, not a template, sheet, 3D view or schedule.");
        return view;
    }
    private static double AScale(Document doc,View view)=>doc.IsFamilyDocument ? 1 : view.Scale;
    private static XYZ APoint(Document doc,View view,JsonElement p)=>view.Origin+view.RightDirection*SheetUnits(FNumber(p,"xMm")*AScale(doc,view))+view.UpDirection*SheetUnits(FNumber(p,"yMm")*AScale(doc,view));
    private static object ALocal(Document doc,View view,XYZ point)=>new {xMm=SheetMm((point-view.Origin).DotProduct(view.RightDirection))/AScale(doc,view),yMm=SheetMm((point-view.Origin).DotProduct(view.UpDirection))/AScale(doc,view)};
    private static Curve ACurve(Document doc,View view,JsonElement curve)
    {
        return FString(curve,"kind") switch {
            "line"=>Line.CreateBound(APoint(doc,view,curve.GetProperty("start")),APoint(doc,view,curve.GetProperty("end"))),
            "arc"=>Arc.Create(APoint(doc,view,curve.GetProperty("start")),APoint(doc,view,curve.GetProperty("end")),APoint(doc,view,curve.GetProperty("through"))),
            "circle"=>Arc.Create(APoint(doc,view,curve.GetProperty("center")),SheetUnits(FNumber(curve,"radiusMm")*AScale(doc,view)),0,Math.PI*2,view.RightDirection,view.UpDirection),
            _=>throw new ArgumentException("Unsupported annotation curve kind.") };
    }
    private static object ACurveInfo(Curve curve)=>new {nativeClass=curve.GetType().Name,isBound=curve.IsBound,
        start=curve.IsBound ? FCoordinates(curve.GetEndPoint(0)) : null,end=curve.IsBound ? FCoordinates(curve.GetEndPoint(1)) : null,
        center=curve is Arc arc ? FCoordinates(arc.Center) : null,radiusMeters=curve is Arc a ? (double?)FMeters(a.Radius) : null,
        points=curve.Tessellate().Select(p=>FCoordinates(p)).ToArray()};
    private static GraphicsStyle AStyle(FamilyBuildContext ctx,JsonElement reference)=>ctx.Get(reference) as GraphicsStyle??throw new ArgumentException("lineStyle must identify a GraphicsStyle.");
    private static void AStyle(FamilyBuildContext ctx,Element element,JsonElement op)
    {
        if(!op.TryGetProperty("lineStyle",out var reference))return;
        var style=AStyle(ctx,reference);
        if(element is CurveElement curve)curve.LineStyle=style;
        else if(element is FilledRegion region)region.SetLineStyleId(style.Id);
    }
    private static void ARequireEditable(Element element)
    { if(element.Pinned||element.GroupId!=ElementId.InvalidElementId)throw new ArgumentException("Pinned or grouped annotations cannot be edited by this operation."); }
    private static Element AOwned(FamilyBuildContext ctx,JsonElement op,View view)
    {
        var element=ctx.Get(op.GetProperty("element"));ARequireEditable(element);
        if(element.ViewSpecific&&element.OwnerViewId!=view.Id)throw new ArgumentException("The annotation belongs to a different view.");
        return element;
    }
    private static void ATextSettings(Document doc,TextElement text,View view,JsonElement op)
    {
        if(op.TryGetProperty("text",out var content))
        {
            if(text is not TextNote note)throw new ArgumentException("Label composition and content cannot be edited in Revit 2026. Only TextNote supports literal text edits.");
            note.Text=content.GetString();
        }
        if(op.TryGetProperty("widthMm",out var width))text.Width=SheetUnits(width.GetDouble());
        if(op.TryGetProperty("horizontal",out var horizontal))text.HorizontalAlignment=Enum.Parse<HorizontalTextAlignment>(horizontal.GetString()!);
        if(op.TryGetProperty("vertical",out var vertical))text.VerticalAlignment=Enum.Parse<VerticalTextAlignment>(vertical.GetString()!);
        if(op.TryGetProperty("position",out var position))ElementTransformUtils.MoveElement(doc,text.Id,APoint(doc,view,position)-text.Coord);
        doc.Regenerate();
        if(op.TryGetProperty("widthMm",out var expectedWidth)&&Math.Abs(SheetMm(text.Width)-expectedWidth.GetDouble())>.001)throw new InvalidOperationException("Revit did not retain the requested text width.");
        if(op.TryGetProperty("position",out var expectedPosition)&&text.Coord.DistanceTo(APoint(doc,view,expectedPosition))>SheetUnits(.001*AScale(doc,view)))throw new InvalidOperationException("Revit did not retain the requested text position.");
        if(op.TryGetProperty("horizontal",out var h)&&text.HorizontalAlignment.ToString()!=h.GetString())throw new InvalidOperationException("Revit did not retain text alignment.");
        if(op.TryGetProperty("vertical",out var v)&&text.VerticalAlignment.ToString()!=v.GetString())throw new InvalidOperationException("Revit did not retain vertical text alignment.");
    }
    private static bool FAnnotationOperation(FamilyBuildContext ctx,JsonElement op)
    {
        var doc=ctx.Document;var action=FString(op,"action");
        switch(action)
        {
            case "annotationRegionStyle":
                if(ctx.Get(op.GetProperty("baseType")) is not FilledRegionType originalRegionType)throw new ArgumentException("baseType must identify a FilledRegionType.");
                var regionType=(FilledRegionType)originalRegionType.Duplicate(FString(op,"name"));
                if(op.TryGetProperty("foregroundPatternId",out var foreground))regionType.ForegroundPatternId=new ElementId(foreground.GetInt64());
                if(op.TryGetProperty("backgroundPatternId",out var background))regionType.BackgroundPatternId=new ElementId(background.GetInt64());
                if(op.TryGetProperty("foregroundColor",out var fc))regionType.ForegroundPatternColor=new Color(fc[0].GetByte(),fc[1].GetByte(),fc[2].GetByte());
                if(op.TryGetProperty("backgroundColor",out var bc))regionType.BackgroundPatternColor=new Color(bc[0].GetByte(),bc[1].GetByte(),bc[2].GetByte());
                if(op.TryGetProperty("lineWeight",out var weight))regionType.LineWeight=weight.GetInt32();
                ctx.Add(op,regionType);return true;
            case "annotationLineStyle":
                var parent=doc.IsFamilyDocument ? doc.OwnerFamily.FamilyCategory : doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                if(parent is null||!parent.CanAddSubcategory)throw new ArgumentException("The current category cannot host a line subcategory.");
                var category=doc.Settings.Categories.NewSubcategory(parent,FString(op,"name"));
                category.SetLineWeight(op.GetProperty("lineWeight").GetInt32(),GraphicsStyleType.Projection);
                if(op.TryGetProperty("color",out var rgb))category.LineColor=new Color(rgb[0].GetByte(),rgb[1].GetByte(),rgb[2].GetByte());
                if(op.TryGetProperty("linePatternId",out var pattern))category.SetLinePatternId(new ElementId(pattern.GetInt64()),GraphicsStyleType.Projection);
                ctx.Add(op,category.GetGraphicsStyle(GraphicsStyleType.Projection));return true;
            case "annotationTextStyle":
                if(ctx.Get(op.GetProperty("baseType")) is not TextElementType baseType)throw new ArgumentException("baseType must identify a TextElementType.");
                var textType=baseType.Duplicate(FString(op,"name"));
                void Set(BuiltInParameter id,Action<Parameter> setter) {var p=textType.get_Parameter(id);if(p is null||p.IsReadOnly)throw new ArgumentException("Text type does not support "+id);setter(p);}
                if(op.TryGetProperty("font",out var font))Set(BuiltInParameter.TEXT_FONT,p=>p.Set(font.GetString()));
                if(op.TryGetProperty("sizeMm",out var size))Set(BuiltInParameter.TEXT_SIZE,p=>p.Set(SheetUnits(size.GetDouble())));
                foreach(var (key,id) in new[]{("bold",BuiltInParameter.TEXT_STYLE_BOLD),("italic",BuiltInParameter.TEXT_STYLE_ITALIC),("underline",BuiltInParameter.TEXT_STYLE_UNDERLINE)})
                    if(op.TryGetProperty(key,out var flag))Set(id,p=>p.Set(flag.GetBoolean()?1:0));
                if(op.TryGetProperty("opaque",out var opaque))Set(BuiltInParameter.TEXT_BACKGROUND,p=>p.Set(opaque.GetBoolean()?0:1));
                ctx.Add(op,textType);return true;
            case "annotationCurve":
                var cv=AView(doc,op);var geometry=ACurve(doc,cv,op.GetProperty("curve"));CurveElement line;
                if(FString(op,"mode","detail")=="symbolic")
                { if(!doc.IsFamilyDocument)throw new ArgumentException("Symbolic curves require a family document.");line=doc.FamilyCreate.NewSymbolicCurve(geometry,SketchPlane.Create(doc,Plane.CreateByNormalAndOrigin(cv.ViewDirection,cv.Origin))); }
                else line=doc.IsFamilyDocument ? doc.FamilyCreate.NewDetailCurve(cv,geometry) : doc.Create.NewDetailCurve(cv,geometry);
                AStyle(ctx,line,op);ctx.Add(op,line);return true;
            case "editAnnotationCurve":
                var ev=AView(doc,op);if(AOwned(ctx,op,ev) is not CurveElement existing)throw new ArgumentException("Expected a curve element.");
                existing.SetGeometryCurve(ACurve(doc,ev,op.GetProperty("curve")),false);AStyle(ctx,existing,op);return true;
            case "annotationText":
                var tv=AView(doc,op);var type=ctx.Get(op.GetProperty("textType"));
                if(type is not TextNoteType)throw new ArgumentException("Literal text creation requires a TextNoteType.");
                var options=new TextNoteOptions(type.Id);
                var note=op.TryGetProperty("widthMm",out var w) ? TextNote.Create(doc,tv.Id,APoint(doc,tv,op.GetProperty("position")),SheetUnits(w.GetDouble()),FString(op,"text"),options)
                    : TextNote.Create(doc,tv.Id,APoint(doc,tv,op.GetProperty("position")),FString(op,"text"),options);
                ATextSettings(doc,note,tv,op);ctx.Add(op,note);return true;
            case "editAnnotationText":
                var view=AView(doc,op);if(AOwned(ctx,op,view) is not TextElement text)throw new ArgumentException("Expected a TextElement or TextNote.");
                if(op.TryGetProperty("textType",out var tt)){if(ctx.Get(tt) is not TextElementType selectedType)throw new ArgumentException("textType must identify a TextElementType.");text.ChangeTypeId(selectedType.Id);}
                ATextSettings(doc,text,view,op);return true;
            case "annotationRegion": case "replaceAnnotationRegion":
                var rv=AView(doc,op);FilledRegion? old=null;
                if(action=="replaceAnnotationRegion")old=AOwned(ctx,op,rv) as FilledRegion??throw new ArgumentException("Expected a filled or masking region.");
                var loops=FArray(op,"boundaries").Select(boundary=>{
                    var loop=new CurveLoop();foreach(var c in boundary.EnumerateArray())loop.Append(ACurve(doc,rv,c));return loop;}).ToList();
                var region=FBool(op,"masking") ? FilledRegion.CreateMaskingRegion(doc,rv.Id,loops)
                    : FilledRegion.Create(doc,ctx.Get(op.GetProperty("regionType")).Id,rv.Id,loops);
                AStyle(ctx,region,op);
                if(old is not null){var oldId=old.Id.Value;var deleted=doc.Delete(old.Id);if(deleted.Count!=1&&!FBool(op,"allowDependentDeletion"))throw new ArgumentException("Replacing the region deletes dependencies; allowDependentDeletion is required.");ctx.Fields.Add(new {status="replacedRegion",oldId,newId=region.Id.Value,deletedIds=deleted.Select(i=>i.Value).ToArray()});}
                ctx.Add(op,region);return true;
            case "annotationDimension":
                var dv=AView(doc,op);var refs=new ReferenceArray();
                foreach(var r in FArray(op,"references")){var e=ctx.Get(r);refs.Append(e is ReferencePlane plane ? plane.GetReference() : e is CurveElement c ? c.GeometryCurve.Reference : throw new ArgumentException("Dimensions require reference planes or curve references."));}
                var axis=Line.CreateBound(APoint(doc,dv,op.GetProperty("start")),APoint(doc,dv,op.GetProperty("end")));
                var dimension=doc.IsFamilyDocument ? doc.FamilyCreate.NewLinearDimension(dv,axis,refs) : doc.Create.NewDimension(dv,axis,refs);
                ASetDimension(doc,dimension,op);ctx.Add(op,dimension);return true;
            case "setAnnotationDimension":
                var dim=ctx.Get(op.GetProperty("element")) as Dimension??throw new ArgumentException("Expected a Dimension.");ARequireEditable(dim);ASetDimension(doc,dim,op);return true;
            default:return false;
        }
    }
    private static void ASetDimension(Document doc,Dimension dimension,JsonElement op)
    {
        if(op.TryGetProperty("parameter",out var parameter))
        {if(!doc.IsFamilyDocument)throw new ArgumentException("Family dimension parameters require a family document.");dimension.FamilyLabel=parameter.ValueKind==JsonValueKind.Null ? null : FParameter(doc,parameter);}
        if(op.TryGetProperty("locked",out var locked))dimension.IsLocked=locked.GetBoolean();
    }
    private static object AParameters(Document doc,Element element)=>element.Parameters.Cast<Parameter>().OrderBy(p=>p.Id.Value).Select(p=>new {id=p.Id.Value,name=p.Definition.Name,
        guid=p.IsShared?p.GUID.ToString():null,storageType=p.StorageType.ToString(),readOnly=p.IsReadOnly,value=FamilyParameterInfo(p),
        familyParameter=doc.IsFamilyDocument ? doc.FamilyManager.GetAssociatedFamilyParameter(p)?.Definition.Name : null}).ToArray();
    private static object AElement(Document doc,Element element)
    {
        var view=doc.GetElement(element.OwnerViewId) as View;
        return new {id=element.Id.Value,uniqueId=element.UniqueId,nativeClass=element.GetType().Name,typeId=element.GetTypeId().Value,ownerViewId=element.OwnerViewId.Value,
            frame=element is FamilyInstance familyInstance ? FFrameInfo(familyInstance.GetTransform()) : null,
            parameters=AParameters(doc,element),
            curve=element is CurveElement curve ? ACurveInfo(curve.GeometryCurve) : null,
            lineStyleId=element is CurveElement ce ? ce.LineStyle?.Id.Value : null,
            text=element is TextElement text ? new {content=text is TextNote ? text.Text : null,labelCandidate=text is not TextNote,compositionAvailable=false,
                position=FCoordinates(text.Coord),localPosition=view is null?null:ALocal(doc,view,text.Coord),widthMm=SheetMm(text.Width),heightMm=SheetMm(text.Height),
                horizontal=text.HorizontalAlignment.ToString(),vertical=text.VerticalAlignment.ToString(),baseDirection=FCoordinates(text.BaseDirection,false),upDirection=FCoordinates(text.UpDirection,false)} : null,
            boundaries=element is FilledRegion region ? region.GetBoundaries().Select(l=>l.Select(ACurveInfo).ToArray()).ToArray() : null,
            isMasking=element is FilledRegion filledRegion ? (bool?)filledRegion.IsMasking : null,
            dimension=element is Dimension d ? new {value=d.Value,isLocked=d.IsLocked,parameter=doc.IsFamilyDocument ? d.FamilyLabel?.Definition.Name : null,
                references=d.References.Cast<Reference>().Select(r=>new {elementId=r.ElementId.Value,stableReference=r.ConvertToStableRepresentation(doc)}).ToArray()} : null};
    }
    private static object AResources(Document doc)=>new {
        views=new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v=>!v.IsTemplate&&v.ViewType is not (ViewType.ThreeD or ViewType.Schedule or ViewType.DrawingSheet)).Select(v=>new {id=v.Id.Value,name=v.Name,kind=v.ViewType.ToString(),scale=v.Scale,
            origin=FCoordinates(v.Origin),right=FCoordinates(v.RightDirection,false),up=FCoordinates(v.UpDirection,false),normal=FCoordinates(v.ViewDirection,false)}).ToArray(),
        textTypes=new FilteredElementCollector(doc).WhereElementIsElementType().OfType<TextElementType>().OrderBy(t=>t.Id.Value).Select(t=>new {id=t.Id.Value,name=t.Name,nativeClass=t.GetType().Name,parameters=AParameters(doc,t)}).ToArray(),
        regionTypes=new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().OrderBy(t=>t.Id.Value).Select(t=>new {id=t.Id.Value,name=t.Name,parameters=AParameters(doc,t)}).ToArray(),
        lineStyles=new FilteredElementCollector(doc).OfClass(typeof(GraphicsStyle)).Cast<GraphicsStyle>().Where(s=>s.GraphicsStyleType==GraphicsStyleType.Projection).OrderBy(s=>s.Id.Value).Select(s=>new {id=s.Id.Value,name=s.Name,categoryId=s.GraphicsStyleCategory.Id.Value,lineWeight=s.GraphicsStyleCategory.GetLineWeight(GraphicsStyleType.Projection),linePatternId=s.GraphicsStyleCategory.GetLinePatternId(GraphicsStyleType.Projection).Value,color=new {r=s.GraphicsStyleCategory.LineColor.Red,g=s.GraphicsStyleCategory.LineColor.Green,b=s.GraphicsStyleCategory.LineColor.Blue}}).ToArray(),
        fillPatterns=new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().OrderBy(p=>p.Id.Value).Select(p=>new {id=p.Id.Value,name=p.Name,target=p.GetFillPattern().Target.ToString(),solid=p.GetFillPattern().IsSolidFill}).ToArray(),
        sharedParameters=new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>().OrderBy(p=>p.Id.Value).Select(p=>new {id=p.Id.Value,name=p.Name,guid=p.GuidValue.ToString()}).ToArray()};
    private static object AStyleState(Document doc)=>new {
        types=new FilteredElementCollector(doc).WhereElementIsElementType().Where(e=>e is TextElementType or FilledRegionType).OrderBy(e=>e.Id.Value)
            .Select(e=>new {id=e.Id.Value,name=e.Name,nativeClass=e.GetType().Name,parameters=AParameters(doc,e)}).ToArray()};
    private static object AState(Document doc,long? viewId=null,int offset=0,int limit=5000)
    {
        var elements=new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e=>e is TextElement or CurveElement or FilledRegion or Dimension)
            .Where(e=>viewId is null||e.OwnerViewId.Value==viewId).OrderBy(e=>e.Id.Value).ToArray();
        var errors=new List<object>();var rows=new List<object>();
        foreach(var element in elements.Skip(offset).Take(limit))
            try{rows.Add(AElement(doc,element));}catch(Exception ex){errors.Add(new {elementId=element.Id.Value,nativeClass=element.GetType().Name,error=ex.Message});}
        return new {totalCount=elements.Length,offset,limit,hasMore=offset+limit<elements.Length,elements=rows,errors,
            labelComposition=new {available=false,reason="Revit 2026 public API exposes TextElement layout and type properties but not Label field composition, order, prefixes, suffixes, separators, breaks or unit formatting."}};
    }
    private static object ReadFamilyAnnotations(UIApplication app,JsonElement args)
    {
        Document? doc=null;
        try {
            doc=FOpen(app,FPath(args,"path",".rfa"));using var tx=new Transaction(doc,"Inspect family annotations");BeginCheckedTransaction(tx);
            if(args.TryGetProperty("typeName",out var type))doc.FamilyManager.CurrentType=FType(doc,type.GetString()!);
            doc.Regenerate();
            var result=JsonSerializer.SerializeToElement(new {category=doc.OwnerFamily.FamilyCategory?.Name,resources=AResources(doc),
                annotations=AState(doc,args.TryGetProperty("viewId",out var id)?id.GetInt64():null,(int)FNumber(args,"offset"),(int)FNumber(args,"limit",100))});
            tx.RollBack();return result;
        }finally{FClose(doc);}
    }
}
