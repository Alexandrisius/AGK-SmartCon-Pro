using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Loads a family (.rfa file) into the project. When a family with the same
/// name is already loaded, overwrite=false refuses with
/// <c>family_already_loaded</c> and overwrite=true reloads the file over it
/// (undo of an overwrite is not supported).
/// </summary>
public sealed class LoadFamilyTool : AgentWriteToolBase
{
    public LoadFamilyTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "load_family",
        Description =
            "Loads a family from an absolute .rfa file path into the project. A missing file " +
            "returns file_not_found. A family with the same name already loaded: overwrite=false " +
            "refuses with family_already_loaded (the existing familyName is returned), " +
            "overwrite=true reloads the file over it — that reload is not reversible by " +
            "undo_last. Loading a new family is undone by deleting the loaded family. Dry run " +
            "checks the file and the name without opening it. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""Absolute path to the .rfa file""},""overwrite"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""path""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var path = AgentJson.GetString(args, "path")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass path (the absolute path to the .rfa file).";
            return;
        }

        if (!File.Exists(path))
        {
            outcome.Success = false;
            outcome.ErrorCode = "file_not_found";
            outcome.Message = "The family file '" + path + "' does not exist. Check the path.";
            return;
        }

        if (!string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "The path must point to a .rfa family file: '" + path + "'.";
            return;
        }

        var overwrite = AgentJson.GetBool(args, "overwrite", false);
        var familyName = Path.GetFileNameWithoutExtension(path);
        var existing = FindFamilyByName(doc, familyName);

        outcome.Description = "Load family " + familyName;
        outcome.Data["fileName"] = Path.GetFileName(path);
        outcome.Data["familyName"] = familyName;

        if (existing is not null && !overwrite)
        {
            outcome.Success = false;
            outcome.ErrorCode = "family_already_loaded";
            outcome.Message = "Family '" + existing.Name + "' is already loaded in the project " +
                              "(familyId " + existing.Id.GetValue() + "). Pass overwrite=true to " +
                              "reload the file over it.";
            outcome.Data["existingFamilyId"] = existing.Id.GetValue();
            outcome.Data["existingFamilyName"] = existing.Name;
            return;
        }

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = familyName,
                NewValue = existing is null
                    ? "loaded from " + Path.GetFileName(path)
                    : "reloaded over the existing family (overwrite)",
                Reason = "Dry run: the file exists and would be " +
                         (existing is null ? "loaded" : "reloaded") + "."
            });
            outcome.AffectedCount = 1;
            outcome.Data["overwrite"] = overwrite;
            return;
        }

        RevitFamily loadedFamily;
        var loaded = false;
        try
        {
            loaded = doc.LoadFamily(path, new LoadOptions(overwrite), out loadedFamily);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "load_failed";
            outcome.Message = "Revit refused to load '" + path + "': " + ex.GetType().Name +
                              ": " + ex.Message;
            return;
        }

        if (!loaded || loadedFamily is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "load_failed";
            outcome.Message = "Revit did not load the family '" + familyName + "' from '" + path +
                              "'. The file may be from a newer Revit version or damaged.";
            return;
        }

        var symbolIds = loadedFamily.GetFamilySymbolIds();
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = loadedFamily.Id.GetValue(),
            Name = loadedFamily.Name,
            NewValue = (existing is null ? "loaded" : "reloaded") + " (" +
                       symbolIds.Count + " type(s))"
        });
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = loadedFamily.UniqueId,
            ElementId = loadedFamily.Id.GetValue(),
            ParameterName = existing is null ? "family_loaded" : "family_overwritten",
            OldValue = path,
            NewValue = loadedFamily.Name,
            Status = ChangeStatus.Applied
        });

        outcome.Data["familyId"] = loadedFamily.Id.GetValue();
        outcome.Data["familyName"] = loadedFamily.Name;
        outcome.Data["symbolCount"] = symbolIds.Count;
        outcome.Data["wasOverwrite"] = existing is not null;
        if (existing is not null)
        {
            outcome.Data["undoSupported"] = false;
            outcome.Warnings.Add(
                "The family was reloaded over an existing one; undo_last will not restore the " +
                "previous version. Reload the previous file manually if needed.");
        }
    }

    private static RevitFamily? FindFamilyByName(Document doc, string familyName)
    {
        foreach (var family in AgentFamilyReader.LoadableFamilies(doc))
        {
            if (string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase))
                return family;
        }

        return null;
    }

    private sealed class LoadOptions : IFamilyLoadOptions
    {
        private readonly bool _overwrite;

        public LoadOptions(bool overwrite)
        {
            _overwrite = overwrite;
        }

        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = _overwrite;
            return _overwrite;
        }

        public bool OnSharedFamilyFound(
            RevitFamily sharedFamily,
            bool familyInUse,
            out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = _overwrite;
            return _overwrite;
        }
    }
}
