using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalFamilyDependencyRepositoryTests
{
    private static async Task<TempCatalogFixture> CreateAndMigrate()
    {
        var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        return fixture;
    }

    [Fact]
    public async Task ReplaceForVersion_ThenGetForCurrentVersion_ReturnsOrderedLinks()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "parent1");
        var childA = await SeedItemAsync(fixture, "childA", "Отвод A");
        var childB = await SeedItemAsync(fixture, "childB", "Тройник B");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childB, FamilyDependencyKind.Routing, "Тройник B:Стандарт", 0),
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });

        var links = await sut.GetForCurrentVersionAsync(parentId);

        Assert.Equal(2, links.Count);
        Assert.Equal(childB, links[0].ChildCatalogItemId);
        Assert.Equal(FamilyDependencyKind.Routing, links[0].Kind);
        Assert.Equal("Тройник B:Стандарт", links[0].PartName);
        Assert.Equal(0, links[0].Ordinal);
        Assert.Equal(childA, links[1].ChildCatalogItemId);
        Assert.Equal(1, links[1].Ordinal);
    }

    [Fact]
    public async Task ReplaceForVersion_CalledTwice_SecondReplacesFirst()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "parent2");
        var childA = await SeedItemAsync(fixture, "childA2", "Отвод A");
        var childB = await SeedItemAsync(fixture, "childB2", "Тройник B");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childB, FamilyDependencyKind.Routing, "Тройник B:Стандарт", 0),
        });

        var links = await sut.GetForCurrentVersionAsync(parentId);

        var link = Assert.Single(links);
        Assert.Equal(childB, link.ChildCatalogItemId);
    }

    [Fact]
    public async Task ReplaceForVersion_DedupsSameChildAndKind()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "parent3");
        var childA = await SeedItemAsync(fixture, "childA3", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Малый", 0),
        });

        var links = await sut.GetForCurrentVersionAsync(parentId);

        var link = Assert.Single(links);
        Assert.Equal("Отвод A:Стандарт", link.PartName);
    }

    [Fact]
    public async Task GetForCurrentVersion_IgnoresLinksOfNonCurrentVersion()
    {
        using var fixture = await CreateAndMigrate();
        var parentId = "parent4";
        // Item's CURRENT label is v2; the seeded version row is v1 → links
        // recorded on v1 must not surface through the current-version join.
        await SeedItemAsync(fixture, parentId, "Родитель", currentVersionLabel: "v2");
        var oldVersionId = await SeedVersionRowAsync(fixture, parentId, "v1");
        var childA = await SeedItemAsync(fixture, "childA4", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, oldVersionId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });

        var links = await sut.GetForCurrentVersionAsync(parentId);

        Assert.Empty(links);
    }

    [Fact]
    public async Task GetForCurrentVersion_NoLinks_ReturnsEmpty()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, _) = await SeedParentWithCurrentVersionAsync(fixture, "parent5");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        var links = await sut.GetForCurrentVersionAsync(parentId);

        Assert.Empty(links);
    }

    [Fact]
    public async Task ReplaceForCurrentVersion_WritesOntoCurrentVersion()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, _) = await SeedParentWithCurrentVersionAsync(fixture, "parent7");
        var childA = await SeedItemAsync(fixture, "childA7", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        var written = await sut.ReplaceForCurrentVersionAsync(parentId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });

        Assert.Equal(1, written);
        var links = await sut.GetForCurrentVersionAsync(parentId);
        var link = Assert.Single(links);
        Assert.Equal(childA, link.ChildCatalogItemId);
    }

    [Fact]
    public async Task ReplaceForCurrentVersion_NoCurrentVersion_ReturnsZero()
    {
        using var fixture = await CreateAndMigrate();
        // Item WITHOUT a current version label — links must not be written.
        await SeedItemAsync(fixture, "parent8", "Родитель");
        var childA = await SeedItemAsync(fixture, "childA8", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        var written = await sut.ReplaceForCurrentVersionAsync("parent8", new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });

        Assert.Equal(0, written);
    }

    [Fact]
    public async Task ReplaceForVersion_EmptyList_ClearsExistingLinks()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "parent6");
        var childA = await SeedItemAsync(fixture, "childA6", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, Array.Empty<FamilyDependencyInfo>());

        var links = await sut.GetForCurrentVersionAsync(parentId);

        Assert.Empty(links);
    }

    // ---- E5 (#213, ADR-067): reverse lookup for the dependency guard ----

    [Fact]
    public async Task GetReferencingParents_NoReferences_ReturnsEmpty()
    {
        using var fixture = await CreateAndMigrate();
        var childA = await SeedItemAsync(fixture, "free-child", "Свободный отвод");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        var references = await sut.GetReferencingParentsAsync(childA);

        Assert.Empty(references);
    }

    [Fact]
    public async Task GetReferencingParents_CurrentAndArchivedVersions_AllReturned()
    {
        using var fixture = await CreateAndMigrate();
        // ADR-067 core semantics: an ARCHIVED parent version blocks exactly
        // like the current one. Parent current label = v2; links live on
        // v1 (archived) and v2 (current).
        var parentId = "guard-parent-1";
        await SeedItemAsync(fixture, parentId, "Трубы ГОСТ", currentVersionLabel: "v2");
        var v1 = await SeedVersionRowAsync(fixture, parentId, "v1");
        var v2 = await SeedVersionRowAsync(fixture, parentId, "v2");
        var child = await SeedItemAsync(fixture, "guard-child-1", "Отвод");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, v1, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.Routing, "Отвод:Стандарт", 0),
        });
        await sut.ReplaceForVersionAsync(parentId, v2, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.Routing, "Отвод:Стандарт", 0),
        });

        var references = await sut.GetReferencingParentsAsync(child);

        Assert.Equal(2, references.Count);
        var archived = Assert.Single(references, r => r.VersionLabel == "v1");
        Assert.False(archived.IsCurrentVersion);
        var current = Assert.Single(references, r => r.VersionLabel == "v2");
        Assert.True(current.IsCurrentVersion);
        Assert.All(references, r => Assert.Equal("Трубы ГОСТ", r.ParentName));
    }

    [Fact]
    public async Task GetReferencingParents_MultipleParents_AllReturned()
    {
        using var fixture = await CreateAndMigrate();
        var (parentA, parentAv) = await SeedParentWithCurrentVersionAsync(fixture, "guard-parent-a");
        var (parentB, parentBv) = await SeedParentWithCurrentVersionAsync(fixture, "guard-parent-b");
        var child = await SeedItemAsync(fixture, "guard-child-2", "Тройник");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentA, parentAv, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.Routing, "Тройник:Стандарт", 0),
        });
        await sut.ReplaceForVersionAsync(parentB, parentBv, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.Routing, "Тройник:Большой", 0),
        });

        var references = await sut.GetReferencingParentsAsync(child);

        Assert.Equal(2, references.Count);
        Assert.Contains(references, r => r.ParentCatalogItemId == parentA);
        Assert.Contains(references, r => r.ParentCatalogItemId == parentB);
    }

    [Fact]
    public async Task GetReferencingParents_SameVersionMultipleKinds_CollapsesToOne()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "guard-parent-3");
        var child = await SeedItemAsync(fixture, "guard-child-3", "Отвод");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        // Same parent version, two kinds (routing + shared_nested) — the
        // guard list must show the parent version once.
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.Routing, "Отвод:Стандарт", 0),
            new FamilyDependencyInfo(child, FamilyDependencyKind.SharedNested, null, 1),
        });

        var references = await sut.GetReferencingParentsAsync(child);

        Assert.Single(references);
    }

    [Fact]
    public async Task GetReferencingParentsBatch_OnlyReferencedChildrenHaveEntries()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "guard-parent-4");
        var referenced = await SeedItemAsync(fixture, "guard-child-4a", "Отвод A");
        var free = await SeedItemAsync(fixture, "guard-child-4b", "Отвод B");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(referenced, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });

        var batch = await sut.GetReferencingParentsBatchAsync(new[] { referenced, free });

        Assert.Single(batch);
        Assert.True(batch.ContainsKey(referenced));
        Assert.False(batch.ContainsKey(free));
    }

    [Fact]
    public async Task GetReferencingParents_ParentVersionDeleted_ChildFreed()
    {
        using var fixture = await CreateAndMigrate();
        // Освобождение по ADR-067: удаление версии родителя (CASCADE по
        // parent_version_id) снимает её ссылки. Parent: v1 (archived,
        // ссылка), v2 (current, без ссылок).
        var parentId = "guard-parent-5";
        await SeedItemAsync(fixture, parentId, "Родитель", currentVersionLabel: "v2");
        var v1 = await SeedVersionRowAsync(fixture, parentId, "v1");
        await SeedVersionRowAsync(fixture, parentId, "v2");
        var child = await SeedItemAsync(fixture, "guard-child-5", "Отвод");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, v1, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.Routing, "Отвод:Стандарт", 0),
        });
        Assert.Single(await sut.GetReferencingParentsAsync(child));

        var result = await fixture.GetProvider().DeleteVersionAsync(parentId, "v1");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(await sut.GetReferencingParentsAsync(child));
    }

    [Fact]
    public async Task GetReferencingParents_ParentItemDeleted_ChildFreed()
    {
        using var fixture = await CreateAndMigrate();
        // Освобождение по ADR-067: удаление родителя целиком (CASCADE по
        // parent_catalog_item_id) снимает все его ссылки.
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "guard-parent-6");
        var child = await SeedItemAsync(fixture, "guard-child-6", "Отвод");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.Routing, "Отвод:Стандарт", 0),
        });
        Assert.Single(await sut.GetReferencingParentsAsync(child));

        var deleted = await fixture.GetProvider().DeleteItemAsync(parentId);

        Assert.True(deleted);
        Assert.Empty(await sut.GetReferencingParentsAsync(child));
    }
    // ---- E2 (#209, V30): embedded child version + drift detection ----

    [Fact]
    public async Task ChildVersionLabel_RoundtripsThroughWriteAndRead()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "drift-parent-0");
        var child = await SeedItemAsync(fixture, "drift-child-0", "Фланец", currentVersionLabel: "v2");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.SharedNested, null, 0, ChildVersionLabel: "v1"),
        });

        var link = Assert.Single(await sut.GetForCurrentVersionAsync(parentId));
        Assert.Equal("v1", link.ChildVersionLabel);
    }

    [Fact]
    public async Task GetDependencyDrift_EmbeddedDiffersFromCurrent_ReportsDrift()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "drift-parent-1");
        var child = await SeedItemAsync(fixture, "drift-child-1", "Фланец ответный", currentVersionLabel: "v2");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.SharedNested, null, 0, ChildVersionLabel: "v1"),
        });

        var drift = await sut.GetDependencyDriftBatchAsync(new[] { parentId });

        var entry = Assert.Single(drift);
        Assert.Equal(parentId, entry.Key);
        var row = Assert.Single(entry.Value);
        Assert.Equal(child, row.ChildCatalogItemId);
        Assert.Equal("Фланец ответный", row.ChildName);
        Assert.Equal("v1", row.EmbeddedVersionLabel);
        Assert.Equal("v2", row.CurrentVersionLabel);
    }

    [Fact]
    public async Task GetDependencyDrift_EmbeddedEqualsCurrent_NoDrift()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "drift-parent-2");
        var child = await SeedItemAsync(fixture, "drift-child-2", "Фланец", currentVersionLabel: "v2");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.SharedNested, null, 0, ChildVersionLabel: "v2"),
        });

        var drift = await sut.GetDependencyDriftBatchAsync(new[] { parentId });

        Assert.Empty(drift);
    }

    [Fact]
    public async Task GetDependencyDrift_NullEmbeddedLabel_NeverDrifts()
    {
        using var fixture = await CreateAndMigrate();
        // Legacy V29 link (no child_version_label) — unknown, never drifted.
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "drift-parent-3");
        var child = await SeedItemAsync(fixture, "drift-child-3", "Отвод", currentVersionLabel: "v2");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.Routing, "Отвод:Стандарт", 0),
        });

        var drift = await sut.GetDependencyDriftBatchAsync(new[] { parentId });

        Assert.Empty(drift);
    }

    [Fact]
    public async Task GetDependencyDrift_LinkOnArchivedParentVersion_NotReported()
    {
        using var fixture = await CreateAndMigrate();
        // Only the parent's CURRENT version links participate in drift —
        // archived versions keep their historical embedded labels.
        var parentId = "drift-parent-4";
        await SeedItemAsync(fixture, parentId, "Родитель", currentVersionLabel: "v2");
        var v1 = await SeedVersionRowAsync(fixture, parentId, "v1");
        await SeedVersionRowAsync(fixture, parentId, "v2");
        var child = await SeedItemAsync(fixture, "drift-child-4", "Фланец", currentVersionLabel: "v3");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, v1, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.SharedNested, null, 0, ChildVersionLabel: "v1"),
        });

        var drift = await sut.GetDependencyDriftBatchAsync(new[] { parentId });

        Assert.Empty(drift);
    }

    [Fact]
    public async Task GetDependencyDrift_EmbeddedNewerThanCurrent_NoDrift()
    {
        using var fixture = await CreateAndMigrate();
        // Owner decision (#209): drift is directional. A parent embedding a
        // NEWER-than-active child version (fresh import carrying the newest
        // nested content) must NOT be flagged — otherwise every such import
        // would instantly block the just-imported parent (validator M1).
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "drift-parent-6");
        var child = await SeedItemAsync(fixture, "drift-child-6", "Фланец", currentVersionLabel: "v1");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.SharedNested, null, 0, ChildVersionLabel: "v2"),
        });

        var drift = await sut.GetDependencyDriftBatchAsync(new[] { parentId });

        Assert.Empty(drift);
    }

    [Fact]
    public async Task GetDependencyDrift_SystemParent_NeverReported()
    {
        using var fixture = await CreateAndMigrate();
        // A SYSTEM parent's sync resolves routing children dynamically at
        // their ACTIVE version — no embedded copy, no drift semantics.
        var parentId = "drift-parent-5";
        await SeedItemAsync(fixture, parentId, "Трубы ГОСТ", currentVersionLabel: "v1", familySource: "system");
        var versionId = await SeedVersionRowAsync(fixture, parentId, "v1");
        var child = await SeedItemAsync(fixture, "drift-child-5", "Отвод", currentVersionLabel: "v2");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());
        await sut.ReplaceForVersionAsync(parentId, versionId, new[]
        {
            new FamilyDependencyInfo(child, FamilyDependencyKind.Routing, "Отвод:Стандарт", 0, ChildVersionLabel: "v1"),
        });

        var drift = await sut.GetDependencyDriftBatchAsync(new[] { parentId });

        Assert.Empty(drift);
    }

    private static async Task<(string ItemId, string VersionId)> SeedParentWithCurrentVersionAsync(
        TempCatalogFixture fixture, string itemId)
    {
        await SeedItemAsync(fixture, itemId, "Родитель", currentVersionLabel: "v1");
        var versionId = await SeedVersionRowAsync(fixture, itemId, "v1");
        return (itemId, versionId);
    }

    private static async Task<string> SeedItemAsync(
        TempCatalogFixture fixture, string itemId, string name, string? currentVersionLabel = null,
        string familySource = "loadable")
    {
        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, description, category_name, manufacturer, content_status, current_version_label, published_by, family_source, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @norm, NULL, NULL, NULL, 'Active', @currentLabel, NULL, @source, '2026-08-06T00:00:00Z', '2026-08-06T00:00:00Z')
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", itemId));
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        cmd.Parameters.Add(new SqliteParameter("@norm", name.ToUpperInvariant()));
        cmd.Parameters.Add(new SqliteParameter("@source", familySource));
        cmd.Parameters.Add(new SqliteParameter("@currentLabel",
            currentVersionLabel is null ? DBNull.Value : currentVersionLabel));
        await cmd.ExecuteNonQueryAsync();
        return itemId;
    }

    private static async Task<string> SeedVersionRowAsync(
        TempCatalogFixture fixture, string itemId, string versionLabel)
    {
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using (var fileCmd = connection.CreateCommand())
        {
            fileCmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, 'files/x.rvt', 'x.rvt', 2025, '2026-08-06T00:00:00Z')
                """;
            fileCmd.Parameters.Add(new SqliteParameter("@id", fileId));
            await fileCmd.ExecuteNonQueryAsync();
        }

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, published_at_utc)
            VALUES (@id, @itemId, @fileId, @label, 2025, '2026-08-06T00:00:00Z')
            """;
        versionCmd.Parameters.Add(new SqliteParameter("@id", versionId));
        versionCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        versionCmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        versionCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
        await versionCmd.ExecuteNonQueryAsync();
        return versionId;
    }
}
