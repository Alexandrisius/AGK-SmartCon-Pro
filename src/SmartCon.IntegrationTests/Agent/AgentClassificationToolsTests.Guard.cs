using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// The classification check of the set_parameters tool: an Assembly Code is
/// only written when a row of the table loaded in the document backs it,
/// unless the caller opts in. Partial of
/// <see cref="AgentClassificationToolsTests"/>.
/// </summary>
public sealed partial class AgentClassificationToolsTests
{
    private const string TableNotLoaded = "classification_table_not_loaded";
    private const string CodeNotFound = "classification_code_not_found";

    private string DescriptionParameterOf(long typeId) =>
        Doc.GetElement(ElementIdCompat.Create(typeId))?.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString()
        ?? string.Empty;

    /// <summary>Warnings of the classification check itself (a type-scope write has its own blast-radius warning).</summary>
    private static int UnverifiedWarnings(ToolResult result) =>
        result.Warnings.Count(warning => warning.Contains("written unverified"));

    private static JsonElement Failed(ToolResult result, int index = 0) =>
        result.Data!.Value.GetProperty("failed")[index];

    private static JsonElement Changed(ToolResult result, int index = 0) =>
        result.Data!.Value.GetProperty("changed")[index];

    // ── no classifier in the document ───────────────────────────────────

