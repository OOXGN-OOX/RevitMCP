using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static void RequireWritableParameterDocument(Document document)
    {
        if(document.IsReadOnly||document.IsModifiable) throw new InvalidOperationException("A writable document without an open transaction is required.");
    }
    private static Parameter SelectParameter(Document doc,Element source,JsonElement args)
    {
        var scope=JsonTools.GetString(args,"parameterScope","instance");
        if(scope is not ("instance" or "type")) throw new ArgumentException("parameterScope must be instance or type.");
        var owner=scope=="type"&&source is not ElementType ? doc.GetElement(source.GetTypeId()) : source;
        if(owner is null) throw new ArgumentException("The element has no parameter owner for the requested scope.");
        var selectors=new[]{"parameterName","parameterId","parameterGuid"}.Where(k=>args.TryGetProperty(k,out _)).ToArray();
        if(selectors.Length!=1) throw new ArgumentException("Specify exactly one parameterName, parameterId or parameterGuid.");
        var parameters=selectors[0] switch {
            "parameterName"=>owner.GetParameters(JsonTools.GetString(args,"parameterName").Trim()).ToArray(),
            "parameterGuid"=>owner.Parameters.Cast<Parameter>().Where(p=>p.IsShared&&p.GUID==Guid.Parse(JsonTools.GetString(args,"parameterGuid"))).ToArray(),
            _=>owner.Parameters.Cast<Parameter>().Where(p=>p.Id.Value==args.GetProperty("parameterId").GetInt64()).ToArray() };
        return parameters.Length==1 ? parameters[0] : throw new ArgumentException("Parameter is missing or ambiguous; use its exact ID or shared GUID and scope.");
    }
    private static Family? ParameterOwnerFamily(Document doc,Element owner) => owner switch {
        FamilyInstance instance=>instance.Symbol.Family,FamilySymbol symbol=>symbol.Family,_=>doc.IsFamilyDocument ? doc.OwnerFamily : null };

    private static void ValidateElementIdReference(Document doc,ForgeTypeId dataType,ElementId id,Family? family,ElementId parameterId)
    {
        if(id.Value>0&&doc.GetElement(id) is null) throw new ArgumentException("The referenced ElementId does not exist in this document.");
        if(dataType==SpecTypeId.Reference.Material&&id.Value>0&&doc.GetElement(id) is not Material) throw new ArgumentException("A material parameter requires a Material ID.");
        if(Category.IsBuiltInCategory(dataType))
        {
            if(id.Value<0&&id!=ElementId.InvalidElementId) throw new ArgumentException("A family-type reference accepts an applicable type ID or -1.");
            if(id.Value>0&&(family is null||!family.GetFamilyTypeParameterValues(parameterId).Contains(id)))
                throw new ArgumentException("The referenced type is not an applicable value of this family-type parameter.");
        }
    }
    private static object ParameterReferenceInfo(Document doc,ElementId id)
    {
        var element=id.Value>0 ? doc.GetElement(id) : null;
        return new { elementId=id.Value,elementIdText=id.Value.ToString(CultureInfo.InvariantCulture),uniqueId=element?.UniqueId,
            name=element is NestedFamilyTypeReference nt ? nt.TypeName : element?.Name,nativeClass=element?.GetType().Name,
            family=element is NestedFamilyTypeReference nested ? nested.FamilyName : element is FamilySymbol symbol ? symbol.FamilyName : null,
            type=element is NestedFamilyTypeReference reference ? reference.TypeName : element is ElementType ? element.Name : null,
            categoryId=element is NestedFamilyTypeReference n ? n.CategoryId.Value : element?.Category?.Id.Value };
    }
    private static object GetParameterValueOptions(UIApplication app,JsonElement args)
    {
        var doc=RequireDocument(app);
        var source=doc.GetElement(new ElementId(args.GetProperty("elementId").GetInt64()))??throw new ArgumentException("Element not found.");
        var parameter=SelectParameter(doc,source,args);
        if(parameter.StorageType!=StorageType.ElementId) throw new ArgumentException("The selected parameter does not store an ElementId.");
        var spec=parameter.Definition.GetDataType(); var family=ParameterOwnerFamily(doc,parameter.Element);
        var familyType=Category.IsBuiltInCategory(spec);
        IEnumerable<ElementId> candidates=familyType&&family is not null ? family.GetFamilyTypeParameterValues(parameter.Id)
            : spec==SpecTypeId.Reference.Material ? new FilteredElementCollector(doc).OfClass(typeof(Material)).ToElementIds() : [];
        var ids=candidates.Where(id=>id.Value>0).OrderBy(id=>id.Value).ToArray();
        var offset=JsonTools.GetInt(args,"offset",0);var limit=JsonTools.GetInt(args,"limit",100);
        if(offset<0||limit is <1 or >500) throw new ArgumentException("Invalid parameter value page.");
        return new { ownerId=parameter.Element.Id.Value,parameterId=parameter.Id.Value,parameter=parameter.Definition.Name,
            scope=parameter.Element is ElementType ? "type" : "instance",
            storageType="ElementId",dataType=spec.TypeId,readOnly=parameter.IsReadOnly,current=ParameterReferenceInfo(doc,parameter.AsElementId()),
            exhaustive=familyType&&family is not null||spec==SpecTypeId.Reference.Material,
            totalCount=ids.Length,offset,limit,hasMore=offset+limit<ids.Length,items=ids.Skip(offset).Take(limit).Select(id=>ParameterReferenceInfo(doc,id)).ToArray() };
    }

    private static object PreviewSetParameter(UIApplication app,JsonElement args)
    {
        var doc=RequireDocument(app);RequireWritableParameterDocument(doc);
        var input=ParameterValueInput.Read(args);var requested=ReadRequestedIds(args);
        var targets=new List<object>();var skipped=new List<object>();var owners=new HashSet<long>();
        foreach(var id in requested.Distinct())
        {
            try
            {
                var source=doc.GetElement(new ElementId(id))??throw new ArgumentException("Element not found.");
                var p=SelectParameter(doc,source,args);
                if(p.IsReadOnly) throw new ArgumentException("Parameter is read-only or formula-driven.");
                if(input.ElementId.HasValue&&p.StorageType!=StorageType.ElementId) throw new ArgumentException("elementIdValue requires an ElementId parameter.");
                if(p.StorageType==StorageType.None) throw new ArgumentException("Unsupported parameter storage type.");
                if(p.StorageType==StorageType.ElementId) ValidateElementIdReference(doc,p.Definition.GetDataType(),new ElementId(input.ReferenceId()),ParameterOwnerFamily(doc,p.Element),p.Id);
                if(!owners.Add(p.Element.Id.Value)) continue; // Several requested instances may share one type parameter.
                targets.Add(new { ownerId=p.Element.Id.Value,ownerUniqueId=p.Element.UniqueId,parameterId=p.Id.Value,parameter=p.Definition.Name,
                    storageType=p.StorageType.ToString(),dataType=p.Definition.GetDataType().TypeId,before=RawParameterValue(p),hasValue=p.HasValue });
            }
            catch(ArgumentException error) { skipped.Add(new { id,reason=error.Message }); }
        }
        if(targets.Count==0) return new {planCreated=false,changedCount=0,skipped};
        var bound=JsonNode.Parse(args.GetRawText())!.AsObject();
        bound["parameterTargets"]=JsonSerializer.SerializeToNode(targets);bound["skipped"]=JsonSerializer.SerializeToNode(skipped);
        return PreviewOperation(app,JsonSerializer.SerializeToElement(bound),"parameter","Изменение параметра",SetParametersOperation,VerifyParametersAfterCommit,allowFamilyDocument:true);
    }
    private static object ApplyParameterPlan(UIApplication app,JsonElement args,Func<bool>? cancelled) =>
        ApplyOperation(app,args,"parameter","Изменить проверенные параметры?",SetParametersOperation,cancelled,VerifyParametersAfterCommit,allowFamilyDocument:true);

    private static Parameter BoundParameter(Document doc,JsonElement target)
    {
        var owner=doc.GetElement(new ElementId(target.GetProperty("ownerId").GetInt64()));
        if(owner is null||owner.UniqueId!=target.GetProperty("ownerUniqueId").GetString()) throw new InvalidOperationException("The parameter owner was deleted or replaced after preview.");
        var parameter=owner.Parameters.Cast<Parameter>().SingleOrDefault(p=>p.Id.Value==target.GetProperty("parameterId").GetInt64());
        if(parameter is null||parameter.Definition.Name!=target.GetProperty("parameter").GetString()||parameter.StorageType.ToString()!=target.GetProperty("storageType").GetString()
            ||parameter.Definition.GetDataType().TypeId!=target.GetProperty("dataType").GetString())
            throw new InvalidOperationException("The selected parameter identity changed after preview.");
        return parameter;
    }
    private static object SetParametersOperation(Document doc,JsonElement args)
    {
        var input=ParameterValueInput.Read(args);var changes=new List<object>();var changedCount=0;
        foreach(var target in args.GetProperty("parameterTargets").EnumerateArray())
        {
            var parameter=BoundParameter(doc,target);
            if(parameter.IsReadOnly||RawParameterValue(parameter)!=target.GetProperty("before").GetString()||parameter.HasValue!=target.GetProperty("hasValue").GetBoolean())
                throw new InvalidOperationException("The parameter became read-only or its value changed after preview.");
            var before=RawParameterValue(parameter);var displayBefore=DisplayParameterValue(parameter);var hadValue=parameter.HasValue;
            try { SetCheckedParameter(doc,parameter,input); }
            catch(Exception error)
            {
                throw new RevitOperationException("Revit rejected the requested parameter value: "+error.Message,
                    new {target,requested=input,exceptionType=error.GetType().FullName});
            }
            var changed=before!=RawParameterValue(parameter)||hadValue!=parameter.HasValue;
            if(changed) changedCount++;
            changes.Add(new { ownerId=parameter.Element.Id.Value,parameterId=parameter.Id.Value,parameter=parameter.Definition.Name,storageType=parameter.StorageType.ToString(),
                before,after=RawParameterValue(parameter),displayBefore,displayAfter=DisplayParameterValue(parameter),hasValue=parameter.HasValue,changed });
        }
        return new {changedCount,changes,skipped=args.GetProperty("skipped")};
    }
    private static void SetCheckedParameter(Document doc,Parameter parameter,ParameterValueInput input)
    {
        string? expected=null;bool changed;
        switch(parameter.StorageType)
        {
            case StorageType.ElementId:
                var id=new ElementId(input.ReferenceId());
                ValidateElementIdReference(doc,parameter.Definition.GetDataType(),id,ParameterOwnerFamily(doc,parameter.Element),parameter.Id);
                expected=id.Value.ToString(CultureInfo.InvariantCulture);
                changed=parameter.HasValue&&parameter.AsElementId()==id||parameter.Set(id);break;
            case StorageType.String:
                expected=input.Text??throw new ArgumentException("This parameter requires value.");
                changed=parameter.HasValue&&parameter.AsString()==expected||parameter.Set(expected);break;
            case StorageType.Integer:
                if(!int.TryParse(input.Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out var number)) throw new ArgumentException("This parameter requires an integer string.");
                expected=number.ToString(CultureInfo.InvariantCulture);
                changed=parameter.HasValue&&parameter.AsInteger()==number||parameter.Set(number);break;
            case StorageType.Double: changed=parameter.SetValueString(input.Text??throw new ArgumentException("This parameter requires value."));break;
            default: throw new ArgumentException("Unsupported parameter storage type.");
        }
        doc.Regenerate();
        if(!changed||(expected is not null&&RawParameterValue(parameter)!=expected)) throw new InvalidOperationException("Revit rejected or changed the requested parameter value.");
    }
    private static JsonElement VerifyParametersAfterCommit(Document doc,JsonElement args,JsonElement result)
    {
        var changes=result.GetProperty("changes").EnumerateArray().ToArray();
        var targets=args.GetProperty("parameterTargets").EnumerateArray().ToArray();
        for(var i=0;i<targets.Length;i++)
            if(RawParameterValue(BoundParameter(doc,targets[i]))!=changes[i].GetProperty("after").GetString()
                ||BoundParameter(doc,targets[i]).HasValue!=changes[i].GetProperty("hasValue").GetBoolean())
                throw new RevitOperationException("Parameter value changed during commit; the operation was rolled back.",new {target=targets[i],expected=changes[i]});
        return result;
    }
}
