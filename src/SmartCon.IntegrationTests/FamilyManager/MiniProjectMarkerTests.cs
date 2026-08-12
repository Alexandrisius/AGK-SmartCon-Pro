using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// RevitMiniProjectMarker (Issue #188): ES-маркер SmartCon_MiniProject_v1 на
/// DataStorage внутри staged мини-проекта. Маркер — единственная авторизация
/// для Close-without-save после реимпорта (#186) и для guard'а
/// автопереключения активной БД. Покрытие: запись/чтение, отсутствие маркера
/// в обычном проекте, выживание через SaveAs+reopen, идемпотентность.
/// </summary>
public sealed class MiniProjectMarkerTests : RevitApiTest
{
    private Document? _document;
    private RevitMiniProjectMarker? _marker;
    private string? _tempPathToCleanup;

    private Document Doc => _document!;
    private RevitMiniProjectMarker Marker => _marker!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocument()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        var transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _marker = new RevitMiniProjectMarker(transactions, new SystemClock());
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        _document?.Close(false);
        _document = null;
        if (_tempPathToCleanup is not null && File.Exists(_tempPathToCleanup))
        {
            File.Delete(_tempPathToCleanup);
            _tempPathToCleanup = null;
        }
    }

    [Test]
    public async Task IsMiniProject_UnmarkedDocument_ReturnsFalse()
    {
        var result = Marker.IsMiniProject(Doc);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ReadCatalogItemId_UnmarkedDocument_ReturnsNull()
    {
        var result = Marker.ReadCatalogItemId(Doc);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task MarkAsMiniProject_ThenIsMiniProject_ReturnsTrue()
    {
        Marker.MarkAsMiniProject(Doc, "catalog-item-42");

        await Assert.That(Marker.IsMiniProject(Doc)).IsTrue();
    }

    [Test]
    public async Task MarkAsMiniProject_ThenRead_RoundTripsCatalogItemId()
    {
        Marker.MarkAsMiniProject(Doc, "catalog-item-42");

        await Assert.That(Marker.ReadCatalogItemId(Doc)).IsEqualTo("catalog-item-42");
    }

    [Test]
    public async Task MarkAsMiniProject_NullCatalogItemId_MarkerStillValid()
    {
        Marker.MarkAsMiniProject(Doc, null);

        await Assert.That(Marker.IsMiniProject(Doc)).IsTrue();
        await Assert.That(Marker.ReadCatalogItemId(Doc)).IsNull();
    }

    [Test]
    public async Task MarkAsMiniProject_Twice_IsIdempotent()
    {
        Marker.MarkAsMiniProject(Doc, "catalog-item-42");
        Marker.MarkAsMiniProject(Doc, "catalog-item-42");

        await Assert.That(Marker.IsMiniProject(Doc)).IsTrue();
        await Assert.That(Marker.ReadCatalogItemId(Doc)).IsEqualTo("catalog-item-42");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Marker_SurvivesSaveAsAndReopen()
    {
        // SaveAs re-targets the SAME Document object at the new path — the
        // document must be closed before reopening, and the After-hook closes
        // whatever _document points at (the reopened one here).
#pragma warning disable TUnit0018 // reopen flow requires swapping the fixture document
        _tempPathToCleanup = Path.Combine(Path.GetTempPath(), $"smartcon-mkp-{Guid.NewGuid().ToString("N")}.rvt");

        Marker.MarkAsMiniProject(Doc, "catalog-item-42");
        Doc.SaveAs(_tempPathToCleanup, new SaveAsOptions { OverwriteExistingFile = true });
        Doc.Close(false);

        _document = Application.OpenDocumentFile(_tempPathToCleanup);
#pragma warning restore TUnit0018

        await Assert.That(Marker.IsMiniProject(Doc)).IsTrue();
        await Assert.That(Marker.ReadCatalogItemId(Doc)).IsEqualTo("catalog-item-42");
    }
}