    [Test]
    public async Task Set_WithoutTable_AnyCodeIsRefusedAsTableNotLoaded()
    {
        RequireWallType();

        var result = Set("[" + CodeItem(WallTypeId, "20.10.10") + "]");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo(TableNotLoaded);
        await Assert.That(result.Data!.Value.GetProperty("failed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(Failed(result).GetProperty("errorCode").GetString()).IsEqualTo(TableNotLoaded);
        await Assert.That(Failed(result).GetProperty("reason").GetString()).Contains("load_classification_table");
        await Assert.That(result.Message).Contains("No classification table is loaded");
        await Assert.That(CodeOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Set_WithoutTable_DryRun_RefusesTheCodeAsWell()
    {
        RequireWallType();

        var result = Set("[" + CodeItem(WallTypeId, "20.10.10") + "]", dryRun: true);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Failed(result).GetProperty("errorCode").GetString()).IsEqualTo(TableNotLoaded);
        await Assert.That(CodeOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
        await Assert.That(Doc.IsModified).IsFalse();
    }

    [Test]
    public async Task Set_WithoutTable_AllowUnverified_WritesTheCodeAndWarns()
    {
        RequireWallType();

        var result = Set("[" + CodeItem(WallTypeId, "20.10.10") + "]", allowUnverified: true);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(CodeOf(WallTypeId)).IsEqualTo("20.10.10");
        await Assert.That(DescriptionOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
        await Assert.That(UnverifiedWarnings(result)).IsEqualTo(1);
        await Assert.That(result.Warnings[0]).Contains("written unverified (" + TableNotLoaded + ")");
        await Assert.That(Changed(result).GetProperty("reason").GetString()).Contains("Written unverified");
        await Assert.That(_changeSets!.Last!.Items[0].NewValue).IsEqualTo("20.10.10");
    }

    [Test]
    public async Task Set_WithoutTable_OtherParametersAreNotTouchedByTheCheck()
    {
        RequireWallType();

        var result = Set(
            "[{\"elementId\":" + WallTypeId + ",\"parameter\":\"Description\",\"value\":\"Free text\",\"scope\":\"type\"}]");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(DescriptionParameterOf(WallTypeId)).IsEqualTo("Free text");
        await Assert.That(UnverifiedWarnings(result)).IsEqualTo(0);
    }

    [Test]
    public async Task Set_WithoutTable_OptInOnAnUnrelatedWrite_WarnsNothing()
    {
        RequireWallType();

        var result = Set(
            "[{\"elementId\":" + WallTypeId + ",\"parameter\":\"Description\",\"value\":\"Free text\",\"scope\":\"type\"}]",
            allowUnverified: true);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(UnverifiedWarnings(result)).IsEqualTo(0);
    }

    // ── a classifier is loaded ───────────────────────────────────────────

    [Test]
    public async Task Set_CodeNotInTable_IsRefusedWithTheClosestCodes()
    {
        RequireWallType();
        await Assert.That(Load(SampleFile("guard-missing.txt"), dryRun: false).Success).IsTrue();

        var result = Set("[" + CodeItem(WallTypeId, "20.10.99") + "]");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Failed(result).GetProperty("errorCode").GetString()).IsEqualTo(CodeNotFound);
        var reason = Failed(result).GetProperty("reason").GetString()!;
        await Assert.That(reason).Contains("20.10.99");
        await Assert.That(reason).Contains("20.10.10");
        await Assert.That(reason).Contains("list_classification_entries");
        await Assert.That(reason).Contains("allowUnverifiedClassification=true");
        await Assert.That(CodeOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Set_CodeInTable_IsWrittenAndRevitFillsTheDescription()
    {
        RequireWallType();
        await Assert.That(Load(SampleFile("guard-verified.txt"), dryRun: false).Success).IsTrue();

        var result = Set("[" + CodeItem(WallTypeId, "20.10.10") + "]");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(CodeOf(WallTypeId)).IsEqualTo("20.10.10");
        await Assert.That(DescriptionOf(WallTypeId)).IsEqualTo("Pipes");
        var detail = Changed(result).GetProperty("detail").GetString()!;
        await Assert.That(detail).Contains("Services › Pipelines › Pipes");
        await Assert.That(detail).Contains("description: Pipes");
        await Assert.That(result.Warnings.Any(warning => warning.Contains("unverified"))).IsFalse();

        // One change item for one write: the description is read-only and is
        // recomputed by Revit from whatever code it holds.
        var set = _changeSets!.Last!;
        await Assert.That(set.Items.Count).IsEqualTo(1);
        await Assert.That(set.Items[0].NewValue).IsEqualTo("20.10.10");
    }

    [Test]
    public async Task Set_ThroughAWallInstanceWithScopeType_ChecksAndWritesTheTypeCode()
    {
        RequireWallType();
        await Assert.That(Load(SampleFile("guard-instance.txt"), dryRun: false).Success).IsTrue();
        var wallId = 0L;
        _transactions!.RunInTransaction(Doc, "Seed a wall", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            wallId = ModelSeed.TryCreateWall(doc, level.Id)?.Id.GetValue() ?? 0L;
        });
        if (wallId == 0L)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        // An instance exposes its own (empty) view of the Assembly Code; the
        // explicit scope=type has to reach the parameter of the type.
        var refused = Set("[" + CodeItem(wallId, "20.10.99") + "]");
        await Assert.That(refused.Success).IsFalse();
        await Assert.That(Failed(refused).GetProperty("errorCode").GetString()).IsEqualTo(CodeNotFound);

        var written = Set("[" + CodeItem(wallId, "20.10.10") + "]");
        await Assert.That(written.Success).IsTrue();
        await Assert.That(Changed(written).GetProperty("name").GetString()).Contains("type:");
        await Assert.That(CodeOf(WallTypeId)).IsEqualTo("20.10.10");
        await Assert.That(DescriptionOf(WallTypeId)).IsEqualTo("Pipes");
    }

    [Test]
    public async Task Set_PaddedCode_IsCheckedByItsTrimmedForm()
    {
        RequireWallType();
        await Assert.That(Load(SampleFile("guard-padded.txt"), dryRun: false).Success).IsTrue();

        var result = Set("[" + CodeItem(WallTypeId, " 20.10.10 ") + "]");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(CodeOf(WallTypeId)).IsEqualTo("20.10.10");
        await Assert.That(DescriptionOf(WallTypeId)).IsEqualTo("Pipes");
    }

    [Test]
    public async Task Set_CodeNotInTable_AllowUnverified_WritesWithAWarning()
    {
        RequireWallType();
        await Assert.That(Load(SampleFile("guard-opt-in.txt"), dryRun: false).Success).IsTrue();

        var result = Set("[" + CodeItem(WallTypeId, "20.10.99") + "]", allowUnverified: true);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(CodeOf(WallTypeId)).IsEqualTo("20.10.99");
        await Assert.That(DescriptionOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
        await Assert.That(UnverifiedWarnings(result)).IsEqualTo(1);
        await Assert.That(result.Warnings[0]).Contains("written unverified (" + CodeNotFound + ")");
    }

    [Test]
    public async Task Set_SameUnverifiedCodeOnSeveralTypes_WarnsOnce()
    {
        if (_wallTypeIds.Count < 2)
            Skip.Test("В шаблоне проекта по умолчанию меньше двух типов стены");

        var result = Set(
            "[" + CodeItem(_wallTypeIds[0], "20.10.10") + "," + CodeItem(_wallTypeIds[1], "20.10.10") + "]",
            allowUnverified: true);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("changed").GetArrayLength()).IsEqualTo(2);
        await Assert.That(UnverifiedWarnings(result)).IsEqualTo(1);
    }

    [Test]
    public async Task Set_EmptyValue_IsAlwaysAllowed()
    {
        RequireWallType();
        await Assert.That(Set("[" + CodeItem(WallTypeId, "20.10.10") + "]", allowUnverified: true).Success).IsTrue();

        var cleared = Set("[" + CodeItem(WallTypeId, "") + "]");

        await Assert.That(cleared.Success).IsTrue();
        await Assert.That(UnverifiedWarnings(cleared)).IsEqualTo(0);
        await Assert.That(CodeOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Set_ClearingAnAlreadyEmptyCode_IsAPlainNoOp()
    {
        RequireWallType();

        var result = Set("[" + CodeItem(WallTypeId, "") + "]", allowUnverified: true);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(UnverifiedWarnings(result)).IsEqualTo(0);
        var skipped = result.Data!.Value.GetProperty("skipped")[0];
        await Assert.That(skipped.GetProperty("reason").GetString()).IsEqualTo("The parameter already has this value (no-op).");
    }

    [Test]
    public async Task Set_Atomic_RefusedCodeRollsBackTheWholeBatch()
    {
        RequireWallType();
        var items =
            "[{\"elementId\":" + WallTypeId + ",\"parameter\":\"Description\",\"value\":\"Batch description\",\"scope\":\"type\"}," +
            CodeItem(WallTypeId, "20.10.10") + "]";

        var result = Set(items, atomic: true);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("atomic_aborted");
        await Assert.That(DescriptionParameterOf(WallTypeId)).IsEqualTo(string.Empty);
        await Assert.That(CodeOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Set_NonAtomic_RefusedCodeLeavesTheOtherItemsApplied()
    {
        RequireWallType();
        var items =
            "[{\"elementId\":" + WallTypeId + ",\"parameter\":\"Description\",\"value\":\"Batch description\",\"scope\":\"type\"}," +
            CodeItem(WallTypeId, "20.10.10") + "]";

        var result = Set(items);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(DescriptionParameterOf(WallTypeId)).IsEqualTo("Batch description");
        await Assert.That(result.Data!.Value.GetProperty("failed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(Failed(result).GetProperty("errorCode").GetString()).IsEqualTo(TableNotLoaded);
        await Assert.That(CodeOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task UndoLast_RestoresTheCodeAndRevitRecomputesTheDescription()
    {
        RequireWallType();
        await Assert.That(Load(SampleFile("guard-undo.txt"), dryRun: false).Success).IsTrue();
        await Assert.That(Set("[" + CodeItem(WallTypeId, "20.10.10") + "]").Success).IsTrue();
        await Assert.That(Set("[" + CodeItem(WallTypeId, "10.10.10") + "]").Success).IsTrue();
        await Assert.That(DescriptionOf(WallTypeId)).IsEqualTo("Load-bearing walls");

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);

        var first = undoTool.ExecuteForTests(Args(new { dryRun = false }), approved: true, CancellationToken.None);
        await Assert.That(first.Success).IsTrue();
        await Assert.That(CodeOf(WallTypeId)).IsEqualTo("20.10.10");
        await Assert.That(DescriptionOf(WallTypeId)).IsEqualTo("Pipes");

        var second = undoTool.ExecuteForTests(Args(new { dryRun = false }), approved: true, CancellationToken.None);
        await Assert.That(second.Success).IsTrue();
        await Assert.That(CodeOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
        await Assert.That(DescriptionOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);
    }

    // ── one argument for every Revit version ────────────────────────────

    [Test]
    public async Task Set_BuiltInIdAliases_ResolveTheAssemblyCodeAndDescription()
    {
        RequireWallType();

        // The code: both enum ids reach the built-in, so both meet the check.
        foreach (var parameter in new[] { "UNIFORMAT_CODE", "ASSEMBLY_CODE" })
        {
            var result = Set("[" + CodeItem(WallTypeId, "20.10.10", parameter) + "]");
            await Assert.That(result.Success).IsFalse();
            await Assert.That(Failed(result).GetProperty("errorCode").GetString()).IsEqualTo(TableNotLoaded);
        }

        // The description: both ids reach the built-in parameter, which Revit
        // keeps read-only — a miss would be parameter_not_found instead.
        foreach (var parameter in new[] { "UNIFORMAT_DESCRIPTION", "ASSEMBLY_DESCRIPTION" })
        {
            var result = Set("[" + CodeItem(WallTypeId, "Text", parameter) + "]");
            await Assert.That(result.Success).IsFalse();
            await Assert.That(Failed(result).GetProperty("errorCode").GetString()).IsEqualTo("readonly_parameter");
        }
    }

    [Test]
    public async Task Set_TableIsReadPerDocument()
    {
        RequireWallType();
        await Assert.That(Load(SampleFile("guard-per-document.txt"), dryRun: false).Success).IsTrue();

        var other = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var wallType = new FilteredElementCollector(other).OfClass(typeof(WallType)).FirstElement();
            if (wallType is null)
                Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

            // The transaction service has to be bound to the same document as the tool.
            var otherTransactions = new RevitTransactionService(new StubRevitContext(other));
            var result = new SetParametersTool(new StubRevitContext(other), otherTransactions, _changeSets!)
                .ExecuteForTests(
                    Args(new
                    {
                        items = new[] { new { elementId = wallType.Id.GetValue(), parameter = "ASSEMBLY_CODE", value = "20.10.10", scope = "type" } },
                        dryRun = false
                    }),
                    approved: true,
                    CancellationToken.None);

            await Assert.That(result.Success).IsFalse();
            await Assert.That(Failed(result).GetProperty("errorCode").GetString()).IsEqualTo(TableNotLoaded);
            await Assert.That(
                wallType.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn)?.AsString() ?? string.Empty)
                .IsEqualTo(string.Empty);
        }
        finally
        {
            other.Close(false);
        }
    }
}
