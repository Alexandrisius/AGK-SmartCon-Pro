using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// find_families_with_parameter scope verification (v15): a project-bound
/// shared parameter shows on every element of the bound category, so the
/// assumed "family+project" scope made non-ported families look ported. The
/// default verifyFamilyScope=true opens the family documents and downgrades
/// such matches to an honest "project".
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    [Test]
    public async Task FindFamiliesWithParameter_VerifiedScope_DowngradesProjectOnlyBinding()
    {
        const string parameterName = "KRTRS_VerifyScope";
        var family = (RevitFamily)Doc.GetElement(ElementIdCompat.Create(Seed.FamilyId))!;
        var familyCategory = family.FamilyCategory;
        if (familyCategory is null)
            Skip.Test("У загруженного семейства нет категории");

        using var sharedFile = SharedParameterFileScope.Create(Application);
        var definitionGuid = Guid.Parse("6E2C1D59-8B7A-4C3F-9A51-2D0E7B4C9F13");
        using (var transaction = new Transaction(Doc, "Bind a project parameter"))
        {
            transaction.Start();
            var file = Application.OpenSharedParameterFile();
            var group = file!.Groups.Create("SC_AgentScopeVerify");
            var definition = (ExternalDefinition)group.Definitions.Create(
                new ExternalDefinitionCreationOptions(parameterName, SpecTypeId.String.Text)
                {
                    GUID = definitionGuid
                });
            var categories = Application.Create.NewCategorySet();
            categories.Insert(familyCategory!);
            var binding = Application.Create.NewTypeBinding(categories);
            if (!Doc.ParameterBindings.Insert(definition, binding))
                Skip.Test("Revit не привязал параметр к категории семейства");
            transaction.Commit();
        }

        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        // Without verification the old assumption survives: the GUID binding
        // alone claims family+project.
        var assumed = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"" + parameterName + "\",\"verifyFamilyScope\":false}"),
            CancellationToken.None);
        await Assert.That(assumed.Success).IsTrue();
        var assumedFamily = FindFamily(Data(assumed), Seed.FamilyId);
        await Assert.That(assumedFamily.GetProperty("scopes")[0].GetString())
            .IsEqualTo("family+project");

        // The default mode verifies against the family document: the parameter
        // is absent there, so the honest scope is project.
        var verified = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"" + parameterName + "\",\"compact\":false}"),
            CancellationToken.None);
        await Assert.That(verified.Success).IsTrue();
        var verifiedData = Data(verified);
        var verifiedFamily = FindFamily(verifiedData, Seed.FamilyId);
        await Assert.That(verifiedFamily.GetProperty("scopeVerification").GetString())
            .IsEqualTo("verified");
        await Assert.That(verifiedFamily.GetProperty("scopes")[0].GetString())
            .IsEqualTo("project");
        var matchedRow = verifiedFamily.GetProperty("matchedTypes")[0];
        await Assert.That(matchedRow.GetProperty("parameterScope").GetString())
            .IsEqualTo("project");
        await Assert.That(matchedRow.GetProperty("familyDocument").GetBoolean()).IsFalse();
        var verification = verifiedData.GetProperty("scopeVerification");
        await Assert.That(verification.GetProperty("mode").GetString())
            .IsEqualTo("family-document");
        await Assert.That(verification.GetProperty("verified").GetInt32())
            .IsGreaterThanOrEqualTo(1);

        // The migration filter drops the family once the scope is verified.
        var filtered = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"" + parameterName + "\",\"parameterScope\":\"family+\"}"),
            CancellationToken.None);
        await Assert.That(filtered.Success).IsTrue();
        await Assert.That(ContainsFamily(Data(filtered), Seed.FamilyId)).IsFalse();
    }

    [Test]
    public async Task FindFamiliesWithParameter_CategoryArray_FiltersByAnyListed()
    {
        // v15: category accepted only a string; an array failed the call.
        var family = (RevitFamily)Doc.GetElement(ElementIdCompat.Create(Seed.FamilyId))!;
        var familyCategory = family.FamilyCategory;
        if (familyCategory is null)
            Skip.Test("У загруженного семейства нет категории");

        var ostName = ((BuiltInCategory)familyCategory!.Id.GetValue()).ToString();
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"" + FamilySeed.CodeParam + "\",\"match\":\"exact\"," +
                  "\"category\":[\"" + ostName + "\",\"OST_NoSuchCategory\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(ContainsFamily(Data(result), Seed.FamilyId)).IsTrue();
        var unknown = Data(result).GetProperty("unknownCategories");
        await Assert.That(unknown.GetArrayLength()).IsEqualTo(1);
        await Assert.That(unknown[0].GetString()).IsEqualTo("OST_NoSuchCategory");
    }

    [Test]
    public async Task FindFamiliesWithParameter_MixedScopes_FilterKeepsCountersConsistent()
    {
        // Validator P1 (v15): after the parameterScope filter drops one name of
        // a family, the per-name type counters must drop with it — otherwise
        // matchedTypeCount contradicts matchedParameterCount in the same row.
        // Setup: one name lives IN the family (KRTRS_Mix_InFamily), another is
        // a project-only binding (KRTRS_Mix_Proj) on the same category.
        const string inFamily = "KRTRS_Mix_InFamily";
        const string projectOnly = "KRTRS_Mix_Proj";
        var family = (RevitFamily)Doc.GetElement(ElementIdCompat.Create(Seed.FamilyId))!;
        var familyCategory = family.FamilyCategory;
        if (familyCategory is null)
            Skip.Test("У загруженного семейства нет категории");

        // Embed a family parameter and load the family back (the load recreates
        // the family element, so the row is found by name below, not by id).
        Document? familyDoc = null;
        try
        {
            familyDoc = Doc.EditFamily(family);
            using (var transaction = new Transaction(familyDoc, "Add mix parameter"))
            {
                transaction.Start();
                familyDoc.FamilyManager.AddParameter(
                    inFamily, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                transaction.Commit();
            }

            familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
        }
        finally
        {
            try
            {
                familyDoc?.Close(false);
            }
            catch (Exception)
            {
                // The copy is discarded either way.
            }
        }

        using var sharedFile = SharedParameterFileScope.Create(Application);
        using (var transaction = new Transaction(Doc, "Bind project-only mix parameter"))
        {
            transaction.Start();
            var file = Application.OpenSharedParameterFile();
            var group = file!.Groups.Create("SC_AgentMixScope");
            var definition = (ExternalDefinition)group.Definitions.Create(
                new ExternalDefinitionCreationOptions(projectOnly, SpecTypeId.String.Text)
                {
                    GUID = Guid.Parse("7A1B4E62-9C3D-4F80-8B42-3E5F1C6DA24B")
                });
            var categories = Application.Create.NewCategorySet();
            categories.Insert(familyCategory!);
            if (!Doc.ParameterBindings.Insert(
                    definition, Application.Create.NewTypeBinding(categories)))
            {
                Skip.Test("Revit не привязал проектный параметр к категории семейства");
            }

            transaction.Commit();
        }

        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        // Unfiltered: both names match; the verified scopes cover family and
        // project in one family.
        var any = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"KRTRS_Mix_\",\"match\":\"prefix\",\"compact\":false," +
                  "\"summary\":true}"),
            CancellationToken.None);
        await Assert.That(any.Success).IsTrue();
        var anyRow = FindFamilyByName(Data(any), Seed.FamilyName);
        await Assert.That(anyRow.GetProperty("matchedParameterCount").GetInt32()).IsEqualTo(2);
        await Assert.That(anyRow.GetProperty("scopes").GetArrayLength()).IsEqualTo(2);

        // The migration filter keeps the family (InFamily survived) and every
        // counter must describe ONLY the surviving name.
        var filtered = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"KRTRS_Mix_\",\"match\":\"prefix\"," +
                  "\"parameterScope\":\"family+\",\"compact\":false,\"summary\":true}"),
            CancellationToken.None);
        await Assert.That(filtered.Success).IsTrue();
        var row = FindFamilyByName(Data(filtered), Seed.FamilyName);
        await Assert.That(row.GetProperty("matchedParameterCount").GetInt32()).IsEqualTo(1);
        await Assert.That(row.GetProperty("scopes").GetArrayLength()).IsEqualTo(1);
        await Assert.That(row.GetProperty("scopes")[0].GetString()).IsEqualTo("family");
        var typeCount = row.GetProperty("matchedTypeCount").GetInt32();
        var returnedRows = row.GetProperty("matchedTypes").GetArrayLength();
        // Two seeded types carry the surviving family parameter; a stale
        // TypeMatchesByName entry for the dropped name would inflate this.
        await Assert.That(typeCount).IsEqualTo(2);
        await Assert.That(returnedRows).IsEqualTo(typeCount);
        var byName = Data(filtered).GetProperty("byParameterName");
        var names = new List<string>();
        foreach (var entry in byName.EnumerateArray())
            names.Add(entry.GetProperty("name").GetString()!);
        await Assert.That(string.Join(",", names)).IsEqualTo(inFamily);
    }

    private static JsonElement FindFamilyByName(JsonElement data, string familyName)
    {
        foreach (var row in data.GetProperty("families").EnumerateArray())
        {
            if (string.Equals(row.GetProperty("familyName").GetString(), familyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        throw new InvalidOperationException(
            "Family '" + familyName + "' is missing in the result.");
    }
}
