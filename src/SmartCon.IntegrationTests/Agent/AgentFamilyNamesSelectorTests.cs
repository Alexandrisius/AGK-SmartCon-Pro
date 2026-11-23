using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// familyNames[] of list_family_types (night run 2, N-ds-5): selecting
/// several families by name in one call, the both-selectors refusal, and the
/// typed failures for names that do not resolve.
/// </summary>
public sealed class AgentFamilyNamesSelectorTests : RevitApiTest
{
    private const string FamilyA = "SC_AgentFamilyNames_A";
    private const string FamilyB = "SC_AgentFamilyNames_B";

    private Document? _document;
    private FamilySeed? _seedA;
    private FamilySeed? _seedB;
    private RevitThreadEvent? _event;

    private Document Doc => _document!;

    private RevitThreadEvent Event => _event!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _event = new RevitThreadEvent(Application);
    }

    [After(Test)]
    public void Close()
    {
        _seedA?.Dispose();
        _seedB?.Dispose();
        try
        {
            _document?.Close(false);
        }
        catch (Exception)
        {
            // The document may already be closed by a test scenario.
        }
    }

    [Test]
    public async Task TwoNames_ReturnTypesOfBothFamiliesInOneCall()
    {
        EnsureFamilies();

        var result = await Run(new { familyNames = new[] { FamilyA, FamilyB } });

        await Assert.That(result.Success).IsTrue();
        var payload = result.Data!.Value;
        await Assert.That(payload.GetProperty("familyCount").GetInt32()).IsEqualTo(2);
        var familyNames = payload.GetProperty("types").EnumerateArray()
            .Select(row => row.GetProperty("familyName").GetString() ?? string.Empty)
            .Distinct()
            .ToList();
        await Assert.That(familyNames.Contains(FamilyA)).IsTrue();
        await Assert.That(familyNames.Contains(FamilyB)).IsTrue();
        await Assert.That(payload.GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(4);
    }

    [Test]
    public async Task FamilyNamesTogetherWithFamilyIds_IsInvalidArguments()
    {
        EnsureFamilies();

        var result = await Run(new
        {
            familyNames = new[] { FamilyA },
            familyIds = new[] { 1234 }
        });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
        await Assert.That(result.Message ?? string.Empty).Contains("not both");
    }

    [Test]
    public async Task NoneResolved_TypedFamilyNotFoundWithActionHint()
    {
        EnsureFamilies();

        var result = await Run(new { familyNames = new[] { "Нет такого семейства" } });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("family_not_found");
        await Assert.That(result.Message ?? string.Empty).Contains("[Action:");
        await Assert.That(result.Message ?? string.Empty).Contains("list_families");
    }

    [Test]
    public async Task OneUnresolved_NamesTheMissInsteadOfFailing()
    {
        EnsureFamilies();

        var result = await Run(new { familyNames = new[] { FamilyA, "Нет такого семейства" } });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Message ?? string.Empty)
            .Contains("familyNames batch requires every name to resolve");
        var unresolved = result.Data!.Value.GetProperty("unresolved").EnumerateArray()
            .Select(row => row.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
        await Assert.That(unresolved).IsEqualTo(["Нет такого семейства"]);
    }

    // ── Support ────────────────────────────────────────────────────────

    private void EnsureFamilies()
    {
        _seedA ??= FamilySeed.Create(Application, Doc, FamilyA, out var reasonA)
                   ?? (FamilySeed?)SkipFail(reasonA);
        _seedB ??= FamilySeed.Create(Application, Doc, FamilyB, out var reasonB)
                   ?? (FamilySeed?)SkipFail(reasonB);
    }

    private static object SkipFail(string? reason)
    {
        Skip.Test(reason ?? "Не удалось сидировать семейства");
        return null!;
    }

    private async Task<ToolResult> Run(object args)
    {
        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));
        var context = new AgentToolContext(
            Event,
            AgentDocumentIdentity.GetDocumentId(Doc),
            Guid.Empty);
        return await tool.ExecuteAsync(context, Parse(args), CancellationToken.None);
    }

    private static JsonElement Parse(object args)
    {
        var json = JsonSerializer.Serialize(args);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
