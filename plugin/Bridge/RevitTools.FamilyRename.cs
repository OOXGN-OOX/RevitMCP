using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace RevitMCP;

internal static partial class RevitTools
{
    private static object PreviewRenameFamily(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "renameFamily", "Переименование семейства или типоразмера", RenameFamily);

    private static object ApplyRenameFamily(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "renameFamily", "Переименовать семейство или типоразмер?", RenameFamily, cancelled);

    private static object RenameFamily(Document doc, JsonElement args)
    {
        var element = doc.GetElement(new ElementId(HostedReadId(args, "elementId")));
        if (element is not Family && element is not FamilySymbol)
            throw new ArgumentException("elementId must identify a Family or FamilySymbol in the active project, not an instance or a system type.");
        var name = JsonTools.GetString(args, "newName").Trim();
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl) || !NamingUtils.IsValidName(name))
            throw new ArgumentException("newName must contain 1 to 200 characters and be a valid Revit name without control characters.");

        var family = element is Family f ? f : ((FamilySymbol)element).Family;
        var symbols = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).ToArray();
        var peers = element is Family
            ? new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(other => other.FamilyCategory?.Id == family.FamilyCategory?.Id).Cast<Element>()
            : symbols.AsEnumerable();
        var conflict = peers.FirstOrDefault(other => other.Id != element.Id && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
            throw new ArgumentException($"Name '{name}' is already used by {conflict.GetType().Name} {conflict.Id.Value} in the same naming scope.");

        var oldName = element.Name;
        var changed = !string.Equals(oldName, name, StringComparison.Ordinal);
        if (changed) element.Name = name;
        doc.Regenerate();
        if (element.Name != name) throw new InvalidOperationException("Revit did not retain the requested name.");
        return new
        {
            elementId = element.Id.Value, kind = element is Family ? "Family" : "FamilySymbol",
            oldName, newName = element.Name, changed, familyId = family.Id.Value, familyName = family.Name,
            affectedSymbolIds = element is Family ? symbols.Select(s => s.Id.Value).Order().ToArray() : new[] { element.Id.Value }
        };
    }
}
