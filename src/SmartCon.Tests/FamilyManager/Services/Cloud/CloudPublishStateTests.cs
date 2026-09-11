using System.IO;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services.Cloud;

/// <summary>
/// CloudPublishStateService — точка «есть локальные непубликованные изменения»
/// на шестерёнке (владелец 2026-09-11). Дайджест манифеста без волатильных
/// полей: та же база до/после publish с другим seq/временем/автором = без
/// изменений; правка контента = изменения.
/// </summary>
public sealed class CloudPublishStateTests : IDisposable
{
    private readonly TempCatalogFixture _source = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly string _stateDir;
    private readonly CloudPublishStateService _state;

    public CloudPublishStateTests()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), $"CloudPubState_{Guid.NewGuid():N}");
        var builder = new CatalogManifestBuilder(
            _source.GetDatabase(), new StoragePathResolver(_source.GetDatabase()), _clock);
        _state = new CloudPublishStateService(builder, _stateDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_stateDir, recursive: true); } catch (IOException) { }
        _source.Dispose();
    }

    private async Task<CloudPublishResult> PublishLikeTheServiceDoesAsync(string slug, string catalogId)
    {
        var manifest = await new CatalogManifestBuilder(
                _source.GetDatabase(), new StoragePathResolver(_source.GetDatabase()), _clock)
            .BuildAsync(new CatalogManifestBuildOptions
            {
                CatalogId = catalogId,
                PublishSeq = 1,
                PublishedBy = "Иван",
            });
        _state.RecordPublished(slug, manifest);
        return new CloudPublishResult(1, manifest.Items.Count, 0, manifest);
    }

    [Fact]
    public async Task NoFingerprintFile_HasUnpublishedChangesTrue()
    {
        // Никогда не публиковали с этой машины — точка горит (если есть что публиковать).
        await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Отвод", "v1");

        Assert.True(await _state.HasUnpublishedChangesAsync("demo", "cid"));
    }

    [Fact]
    public async Task EmptyCatalog_NeverPublished_NoDot()
    {
        // Пустая база без публикаций — публиковать нечего, точка не горит
        // (стресс-тест 2026-09-11: точка на только что созданной пустой базе = шум).
        Assert.False(await _state.HasUnpublishedChangesAsync("demo", "cid"));
    }

    [Fact]
    public async Task AfterPublish_NoLocalEdits_HasUnpublishedChangesFalse()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Отвод", "v1");
        await PublishLikeTheServiceDoesAsync("demo", "cid");

        // Пересборка с ДРУГИМИ волатильными полями (seq/время/автор) — тот же контент.
        Assert.False(await _state.HasUnpublishedChangesAsync("demo", "cid"));
    }

    [Fact]
    public async Task LocalEdit_AfterPublish_HasUnpublishedChangesTrue()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Отвод", "v1");
        await PublishLikeTheServiceDoesAsync("demo", "cid");

        await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Тройник", "v1");

        Assert.True(await _state.HasUnpublishedChangesAsync("demo", "cid"));
    }

    [Fact]
    public async Task Clear_RemovesFingerprint_HasUnpublishedChangesTrueAgain()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Отвод", "v1");
        await PublishLikeTheServiceDoesAsync("demo", "cid");
        Assert.False(await _state.HasUnpublishedChangesAsync("demo", "cid"));

        _state.Clear("demo");

        Assert.True(await _state.HasUnpublishedChangesAsync("demo", "cid"));
    }

    [Fact]
    public void Fingerprint_IgnoresVolatileFields()
    {
        // Один контент, разные seq/время/автор → дайджест совпадает.
        var item = new ManifestItemV1 { Id = "a", Name = "Отвод" };
        var first = new CatalogManifestV1 { CatalogId = "cid", PublishSeq = 1, PublishedBy = "Иван", Items = [item] };
        var second = new CatalogManifestV1
        {
            CatalogId = "cid",
            PublishSeq = 42,
            PublishedBy = "Пётр",
            MinPluginVersion = "9.9.9",
            Items = [item],
        };

        Assert.Equal(
            CatalogManifestFingerprint.Compute(first),
            CatalogManifestFingerprint.Compute(second));
    }

    [Fact]
    public void Fingerprint_IgnoresByteLevelFileHash()
    {
        // Владелец 2026-09-11: Revit пересохраняет .rfa без изменения содержимого —
        // битовый sha/размер меняются, FHV contentHash нет. Детекция изменений
        // (точка «не забудь опубликовать» + дельта) обязана молчать.
        ManifestItemV1 Item(string fileSha, long size, string name) => new()
        {
            Id = "a",
            Name = "Отвод",
            Versions =
            [
                new ManifestVersionV1
                {
                    VersionLabel = "v1",
                    ContentHash = "FHV-SAME",
                    SourceRevitVersion = 2025,
                    File = new ManifestFileRefV1 { Sha256 = fileSha, SizeBytes = size, FileName = name },
                },
            ],
        };

        var first = new CatalogManifestV1 { CatalogId = "cid", PublishSeq = 1, Items = [Item("aa", 100, "a.rfa")] };
        var second = new CatalogManifestV1 { CatalogId = "cid", PublishSeq = 2, Items = [Item("bb", 200, "b.rfa")] };

        Assert.Equal(
            CatalogManifestFingerprint.Compute(first),
            CatalogManifestFingerprint.Compute(second));
        Assert.Equal(
            CatalogManifestFingerprint.ComputeItemDigest(first.Items[0]),
            CatalogManifestFingerprint.ComputeItemDigest(second.Items[0]));
    }
}
