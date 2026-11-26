using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Classification;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Connects a classifier file to the document — the equivalent of Revit's
/// Manage &gt; Classification Settings. After it the «Код по классификатору»
/// picker, list_classification_entries and the classification check of
/// set_parameters all see the same table. The table is a document setting, not
/// an element parameter: it has no change item, so undo_last cannot revert it
/// (Revit Undo does).
/// </summary>
public sealed class LoadClassificationTableTool : AgentWriteToolBase
{
    public LoadClassificationTableTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    // A DataStorage element is not a view: it is created in the active workset.
    protected override bool RequiresActiveWorkset => true;

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "load_classification_table",
        Description =
            "Connects a classification file to the document, like Revit Manage > " +
            "Classification Settings. After it the «Код по классификатору» picker, " +
            "list_classification_entries and the classification check of set_parameters " +
            "all see the table. path: an absolute file path to a Revit classification " +
            "table — one row per line, four tab-separated columns code, description, " +
            "level, category id (the last may be empty); saved as UTF-16 (\"Unicode\" in " +
            "Notepad) when the descriptions are not plain ASCII, otherwise as ASCII or " +
            "UTF-8 without BOM. A missing file returns file_not_found; a file of another " +
            "format or encoding returns classification_file_invalid; a file Revit cannot " +
            "load returns classification_load_failed. Loading the same table again changes " +
            "nothing (no_changes). The table is a document setting: undo_last cannot revert " +
            "it, Revit Undo can. Dry run loads the table and rolls back, reporting the " +
            "entry count. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""Absolute path to the classification table file: tab-separated rows of code, description, level and category id (may be empty); UTF-16 (the Unicode encoding of Notepad) for non-ASCII descriptions, otherwise ASCII or UTF-8 without BOM""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""path""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var path = AgentJson.GetString(args, "path")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass path (the absolute path to the classification table file).";
            return;
        }

        if (!Path.IsPathRooted(path))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "path must be an absolute file path, got '" + path + "'.";
            return;
        }

        var fileName = Path.GetFileName(path);
        outcome.Description = "Load classification table " + fileName;
        outcome.Data["path"] = path;

        var result = AgentClassificationLoader.Load(doc, path!);
        if (result.Problems.Count > 0)
            outcome.Data["problems"] = result.Problems;

        switch (result.Status)
        {
            case AgentClassificationLoader.Status.FileNotFound:
                outcome.Success = false;
                outcome.ErrorCode = "file_not_found";
                outcome.Message = "The classification file '" + path + "' does not exist. " +
                                  "Ask the user which classifier file to connect instead of " +
                                  "guessing a path.";
                return;

            case AgentClassificationLoader.Status.InvalidFormat:
                outcome.Success = false;
                outcome.ErrorCode = "classification_file_invalid";
                outcome.Message = "The file '" + fileName + "' is not a classification table " +
                                  "Revit can read: " + string.Join("; ", result.Problems) + ". " +
                                  AgentClassificationLoader.ExpectedFormat;
                return;

            case AgentClassificationLoader.Status.UnreadableText:
                // A real apply must not commit a table whose text nobody can read.
                outcome.Aborted = !dryRun;
                outcome.Success = false;
                outcome.ErrorCode = "classification_file_invalid";
                outcome.Message = "This Revit does not read the encoding of '" + fileName +
                                  "': " + string.Join("; ", result.Problems) + ". Save the table " +
                                  "as UTF-16 (\"Unicode\" in Notepad: Save As > Encoding) — or " +
                                  "keep it to plain ASCII — and load it again.";
                return;

            case AgentClassificationLoader.Status.LoadFailed:
                outcome.Success = false;
                outcome.ErrorCode = "classification_load_failed";
                outcome.Message = "Revit did not load the classification table from '" +
                                  fileName + "': " + string.Join("; ", result.Problems) + ". " +
                                  AgentClassificationLoader.ExpectedFormat;
                return;
        }

        foreach (var problem in result.Problems)
            outcome.Warnings.Add(problem);

        outcome.Data["source"] = result.Source;
        outcome.Data["entriesTotal"] = result.EntryCount;
        outcome.Data["leafTotal"] = result.LeafCount;

        if (result.Status == AgentClassificationLoader.Status.AlreadyCurrent)
        {
            outcome.ErrorCode = "no_changes";
            outcome.Message = "The document already loads this classification table from this " +
                              "file (" + result.EntryCount + " entries); nothing to change.";
            return;
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = fileName,
            OldValue = result.PreviousSource,
            NewValue = result.EntryCount + " entries",
            Reason = dryRun
                ? "Dry run: the table would be connected to the document."
                : "Classification table connected to the document.",
            Detail = "document classification table (Assembly Code)"
        });
        outcome.AffectedCount = 1;

        if (dryRun)
        {
            outcome.Data["wouldLoad"] = true;
            return;
        }

        outcome.SuppressChangeSet = true;
        outcome.Data["undoSupported"] = false;
        outcome.Warnings.Add(
            "The classification table is a document setting, not an element parameter: " +
            "undo_last cannot revert it — use Revit Undo (Ctrl+Z), the step named '" +
            Definition.Name + "'.");
    }

    /// <summary>
    /// The numbers of a real apply are read back after the commit, not from the
    /// transaction that made them.
    /// </summary>
    protected override void AfterTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct)
    {
        if (dryRun || !outcome.Success || outcome.AffectedCount == 0)
            return;

        var table = AgentClassificationReader.Read(document);
        if (!table.Loaded)
        {
            outcome.Success = false;
            outcome.ErrorCode = "classification_load_failed";
            outcome.Message = "The transaction committed, but the document reads back no " +
                              "classification entry. " + AgentClassificationLoader.ExpectedFormat;
            outcome.Batch.Changed.Clear();
            outcome.AffectedCount = 0;
            outcome.ModelChanged = false;
            return;
        }

        outcome.Data["entriesTotal"] = table.Entries.Count;
        outcome.Data["leafTotal"] = table.Entries.Count(entry => entry.IsLeaf);
        outcome.Data["source"] = table.Source;
    }
}
