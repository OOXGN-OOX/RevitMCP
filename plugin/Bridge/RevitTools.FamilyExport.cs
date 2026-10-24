using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private sealed record FamilyExportPlan(bool Nested, JsonElement Args, DateTime Created, Document? Owner, long Revision,
        JsonElement FamilyIdentity, Dictionary<string,string> Hashes);
    private static readonly Dictionary<string,FamilyExportPlan> FamilyExportPlans = new();

    private static Family ExportSelectFamily(Document source, JsonElement args, bool nested)
    {
        var idKey=nested ? "nestedFamilyId" : "familyId"; var nameKey=nested ? "nestedFamilyName" : "familyName";
        if(args.TryGetProperty(idKey,out var id)==args.TryGetProperty(nameKey,out _)) throw new ArgumentException($"Specify {idKey} XOR {nameKey}.");
        Family family;
        if(id.ValueKind!=JsonValueKind.Undefined)
            family=source.GetElement(new ElementId(id.GetInt64())) as Family ?? throw new ArgumentException("The ID must identify a Family, not a FamilySymbol or instance.");
        else
        {
            var matches=new FilteredElementCollector(source).OfClass(typeof(Family)).Cast<Family>()
                .Where(f=>string.Equals(f.Name,FString(args,nameKey),StringComparison.Ordinal)).ToArray();
            if(matches.Length!=1) throw new ArgumentException("The exact family name was not found or is ambiguous; use the family ID.");
            family=matches[0];
        }
        if(source.IsFamilyDocument && family.Id==source.OwnerFamily.Id) throw new ArgumentException("Select a nested family, not the owner family.");
        if(family.IsInPlace || !family.IsEditable) throw new ArgumentException("In-place and non-editable families cannot be exported with EditFamily.");
        return family;
    }

    private static object PreviewExportFamily(UIApplication app, JsonElement args, bool nested, Func<bool>? cancelled)
    {
        foreach(var key in FamilyExportPlans.Where(p=>DateTime.UtcNow-p.Value.Created>PlanLifetime || p.Value.Owner is { IsValidObject:false }).Select(p=>p.Key).ToArray()) FamilyExportPlans.Remove(key);
        if(FamilyExportPlans.Count>=30) throw new InvalidOperationException("Too many pending family export plans.");
        var owner=nested ? null : RequireDocument(app);
        if(owner is not null) RequireWritableProject(owner);
        var hashes=nested ? new Dictionary<string,string>{{FPath(args,"sourcePath",".rfa"),FHash(FPath(args,"sourcePath",".rfa"))}} : new();
        var preview=ExportFamilyCopy(app,args,nested,false,cancelled,null,()=>FVerifyDependencies(hashes));
        var id=Guid.NewGuid().ToString("N");
        FamilyExportPlans[id]=new(nested,args.Clone(),DateTime.UtcNow,owner,owner is null ? 0 : DocumentRevision(owner),
            preview.GetProperty("familyIdentity").Clone(),hashes);
        return new { planCreated=true,planId=id,expiresInMinutes=10,modelChanged=false,outputCreated=false,preview };
    }

    private static object ApplyExportFamily(UIApplication app, JsonElement args, bool nested, Func<bool>? cancelled)
    {
        var id=FString(args,"planId");
        if(!FamilyExportPlans.TryGetValue(id,out var plan) || plan.Nested!=nested || DateTime.UtcNow-plan.Created>PlanLifetime)
            throw new ArgumentException("Family export plan expired or not found; prepare a new preview.");
        void Verify()
        {
            FVerifyDependencies(plan.Hashes);
            if(plan.Owner is not null && (!plan.Owner.IsValidObject || !plan.Owner.Equals(RequireDocument(app)) || DocumentRevision(plan.Owner)!=plan.Revision))
                throw new InvalidOperationException("The active project or its revision changed after export preview.");
        }
        Verify();
        using var dialog=new TaskDialog("Revit MCP") { MainInstruction="Выгрузить семейство в новый RFA?",MainContent=FString(plan.Args,"outputPath"),CommonButtons=TaskDialogCommonButtons.Yes|TaskDialogCommonButtons.No,DefaultButton=TaskDialogResult.No };
        if(cancelled?.Invoke()==true || !ConfirmOperation(dialog) || cancelled?.Invoke()==true) return new { applied=false,cancelled=true };
        Verify();
        var result=ExportFamilyCopy(app,plan.Args,nested,true,cancelled,plan.FamilyIdentity,Verify);
        FamilyExportPlans.Remove(id);
        return new { applied=true,modelChanged=false,result };
    }

    private static JsonElement ExportFamilyCopy(UIApplication app,JsonElement args,bool nested,bool publish,Func<bool>? cancelled,JsonElement? expectedIdentity,Action verify)
    {
        var output=FPath(args,"outputPath",".rfa",false);
        if(File.Exists(output)||Directory.Exists(output)||!Directory.Exists(Path.GetDirectoryName(output))) throw new ArgumentException("Output must be a new RFA in an existing local directory.");
        var sourcePath=nested ? FPath(args,"sourcePath",".rfa") : null;
        if(string.Equals(sourcePath,output,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must differ from source.");
        var outputDirectory=Path.GetDirectoryName(output)!;
        var scratch=Path.GetFullPath(Path.Combine(outputDirectory,".revitmcp-"+Guid.NewGuid().ToString("N")));
        if(!string.Equals(Path.GetDirectoryName(scratch),outputDirectory,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Temporary directory must remain in the output directory.");
        Document? source=null,edit=null;
        Directory.CreateDirectory(scratch);
        try
        {
            if(cancelled?.Invoke()==true) throw new OperationCanceledException();
            source=nested ? FOpen(app,sourcePath!) : RequireDocument(app);
            if(source.IsReadOnly || source.IsModifiable) throw new InvalidOperationException("EditFamily requires a document without an open transaction or read-only state.");
            var family=expectedIdentity.HasValue ? ResolveStateFamily(source,expectedIdentity.Value) : ExportSelectFamily(source,args,nested);
            if(family.IsInPlace||!family.IsEditable) throw new ArgumentException("The family is no longer editable.");
            var familyId=family.Id.Value; var familyName=family.Name;
            // Project reload is identified by file name; prevent exporting a misleading renamed replacement.
            if(!nested && !string.Equals(Path.GetFileNameWithoutExtension(output),familyName,StringComparison.Ordinal))
                throw new ArgumentException("To preserve project family identity, outputPath file name must be exactly '"+familyName+".rfa'. Use a new directory for additional copies.");
            var sourceState=FamilyInstanceState(source,family);
            var familyIdentity=JsonSerializer.SerializeToElement(new { familyId,family=familyName,types=sourceState.GetProperty("types") });
            if(expectedIdentity.HasValue)
            {
                if(!FJsonEquivalent(expectedIdentity.Value.GetProperty("family"),familyIdentity.GetProperty("family"))
                    || !FJsonEquivalent(expectedIdentity.Value.GetProperty("types"),familyIdentity.GetProperty("types")))
                    throw new InvalidOperationException("The selected family symbols or their identity changed after preview.");
            }
            edit=source.EditFamily(family);
            var before=FSnapshot(edit);
            var staged=Path.Combine(scratch,familyName+".rfa");
            edit.SaveAs(staged,new SaveAsOptions{OverwriteExistingFile=false,MaximumBackups=1});
            FClose(edit); edit=null;
            var verified=FValidate(app,staged,JsonSerializer.SerializeToElement(new {}),before,cancelled);
            var loadArgs=JsonSerializer.SerializeToElement(new { path=staged,sha256=FHash(staged),parameterPolicy="preserve",sharedFamilySource="project" });
            var roundTrip=SimulateOperation(source,loadArgs,"Контрольная обратная загрузка семейства",LoadFamilyOperation,ValidateFamilyLoadAfterCommit);
            var rolledBackState=FamilyInstanceState(source,ResolveStateFamily(source,sourceState));
            RequireFamilyStatePreserved(sourceState,rolledBackState);
            // Additions are expected during a reload, but none may remain after its transaction group is rolled back.
            if(!FJsonEquivalent(sourceState.GetProperty("types"),rolledBackState.GetProperty("types"))
                || sourceState.GetProperty("instances").GetArrayLength()!=rolledBackState.GetProperty("instances").GetArrayLength())
                throw new InvalidOperationException("The round-trip rollback left additional family types or instances in the source document.");
            if(cancelled?.Invoke()==true) throw new OperationCanceledException();
            verify();
            var result=JsonSerializer.SerializeToElement(new { outputPath=output,saved=publish,sourcePath,familyId=rolledBackState.GetProperty("familyId").GetInt64(),familyUniqueId=ResolveStateFamily(source,sourceState).UniqueId,familyName,familyIdentity,
                reloadFileName=familyName+".rfa",roundTripPassed=true,roundTrip,verified,modelChanged=false });
            // Close every temporary document before publishing; a close failure must not leave a reported-success output.
            if(nested) { FClose(source); source=null; }
            verify();
            if(cancelled?.Invoke()==true) throw new OperationCanceledException();
            if(publish) File.Move(staged,output,overwrite:false);
            return result;
        }
        finally
        {
            try { FClose(edit); }
            finally
            {
                try { if(nested) FClose(source); }
                finally { if(Directory.Exists(scratch)) Directory.Delete(scratch,recursive:true); }
            }
        }
    }
}
