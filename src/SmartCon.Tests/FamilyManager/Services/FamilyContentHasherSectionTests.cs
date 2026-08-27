using System.Security.Cryptography;
using System.Text;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Issue #249, Phase 1/2: the sectional decomposition of the content
/// hasher must reproduce the pre-refactor canonical string byte-for-byte
/// (no FHV bump, no migration), section hashes must be the SHA-256 of
/// their own canonical substring, and per-type hashes must isolate the
/// changed type.
/// </summary>
public sealed class FamilyContentHasherSectionTests
{
    private readonly FamilyContentHasher _hasher = new();

    private static string Sha256(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
#if NET8_0_OR_GREATER
        return Convert.ToHexString(SHA256.HashData(bytes));
#else
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty);
#endif
    }

    private static FamilySnapshot CreateRichLoadableSnapshot(bool withLookup = true)
    {
        var parameters = new[]
        {
            new FamilyParameterInfo(
                Name: "Диаметр|условный",
                StorageType: "Double",
                ParameterGroup: "PG",
                IsInstance: false,
                IsShared: true,
                Formula: null,
                IsDeterminedByFormula: false,
                IsReporting: false,
                SharedParamGuid: "guid-1",
                BuiltInParameterId: null),
            new FamilyParameterInfo(
                Name: "Модель",
                StorageType: "String",
                ParameterGroup: "PG",
                IsInstance: true,
                IsShared: false,
                Formula: "if(x, 1%, 2)",
                IsDeterminedByFormula: true,
                IsReporting: true,
                SharedParamGuid: null,
                BuiltInParameterId: "BIP-1"),
        };

        var types = new[]
        {
            new FamilyTypeSnapshot(
                Name: "Ду50",
                Values: new[]
                {
                    new FamilyParameterValue("Диаметр|условный", "Double", true, null, 0.164, null),
                    new FamilyParameterValue("Модель", "String", true, "A|B%C", null, null),
                }),
            new FamilyTypeSnapshot(
                Name: "Ду80",
                Values: new[]
                {
                    new FamilyParameterValue("Диаметр|условный", "Double", true, null, 0.262, null),
                }),
        };

        var forms = new[]
        {
            new FormMetrics("Extrusion", true, 0.5, 6, 9, null, 2.5,
                new BoundingBoxSnapshot(0, 0, 0, 1, 2, 3)),
            new FormMetrics("Sweep", false, 0.1, 4, 6, "Sub|cat", 1.1, null),
        };

        return new FamilySnapshot(
            FamilyName: "Отвод",
            Category: "Pipe Fittings",
            Parameters: parameters,
            Types: types,
            Geometry: new GeometryMetrics(
                TotalFormCount: 2,
                Forms: forms,
                SymbolicCurveCount: 1,
                DetailCurveCount: 2,
                ModelCurveCount: 3,
                TextNoteCount: 1,
                ReferencePlaneCount: 4,
                DimensionCount: 5,
                TotalSymbolicCurveLength: 1.5,
                TotalDetailCurveLength: 2.5,
                TotalModelCurveLength: 3.5),
            SharedNestedFamilyNames: new[] { "Вложенное|семейство" },
            CategoryId: -12345,
            Facts: new[] { new FamilyFact("PartType", "Elbow", "Отвод") },
            Connectors: new[]
            {
                new ConnectorSnapshot(1, 2, 3, true, 0.1, null, null, 0.1, 0.2, 0.3, 0),
                new ConnectorSnapshot(1, 2, 3, false, null, 0.2, null, -0.1, 0, 0, 1),
            },
            BehaviorFlags: new FamilyBehaviorFlags(true, false, null, true),
            NonSharedNestedFamilyNames: new[] { "NonShared%1" },
            SharedNestedContentHashes: new[] { new NestedContentHash("Вложенное|семейство", "ABC123") },
            PhantomTypeValues: new[]
            {
                new FamilyParameterValue("Модель", "String", true, "phantom", null, null),
            },
            LookupTables: withLookup
                ? new[] { new LookupTableSnapshot("L1", "a,b\n1,2") }
                : null);
    }

    private static SystemFamilySnapshot CreateRichSystemSnapshot()
    {
        var types = new[]
        {
            new SystemTypeSnapshot(
                Name: "Стандартный",
                Values: new[]
                {
                    new SystemParameterValue("Толщина", "Double", true, null, 0.3, null),
                    new SystemParameterValue("Комментарий|%", "String", true, "текст", null, null),
                },
                Structure: new CompoundStructureSnapshot(
                    0, 0,
                    new[]
                    {
                        new CompoundLayerSnapshot(1, 0.3, "Бетон|М300", false, true, false),
                    }),
                Routing: new RoutingPreferencesSnapshot(
                    1,
                    new[]
                    {
                        new RoutingRuleSnapshot(
                            2, "Отвод:Ду50", "правило|1",
                            new[] { new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.1, 0.5) }),
                    }),
                FamilyName: "Basic Wall",
                FamilyKey: "Wall",
                Stairs: new StairsSubtypesSnapshot("Марш", "Площадка", null, "Опора|П", null, null),
                Railing: new RailingStructureSnapshot(
                    "Поручень|В", 0.9, null, null, null, null, null, null, null, null,
                    new[] { new RailingRailSnapshot("Рейка", 0.5, 0.05, "Профиль", "Сталь%") },
                    new RailingBalusterSnapshot(1.2, 1, 0, new string?[] { "Стойка" }, true, 2, "Семейство%1")),
                Segments: new[]
                {
                    new SegmentSnapshot(
                        "Сталь|угл", "Сталь", "Сортамент", 0.0001,
                        new[] { new SegmentSizeSnapshot(0.05, 0.045, 0.055, true, false) }),
                },
                Wire: new WireSettingsSnapshot("Медь", "75C", "ПВХ|изол", "16мм", "Кабель|К", 1.0, true)),
            new SystemTypeSnapshot(
                Name: "Тонкая",
                Values: new[]
                {
                    new SystemParameterValue("Толщина", "Double", true, null, 0.1, null),
                },
                FamilyName: "Basic Wall",
                FamilyKey: "Wall"),
        };
        return new SystemFamilySnapshot("Стены", -2000011, types);
    }

    // ---------------------------------------------------------------
    // Loadable: concat equivalence
    // ---------------------------------------------------------------

    [Fact]
    public void Loadable_Sections_ConcatEqualsCanonicalString()
    {
        var snapshot = CreateRichLoadableSnapshot();
        var sections = FamilyContentHasher.BuildLoadableSections(snapshot);

        var concat = string.Concat(sections.Select(s => s.CanonicalString));

        Assert.Equal(FamilyContentHasher.BuildLoadableCanonicalString(snapshot), concat);
    }

    [Fact]
    public void Loadable_Sections_ConcatHashEqualsIdentityHash()
    {
        var snapshot = CreateRichLoadableSnapshot();
        var sections = _hasher.ComputeSectionsForLoadable(snapshot);
        var identity = _hasher.ComputeForLoadable(snapshot);

        Assert.NotNull(sections);
        Assert.NotNull(identity);
        Assert.Equal(identity!.HexString, Sha256(string.Concat(sections!.Select(s => s.CanonicalString))));
    }

    [Fact]
    public void Loadable_Sections_CanonicalOrderAndNames()
    {
        var snapshot = CreateRichLoadableSnapshot();
        var sections = _hasher.ComputeSectionsForLoadable(snapshot);

        Assert.NotNull(sections);
        Assert.Equal(
            new[]
            {
                "META", "PARAMS", "TYPES", "PHANTOM", "DEF", "GEOM", "GEOM2D",
                "NESTED", "NONSHARED", "NESTEDHASH", "FACTS", "FLAGS", "CONN", "LOOKUP",
            },
            sections!.Select(s => s.SectionName).ToArray());
        Assert.All(sections, s => Assert.Null(s.TypeName));
    }

    [Fact]
    public void Loadable_Sections_LookupOmittedForTablelessFamily()
    {
        var snapshot = CreateRichLoadableSnapshot(withLookup: false);
        var sections = _hasher.ComputeSectionsForLoadable(snapshot);

        Assert.NotNull(sections);
        Assert.DoesNotContain(sections!, s => s.SectionName == "LOOKUP");
        Assert.Equal(13, sections!.Count);

        // The byte-identity criterion holds for table-less families too.
        var concat = string.Concat(sections.Select(s => s.CanonicalString));
        Assert.Equal(FamilyContentHasher.BuildLoadableCanonicalString(snapshot), concat);
    }

    [Fact]
    public void Loadable_Sections_EachHashIsSha256OfOwnString()
    {
        var snapshot = CreateRichLoadableSnapshot();
        var sections = _hasher.ComputeSectionsForLoadable(snapshot);

        Assert.NotNull(sections);
        Assert.All(sections!, s => Assert.Equal(Sha256(s.CanonicalString), s.HashHex));
    }

    [Fact]
    public void Loadable_Sections_NullSnapshot_ReturnsNull()
    {
        Assert.Null(_hasher.ComputeSectionsForLoadable(null!));
    }

    // ---------------------------------------------------------------
    // System: concat equivalence
    // ---------------------------------------------------------------

    [Fact]
    public void System_Sections_ConcatEqualsCanonicalString()
    {
        var snapshot = CreateRichSystemSnapshot();
        var sections = FamilyContentHasher.BuildSystemSections(snapshot);

        var concat = string.Concat(sections.Select(s => s.CanonicalString));

        Assert.Equal(FamilyContentHasher.BuildSystemCanonicalString(snapshot), concat);
    }

    [Fact]
    public void System_Sections_ConcatHashEqualsIdentityHash()
    {
        var snapshot = CreateRichSystemSnapshot();
        var sections = _hasher.ComputeSectionsForSystem(snapshot);
        var identity = _hasher.ComputeForSystem(snapshot);

        Assert.NotNull(sections);
        Assert.NotNull(identity);
        Assert.Equal(identity!.HexString, Sha256(string.Concat(sections!.Select(s => s.CanonicalString))));
    }

    [Fact]
    public void System_Sections_MetaThenPerTypeEntries()
    {
        var snapshot = CreateRichSystemSnapshot();
        var sections = _hasher.ComputeSectionsForSystem(snapshot);

        Assert.NotNull(sections);
        // 1 META + 2 types × 8 per-type entries.
        Assert.Equal(1 + 2 * 8, sections!.Count);
        Assert.Equal("META", sections[0].SectionName);
        Assert.Null(sections[0].TypeName);

        var perType = sections.Skip(1).ToArray();
        Assert.All(perType, s => Assert.NotNull(s.TypeName));
        Assert.All(perType, s => Assert.Equal(Sha256(s.CanonicalString), s.HashHex));

        var expectedCycle = new[]
        {
            "VALUES", "FAMKEY", "STRUCT", "ROUTING", "SEGMENTS", "SUBTYPES", "RAILING", "WIRE",
        };
        for (var i = 0; i < perType.Length; i++)
        {
            Assert.Equal(expectedCycle[i % 8], perType[i].SectionName);
        }

        // Types are canonical-sorted: «Стандартный» < «Тонкая» (Ordinal).
        Assert.Equal("Стандартный", perType[0].TypeName);
        Assert.Equal("Тонкая", perType[8].TypeName);
    }

    [Fact]
    public void System_Sections_NullOrEmptySnapshot_ReturnsNull()
    {
        Assert.Null(_hasher.ComputeSectionsForSystem(null!));
        Assert.Null(_hasher.ComputeSectionsForSystem(
            new SystemFamilySnapshot("Стены", -2000011, Array.Empty<SystemTypeSnapshot>())));
    }

    // ---------------------------------------------------------------
    // Per-type hashes (Phase 2 Core)
    // ---------------------------------------------------------------

    [Fact]
    public void PerType_Loadable_ValueChangeShiftsOnlyThatType()
    {
        var snapshot = CreateRichLoadableSnapshot();
        var baseline = _hasher.ComputePerTypeHashesForLoadable(snapshot);
        Assert.NotNull(baseline);
        Assert.Equal(2, baseline!.Count);

        var modified = snapshot with
        {
            Types = new[]
            {
                snapshot.Types[0] with
                {
                    Values = new[]
                    {
                        new FamilyParameterValue("Диаметр|условный", "Double", true, null, 0.999, null),
                    },
                },
                snapshot.Types[1],
            },
        };
        var after = _hasher.ComputePerTypeHashesForLoadable(modified);

        Assert.NotNull(after);
        Assert.NotEqual(baseline["Ду50"], after!["Ду50"]);
        Assert.Equal(baseline["Ду80"], after["Ду80"]);

        // The family identity hash also shifts (TYPES section changed).
        Assert.NotEqual(
            _hasher.ComputeForLoadable(snapshot)!.HexString,
            _hasher.ComputeForLoadable(modified)!.HexString);
    }

    [Fact]
    public void PerType_Loadable_DeterministicAcrossValueOrder()
    {
        var values = new[]
        {
            new FamilyParameterValue("B", "Double", true, null, 2.0, null),
            new FamilyParameterValue("A", "Double", true, null, 1.0, null),
        };
        var t1 = new FamilyTypeSnapshot("T", values);
        var t2 = new FamilyTypeSnapshot("T", values.Reverse().ToArray());

        var h1 = _hasher.ComputePerTypeHashesForLoadable(
            CreateRichLoadableSnapshot() with { Types = new[] { t1 } });
        var h2 = _hasher.ComputePerTypeHashesForLoadable(
            CreateRichLoadableSnapshot() with { Types = new[] { t2 } });

        Assert.NotNull(h1);
        Assert.NotNull(h2);
        Assert.Equal(h1!["T"], h2!["T"]);
    }

    [Fact]
    public void PerType_Loadable_RenameIsRemoveAndAdd()
    {
        var snapshot = CreateRichLoadableSnapshot();
        var renamed = snapshot with
        {
            Types = new[]
            {
                snapshot.Types[0] with { Name = "DN50" },
                snapshot.Types[1],
            },
        };

        var baseline = _hasher.ComputePerTypeHashesForLoadable(snapshot)!;
        var after = _hasher.ComputePerTypeHashesForLoadable(renamed)!;

        Assert.False(after.ContainsKey("Ду50"));
        Assert.True(after.ContainsKey("DN50"));
        // Renamed type keeps its values but the name is part of the hash.
        Assert.NotEqual(baseline["Ду50"], after["DN50"]);
    }

    [Fact]
    public void PerType_System_BodyChangeShiftsOnlyThatType()
    {
        var snapshot = CreateRichSystemSnapshot();
        var baseline = _hasher.ComputePerTypeHashesForSystem(snapshot);
        Assert.NotNull(baseline);
        Assert.Equal(2, baseline!.Count);
        var baselineByName = baseline.ToDictionary(e => e.TypeName, e => e.HashHex);

        var modified = snapshot with
        {
            Types = new[]
            {
                snapshot.Types[0],
                snapshot.Types[1] with
                {
                    Values = new[]
                    {
                        new SystemParameterValue("Толщина", "Double", true, null, 0.2, null),
                    },
                },
            },
        };
        var after = _hasher.ComputePerTypeHashesForSystem(modified);

        Assert.NotNull(after);
        var afterByName = after!.ToDictionary(e => e.TypeName, e => e.HashHex);
        Assert.Equal(baselineByName["Стандартный"], afterByName["Стандартный"]);
        Assert.NotEqual(baselineByName["Тонкая"], afterByName["Тонкая"]);
    }

    [Fact]
    public void PerType_System_StructChangeShiftsHash()
    {
        var snapshot = CreateRichSystemSnapshot();
        var baseline = _hasher.ComputePerTypeHashesForSystem(snapshot)!;
        var baselineByName = baseline.ToDictionary(e => e.TypeName, e => e.HashHex);

        var modified = snapshot with
        {
            Types = new[]
            {
                snapshot.Types[0] with
                {
                    Structure = snapshot.Types[0].Structure! with
                    {
                        Layers = new[]
                        {
                            snapshot.Types[0].Structure!.Layers[0] with { Width = 0.4 },
                        },
                    },
                },
                snapshot.Types[1],
            },
        };
        var after = _hasher.ComputePerTypeHashesForSystem(modified)!;
        var afterByName = after.ToDictionary(e => e.TypeName, e => e.HashHex);

        Assert.NotEqual(baselineByName["Стандартный"], afterByName["Стандартный"]);
        Assert.Equal(baselineByName["Тонкая"], afterByName["Тонкая"]);
    }

    [Fact]
    public void PerType_System_SameNamedTypesOfDifferentFamilies_BothReturned()
    {
        // FHV6 scenario: one category snapshot contains same-named types of
        // different system families («оба воздуховода — "Короб"»). A
        // name-keyed map would silently drop one — the list must not.
        var snapshot = new SystemFamilySnapshot(
            "Воздуховоды",
            -2008000,
            new[]
            {
                new SystemTypeSnapshot(
                    "Короб",
                    new[] { new SystemParameterValue("Ширина", "Double", true, null, 0.3, null) },
                    FamilyName: "Rectangular Duct",
                    FamilyKey: "Duct.Rectangular"),
                new SystemTypeSnapshot(
                    "Короб",
                    new[] { new SystemParameterValue("Диаметр", "Double", true, null, 0.3, null) },
                    FamilyName: "Round Duct",
                    FamilyKey: "Duct.Round"),
            });

        var hashes = _hasher.ComputePerTypeHashesForSystem(snapshot);

        Assert.NotNull(hashes);
        Assert.Equal(2, hashes!.Count);
        Assert.NotEqual(hashes[0].HashHex, hashes[1].HashHex);
        Assert.NotEqual(hashes[0].IdentityKey, hashes[1].IdentityKey);
        Assert.Equal("DUCT.RECTANGULAR|КОРОБ", hashes[0].IdentityKey);
        Assert.Equal("DUCT.ROUND|КОРОБ", hashes[1].IdentityKey);
    }

    [Fact]
    public void PerType_NullSnapshot_ReturnsNull()
    {
        Assert.Null(_hasher.ComputePerTypeHashesForLoadable(null!));
        Assert.Null(_hasher.ComputePerTypeHashesForSystem(null!));
        Assert.Null(_hasher.ComputePerTypeHashesForSystem(
            new SystemFamilySnapshot("Стены", -2000011, Array.Empty<SystemTypeSnapshot>())));
    }
}
