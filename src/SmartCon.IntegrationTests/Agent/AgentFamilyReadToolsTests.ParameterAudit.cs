using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Parameter-audit reads of the family pack (S.33/S.35): prefix matching,
/// compact rows, the honest match total and matchedCount. Also the S.53
/// instance-scope split (typeScopedParameters vs missingParameters) and the
/// nested-family markers.
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    [Test]
    public async Task ListFamilyTypes_InstanceScope_SeparatesTypeScopedFromMissing()
    {
        // Place one instance so valueScope=instance has a reading source.
        var family = new FilteredElementCollector(Doc)
            .OfClass(typeof(RevitFamily))
            .Cast<RevitFamily>()
            .First(item => string.Equals(
                item.Name, Seed.FamilyName, StringComparison.OrdinalIgnoreCase));
        var symbol = (FamilySymbol)Doc.GetElement(family.GetFamilySymbolIds().First());
        using (var transaction = new Transaction(Doc, "Place instance"))
        {
            transaction.Start();
            if (!symbol.IsActive)
                symbol.Activate();
            Doc.Create.NewFamilyInstance(XYZ.Zero, symbol, StructuralType.NonStructural);
            transaction.Commit();
        }

        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));

        // Примечание_экз is an INSTANCE parameter (readable), Ширина is a TYPE
        // parameter (exists — not missing), НетТакого is really absent. Before
        // S.53 all three read as "missing".
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId +
                  ",\"parameterNames\":[\"Примечание_экз\",\"Ширина\",\"НетТакого\"]," +
                  "\"valueScope\":\"instance\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        var typeScoped = data.GetProperty("typeScopedParameters").EnumerateArray()
            .Select(name => name.GetString()!).ToList();
        var missing = data.GetProperty("missingParameters").EnumerateArray()
            .Select(name => name.GetString()!).ToList();
        await Assert.That(typeScoped).Contains(FamilySeed.WidthParam);
        await Assert.That(typeScoped.Contains(FamilySeed.InstanceParam)).IsFalse();
        await Assert.That(missing).Contains("НетТакого");
        await Assert.That(missing.Contains(FamilySeed.WidthParam)).IsFalse();
        await Assert.That(result.Message!).Contains("Type-scoped");
    }

    [Test]
    public async Task GetFamilyParameters_ReportsNestedMarkerShape()
    {
        // S.53: the payload carries the family's own shared flag and its nested
        // subfamilies with theirs (FAMILY_SHARED — no Family.IsShared in the
        // API). The seeded family has no nesting: the markers must say so
        // instead of being absent, so an audit can tell "none" from "unknown".
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + "}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("isShared").GetBoolean()).IsFalse();
        var nestedCount = data.GetProperty("nestedCount").GetInt32();
        var sharedNestedCount = data.GetProperty("sharedNestedCount").GetInt32();
        var nested = data.GetProperty("nestedFamilies");
        // Invariants of the marker block: counts equal the reported rows (the
        // template may bring nested annotation families, so only the contract
        // is fixed here) and every row carries its own shared flag.
        await Assert.That(nestedCount).IsEqualTo(nested.GetArrayLength());
        await Assert.That(sharedNestedCount <= nestedCount).IsTrue();
        foreach (var row in nested.EnumerateArray())
        {
            await Assert.That(row.TryGetProperty("name", out _)).IsTrue();
            await Assert.That(row.TryGetProperty("isShared", out var shared)).IsTrue();
            await Assert.That(shared.ValueKind == JsonValueKind.True
                              || shared.ValueKind == JsonValueKind.False).IsTrue();
        }

        // The compact batch row carries the same marker numbers.
        var batch = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyIds\":[" + Seed.FamilyId + "]}"),
            CancellationToken.None);
        await Assert.That(batch.Success).IsTrue();
        var rows = Data(batch).GetProperty("families");
        await Assert.That(rows.GetArrayLength()).IsEqualTo(1);
        await Assert.That(rows[0].GetProperty("nestedCount").GetInt32()).IsEqualTo(nestedCount);
        await Assert.That(rows[0].GetProperty("sharedNestedCount").GetInt32())
            .IsEqualTo(sharedNestedCount);
        await Assert.That(rows[0].GetProperty("isShared").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task GetFamilyParameters_ReportsRealNestedFamilyWithSharedFlag()
    {
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            Skip.Test("В установленном Revit нет подходящего .rft шаблона семейства");
            return;
        }

        // A host family with the seeded .rfa nested inside it (Tammik's
        // "Nested Family" recipe). NOTE: in a FAMILY document LoadFamily is
        // called OUTSIDE a transaction — the project-context rule ("needs a
        // transaction, else a silent false") does not apply here, and a wrapped
        // call returns false exactly the same way.
        Document? hostDoc = null;
        RevitFamily? host = null;
        try
        {
            hostDoc = Application.NewFamilyDocument(template);
            if (hostDoc is null)
            {
                Skip.Test("Revit не создал хост-семейство из шаблона " + template);
                return;
            }

            var nestedLoaded = hostDoc.LoadFamily(
                Seed.RfaPath, new OverwriteFamilyLoadOptions(), out _);
            if (!nestedLoaded)
            {
                // The string-overload into a family document returns a silent
                // false here; the Document-target push (the path FamilySeed
                // uses for project loads) works.
                Document? childDoc = null;
                try
                {
                    childDoc = Application.OpenDocumentFile(Seed.RfaPath);
                    if (childDoc is not null)
                    {
                        var pushed = childDoc.LoadFamily(hostDoc, new OverwriteFamilyLoadOptions());
                        await Assert.That(pushed).IsNotNull();
                        nestedLoaded = true;
                    }
                }
                finally
                {
                    childDoc?.Close(false);
                }
            }

            await Assert.That(nestedLoaded).IsTrue();

            // Canonical nested-family recipe (Tammik): place an instance of the
            // nested symbol so the host actually uses it. The host itself is
            // marked SHARED (FAMILY_SHARED on the owner family) so the positive
            // branch of the marker is exercised too.
            using (var transaction = new Transaction(hostDoc, "Place nested"))
            {
                transaction.Start();
                var nestedFamily = new FilteredElementCollector(hostDoc)
                    .OfClass(typeof(RevitFamily))
                    .Cast<RevitFamily>()
                    .First(family => string.Equals(
                        family.Name, Seed.FamilyName, StringComparison.OrdinalIgnoreCase));
                var symbol = (FamilySymbol)hostDoc.GetElement(
                    nestedFamily.GetFamilySymbolIds().First());
                if (!symbol.IsActive)
                    symbol.Activate();
                hostDoc.FamilyCreate.NewFamilyInstance(
                    XYZ.Zero, symbol, StructuralType.NonStructural);
                hostDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
                transaction.Commit();
            }

            var hostShared = hostDoc.OwnerFamily
                .get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1;
            var hostPath = Path.Combine(
                Path.GetTempPath(), "SmartConFamilyTests", "SC_NestedHost.rfa");
            hostDoc.SaveAs(hostPath, new SaveAsOptions { OverwriteExistingFile = true });
            hostDoc.Close(false);
            hostDoc = null;

            // Load through an opened document: this overload THROWS on a
            // conflict instead of the string-overload's silent false.
            Document? hostFileDoc = null;
            try
            {
                hostFileDoc = Application.OpenDocumentFile(hostPath);
                host = hostFileDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
            }
            finally
            {
                hostFileDoc?.Close(false);
            }

            await Assert.That(host).IsNotNull();

            var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));
            var result = tool.ExecuteForTests(
                Doc,
                Parse("{\"familyId\":" + host!.Id.GetValue() + "}"),
                CancellationToken.None);

            await Assert.That(result.Success).IsTrue();
            var data = Data(result);
            await Assert.That(data.GetProperty("isShared").GetBoolean()).IsEqualTo(hostShared);
            var sharedNestedCount = data.GetProperty("sharedNestedCount").GetInt32();
            var nested = data.GetProperty("nestedFamilies");
            var found = false;
            foreach (var row in nested.EnumerateArray())
            {
                if (!string.Equals(
                        row.GetProperty("name").GetString(), Seed.FamilyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;
                await Assert.That(row.GetProperty("isShared").GetBoolean()).IsFalse();
            }

            await Assert.That(found).IsTrue();
        }
        finally
        {
            hostDoc?.Close(false);
        }
    }

    [Test]
    public async Task ListFamilies_IsSharedFlag_ReportsSeededFamily()
    {
        var tool = new ListFamiliesTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"nameContains\":\"SC_AgentFamilyRead\"," +
                  "\"fields\":[\"familyId\",\"name\",\"isShared\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var families = Data(result).GetProperty("families");
        await Assert.That(families.GetArrayLength()).IsEqualTo(1);
        await Assert.That(families[0].GetProperty("isShared").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task FindFamiliesWithParameter_CompactRow_CarriesNameCountAndSample()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"Ширина\",\"match\":\"exact\",\"compact\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var family = FindFamily(Data(result), Seed.FamilyId);
        await Assert.That(family.GetProperty("familyName").GetString())
            .IsEqualTo(Seed.FamilyName);
        await Assert.That(family.GetProperty("matchedParameterCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        var sample = family.GetProperty("sample");
        await Assert.That(sample.GetArrayLength()).IsGreaterThanOrEqualTo(1);
        await Assert.That(sample[0].GetString()).IsEqualTo("Ширина");
    }

    [Test]
    public async Task FindFamiliesWithParameter_PrefixMatch_FindsLocalizedPrefix()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterNamePrefix\":\"Шир\",\"compact\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("match").GetString()).IsEqualTo("prefix");
        await Assert.That(ContainsFamily(data, Seed.FamilyId)).IsTrue();
    }

    [Test]
    public async Task FindFamiliesWithParameter_FieldsProjection_ReturnsOnlyRequestedKeys()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"Ширина\",\"compact\":true," +
                  "\"fields\":[\"familyId\",\"familyName\",\"matchedParameterCount\",\"sample\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var family = FindFamily(Data(result), Seed.FamilyId);
        var keys = family.EnumerateObject().Select(property => property.Name).ToList();
        await Assert.That(keys.Count).IsEqualTo(4);
        await Assert.That(keys.Contains("familyId")).IsTrue();
        await Assert.That(keys.Contains("familyName")).IsTrue();
        await Assert.That(keys.Contains("matchedParameterCount")).IsTrue();
        await Assert.That(keys.Contains("sample")).IsTrue();
        await Assert.That(keys.Contains("matchedTypes")).IsFalse();
    }

    [Test]
    public async Task FindFamiliesWithParameter_InstancePass_DoesNotListUnmatchedFamilies()
    {
        var family = new FilteredElementCollector(Doc)
            .OfClass(typeof(RevitFamily))
            .Cast<RevitFamily>()
            .First(item => string.Equals(
                item.Name, Seed.FamilyName, StringComparison.OrdinalIgnoreCase));
        var symbol = (FamilySymbol)Doc.GetElement(family.GetFamilySymbolIds().First());

        using (var transaction = new Transaction(Doc, "Place instance"))
        {
            transaction.Start();
            if (!symbol.IsActive)
                symbol.Activate();

            Doc.Create.NewFamilyInstance(XYZ.Zero, symbol, StructuralType.NonStructural);
            transaction.Commit();
        }

        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        // The instance pass used to add every family with a placed instance to
        // families[], so a search that matched nothing reported total>0.
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"НетТакогоПараметра\",\"includeInstanceParameters\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("families").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task GetFamilyParameters_NameFilter_ReportsMatchedCount()
    {
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));

        var matched = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId +
                  ",\"nameContains\":\"Шир\",\"includeValues\":false}"),
            CancellationToken.None);

        await Assert.That(matched.Success).IsTrue();
        var matchedData = Data(matched);
        await Assert.That(matchedData.GetProperty("matchedCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(matchedData.GetProperty("total").GetInt32())
            .IsEqualTo(matchedData.GetProperty("matchedCount").GetInt32());

        var empty = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId +
                  ",\"nameContains\":\"НетТакогоПараметра\",\"includeValues\":false}"),
            CancellationToken.None);

        await Assert.That(empty.Success).IsTrue();
        var emptyData = Data(empty);
        await Assert.That(emptyData.GetProperty("matchedCount").GetInt32()).IsEqualTo(0);
        await Assert.That(emptyData.GetProperty("parameters").GetArrayLength()).IsEqualTo(0);
    }
}
