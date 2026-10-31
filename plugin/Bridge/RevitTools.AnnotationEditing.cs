using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static object ATagState(IndependentTag tag)
    {
        var doc=tag.Document;var references=tag.GetTaggedReferences().ToArray();
        return new {id=tag.Id.Value,uniqueId=tag.UniqueId,typeId=tag.GetTypeId().Value,viewId=tag.OwnerViewId.Value,text=tag.TagText,isOrphaned=tag.IsOrphaned,
            headPosition=AnnotationPointMeters(tag.TagHeadPosition),orientation=tag.TagOrientation.ToString(),rotationDegrees=tag.RotationAngle*180/Math.PI,
            hasLeader=tag.HasLeader,leaderEndCondition=tag.LeaderEndCondition.ToString(),
            references=references.Select((r,i)=>new {index=i,elementId=r.ElementId.Value,linkedElementId=r.LinkedElementId.Value,stableReference=r.ConvertToStableRepresentation(doc),
                visible=tag.HasLeader?(bool?)tag.IsLeaderVisible(r):null,elbow=tag.HasLeader&&tag.HasLeaderElbow(r)?AnnotationPointMeters(tag.GetLeaderElbow(r)):null,
                end=tag.HasLeader&&tag.LeaderEndCondition==LeaderEndCondition.Free?AnnotationPointMeters(tag.GetLeaderEnd(r)):null}).ToArray()};
    }
    private static object ReadTags(UIApplication app,JsonElement args)
    {
        var doc=RequireDocument(app);return new {documentRevision=DocumentRevision(doc),tags=args.GetProperty("tagIds").EnumerateArray().Select(id=>ATagState(doc.GetElement(new ElementId(id.GetInt64())) as IndependentTag??throw new ArgumentException("Expected IndependentTag ID."))).ToArray()};
    }
    private static object PreviewEditTags(UIApplication app,JsonElement args)
    {
        var doc=RequireDocument(app);
        if(args.TryGetProperty("documentRevision",out var revision)&&revision.GetInt64()!=DocumentRevision(doc))throw new ArgumentException("The document changed after reading the tag references.");
        var bound=JsonNode.Parse(args.GetRawText())!.AsObject();
        bound["tagBaselines"]=JsonSerializer.SerializeToNode(FArray(args,"tags").Select(op=>ATagState(doc.GetElement(new ElementId(op.GetProperty("tagId").GetInt64())) as IndependentTag??throw new ArgumentException("Expected IndependentTag."))).ToArray());
        return PreviewOperation(app,JsonSerializer.SerializeToElement(bound),"editTags","Редактирование марок",EditTags,ValidateTagsAfterCommit);
    }
    private static object ApplyEditTags(UIApplication app,JsonElement args,Func<bool>? cancelled)=>ApplyOperation(app,args,"editTags","Изменить проверенные марки?",EditTags,cancelled,ValidateTagsAfterCommit);
    private static object EditTags(Document doc,JsonElement args)
    {
        var changes=new List<object>();
        foreach(var op in FArray(args,"tags"))
        {
            var tag=doc.GetElement(new ElementId(op.GetProperty("tagId").GetInt64())) as IndependentTag??throw new ArgumentException("Expected IndependentTag.");ARequireEditable(tag);
            var before=ATagState(tag);var references=tag.GetTaggedReferences().ToArray();
            var baseline=args.GetProperty("tagBaselines").EnumerateArray().Single(b=>b.GetProperty("id").GetInt64()==tag.Id.Value);
            if(!FJsonEquivalent(baseline,JsonSerializer.SerializeToElement(before)))throw new InvalidOperationException("The tag or its reference order changed after preview.");
            if(references.Any(r=>r.LinkedElementId!=ElementId.InvalidElementId))throw new ArgumentException("Linked tag references are not supported for editing.");
            var removals=FArray(op,"removeReferenceIndices").Select(v=>v.GetInt32()).ToArray();
            if(removals.Distinct().Count()!=removals.Length||removals.Any(i=>i<0||i>=references.Length))throw new ArgumentException("Invalid or duplicate reference index.");
            if(op.TryGetProperty("addLocalElementIds",out var additions))
            {
                var newRefs=additions.EnumerateArray().Select(v=>doc.GetElement(new ElementId(v.GetInt64()))??throw new ArgumentException("New tagged element does not exist.")).Select(e=>new Reference(e)).ToList();
                if(newRefs.Select(r=>r.ElementId.Value).Distinct().Count()!=newRefs.Count)throw new ArgumentException("Duplicate new tag references.");
                tag.AddReferences(newRefs);
            }
            if(removals.Length>0)tag.RemoveReferences(removals.Select(i=>references[i]).ToList());
            if(op.TryGetProperty("orientation",out var orientation))tag.TagOrientation=Enum.Parse<TagOrientation>(orientation.GetString()!);
            if(op.TryGetProperty("rotationDegrees",out var rotation))tag.RotationAngle=rotation.GetDouble()*Math.PI/180;
            if(op.TryGetProperty("hasLeader",out var hasLeader))tag.HasLeader=hasLeader.GetBoolean();
            if(op.TryGetProperty("leaderEndCondition",out var condition))tag.LeaderEndCondition=Enum.Parse<LeaderEndCondition>(condition.GetString()!);
            foreach(var leader in FArray(op,"leaders"))
            {
                var index=leader.GetProperty("referenceIndex").GetInt32();
                if(index<0||index>=references.Length||removals.Contains(index))throw new ArgumentException("Leader index refers to a missing or removed original reference.");
                var r=references[index];
                if(!tag.HasLeader)throw new ArgumentException("Leader editing requires hasLeader=true.");
                if(leader.TryGetProperty("visible",out var visible)){tag.LeadersPresentationMode=LeadersPresentationMode.ShowSpecificLeaders;tag.SetIsLeaderVisible(r,visible.GetBoolean());}
                if(leader.TryGetProperty("elbowPosition",out var elbow))tag.SetLeaderElbow(r,ToXYZ(ReadPoint3D(leader,"elbowPosition")));
                if(leader.TryGetProperty("endPosition",out var end))
                {if(tag.LeaderEndCondition!=LeaderEndCondition.Free)throw new ArgumentException("Leader endpoint editing requires a free leader.");tag.SetLeaderEnd(r,ToXYZ(ReadPoint3D(leader,"endPosition")));}
            }
            if(op.TryGetProperty("headPosition",out _))tag.TagHeadPosition=ToXYZ(ReadPoint3D(op,"headPosition"));
            doc.Regenerate();
            if(tag.IsOrphaned)throw new InvalidOperationException("Tag became orphaned; edit was rolled back.");
            if(op.TryGetProperty("headPosition",out _)&&tag.TagHeadPosition.DistanceTo(ToXYZ(ReadPoint3D(op,"headPosition")))>FFeet(.0001))throw new InvalidOperationException("Revit did not retain the requested tag head position.");
            if(op.TryGetProperty("hasLeader",out var expectedLeader)&&tag.HasLeader!=expectedLeader.GetBoolean())throw new InvalidOperationException("Revit did not retain the requested leader state.");
            if(op.TryGetProperty("orientation",out var expectedOrientation)&&tag.TagOrientation.ToString()!=expectedOrientation.GetString())throw new InvalidOperationException("Revit did not retain the requested tag orientation.");
            if(op.TryGetProperty("rotationDegrees",out var expectedRotation)&&Math.Abs(Math.IEEERemainder(tag.RotationAngle-expectedRotation.GetDouble()*Math.PI/180,Math.PI*2))>1e-6)throw new InvalidOperationException("Revit did not retain the requested tag rotation.");
            foreach(var leader in FArray(op,"leaders"))
            {
                var r=references[leader.GetProperty("referenceIndex").GetInt32()];
                if(leader.TryGetProperty("elbowPosition",out _)&&(!tag.HasLeaderElbow(r)||tag.GetLeaderElbow(r).DistanceTo(ToXYZ(ReadPoint3D(leader,"elbowPosition")))>FFeet(.0001)))throw new InvalidOperationException("Revit changed the requested leader elbow.");
                if(leader.TryGetProperty("endPosition",out _)&&tag.GetLeaderEnd(r).DistanceTo(ToXYZ(ReadPoint3D(leader,"endPosition")))>FFeet(.0001))throw new InvalidOperationException("Revit changed the requested leader end.");
                if(leader.TryGetProperty("visible",out var visible)&&tag.IsLeaderVisible(r)!=visible.GetBoolean())throw new InvalidOperationException("Revit changed the requested leader visibility.");
            }
            var actual=tag.GetTaggedReferences().Select(r=>r.ConvertToStableRepresentation(doc)).Order().ToArray();
            var expected=references.Where((r,i)=>!removals.Contains(i)).Select(r=>r.ConvertToStableRepresentation(doc))
                .Concat(FArray(op,"addLocalElementIds").Select(v=>new Reference(doc.GetElement(new ElementId(v.GetInt64()))).ConvertToStableRepresentation(doc))).Distinct().Order().ToArray();
            if(!actual.SequenceEqual(expected))throw new InvalidOperationException("Tag references differ from the requested set.");
            changes.Add(new {before,after=ATagState(tag)});
        }
        return new {changes};
    }
    private static JsonElement ValidateTagsAfterCommit(Document doc,JsonElement args,JsonElement result)
    {
        foreach(var change in result.GetProperty("changes").EnumerateArray())
        {
            var expected=change.GetProperty("after");var tag=doc.GetElement(new ElementId(expected.GetProperty("id").GetInt64())) as IndependentTag??throw new InvalidOperationException("Tag disappeared during commit.");
            if(!FJsonEquivalent(expected,JsonSerializer.SerializeToElement(ATagState(tag))))throw new RevitOperationException("Tag state changed during commit; edit was rolled back.",new {expected,actual=ATagState(tag)});
        }
        return result;
    }
    private static object ViewAnnotationResources(UIApplication app)
    {
        var doc=RequireDocument(app);return new {resources=AResources(doc),genericAnnotationTypes=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s=>s.Category?.Id.Value==(long)BuiltInCategory.OST_GenericAnnotation).Select(s=>new {id=s.Id.Value,family=s.FamilyName,name=s.Name}).ToArray()};
    }
    private static object PreviewViewAnnotations(UIApplication app,JsonElement args)=>PreviewOperation(app,args,"viewAnnotations","Аннотации видов",EditViewAnnotations,ValidateViewAnnotations);
    private static object ApplyViewAnnotations(UIApplication app,JsonElement args,Func<bool>? cancelled)=>ApplyOperation(app,args,"viewAnnotations","Изменить оформление видов?",EditViewAnnotations,cancelled,ValidateViewAnnotations);
    private static object EditViewAnnotations(Document doc,JsonElement args)
    {
        var ctx=new FamilyBuildContext(doc,"");var touched=new HashSet<ElementId>();
        foreach(var op in FArray(args,"operations"))
        {
            if(FString(op,"action")=="placeGenericAnnotation")
            {
                var view=AView(doc,op);var symbol=doc.GetElement(new ElementId(op.GetProperty("typeId").GetInt64())) as FamilySymbol;
                if(symbol?.Category?.Id.Value!=(long)BuiltInCategory.OST_GenericAnnotation||symbol.Family.FamilyPlacementType!=FamilyPlacementType.ViewBased)throw new ArgumentException("Choose a view-based Generic Annotation FamilySymbol.");
                if(!symbol.IsActive){symbol.Activate();doc.Regenerate();}
                var position=APoint(doc,view,op.GetProperty("position"));var instance=doc.Create.NewFamilyInstance(position,symbol,view);
                if(Math.Abs(FNumber(op,"rotationDegrees"))>1e-9)ElementTransformUtils.RotateElement(doc,instance.Id,Line.CreateBound(position,position+view.ViewDirection),FNumber(op,"rotationDegrees")*Math.PI/180);
                doc.Regenerate();
                if(instance.OwnerViewId!=view.Id||instance.Symbol.Id!=symbol.Id||instance.Location is not LocationPoint location||location.Point.DistanceTo(position)>SheetUnits(.001*AScale(doc,view)))throw new InvalidOperationException("Revit did not retain Generic Annotation placement.");
                ctx.Add(op,instance);continue;
            }
            if(op.TryGetProperty("element",out var reference)&&FString(op,"action")!="replaceAnnotationRegion")touched.Add(ctx.Get(reference).Id);
            if(!FAnnotationOperation(ctx,op))throw new ArgumentException("Unsupported view annotation operation.");
        }
        touched.UnionWith(ctx.Elements.Values);doc.Regenerate();return new {created=ctx.Elements.Select(p=>new {key=p.Key,id=p.Value.Value}).ToArray(),fields=ctx.Fields,
            states=touched.OrderBy(id=>id.Value).Select(id=>AElement(doc,doc.GetElement(id))).ToArray()};
    }
    private static JsonElement ValidateViewAnnotations(Document doc,JsonElement args,JsonElement result)
    {
        foreach(var state in result.GetProperty("states").EnumerateArray())
        {
            var element=doc.GetElement(new ElementId(state.GetProperty("id").GetInt64()));
            if(element is null||!FJsonEquivalent(state,JsonSerializer.SerializeToElement(AElement(doc,element))))throw new RevitOperationException("Annotation state changed during commit; operation rolled back.",new {expected=state});
        }
        return result;
    }
}
