using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Storage;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.ProjectManagement;

/// <summary>
/// RevitShareProjectSettingsRepository: настройки Share Project в
/// ExtensibleStorage реального документа (per-project, как и маппинг фитингов).
/// </summary>
public sealed class ShareProjectSettingsRepositoryTests : RevitApiTest
{
    private Document? _document;
    private RevitShareProjectSettingsRepository? _repository;

    private Document Doc => _document!;
    private RevitShareProjectSettingsRepository Repository => _repository!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void OpenDocument()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        var transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _repository = new RevitShareProjectSettingsRepository(transactions);
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task Load_FreshDocument_ReturnsEmptyDefaults()
    {
        // Act
        var settings = Repository.Load(Doc);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(settings.ShareFolderPath).IsEqualTo(string.Empty);
            await Assert.That(settings.KeepViewNames.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Save_ThenLoad_RoundTripsAllSettings()
    {
        // Arrange
        var saved = new ShareProjectSettings
        {
            ShareFolderPath = @"D:\BIM\Share",
            KeepViewNames = ["SC_Alpha", "SC_Beta"],
            SyncBeforeShare = false,
            PurgeOptions = new PurgeOptions { PurgeSheets = false, PurgeUnused = false }
        };

        // Act
        Repository.Save(Doc, saved);
        var loaded = Repository.Load(Doc);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(loaded.ShareFolderPath).IsEqualTo(@"D:\BIM\Share");
            await Assert.That(loaded.KeepViewNames).IsEquivalentTo(new[] { "SC_Alpha", "SC_Beta" });
            await Assert.That(loaded.SyncBeforeShare).IsFalse();
            await Assert.That(loaded.PurgeOptions.PurgeSheets).IsFalse();
            await Assert.That(loaded.PurgeOptions.PurgeUnused).IsFalse();
        }
    }
}
