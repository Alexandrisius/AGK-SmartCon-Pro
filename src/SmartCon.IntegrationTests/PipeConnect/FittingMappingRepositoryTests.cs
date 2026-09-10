using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Storage;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.PipeConnect;

/// <summary>
/// RevitFittingMappingRepository (ADR-012, I-13): маппинг фитингов в
/// ExtensibleStorage реального документа. Roundtrip ловит регрессии схемы
/// и JSON-сериализации, которые юнит-тесты сериализатора не видят.
/// </summary>
public sealed class FittingMappingRepositoryTests : RevitApiTest
{
    private Document? _document;
    private RevitFittingMappingRepository? _repository;

    private Document Doc => _document!;
    private RevitFittingMappingRepository Repository => _repository!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void OpenDocument()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        var transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _repository = new RevitFittingMappingRepository(new StubRevitContext(Doc), transactions);
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task SaveMappingRules_ThenGet_RoundTripsInRealDocument()
    {
        // Arrange
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(2),
            IsDirectConnect = true,
            FittingFamilies = [new FittingMapping { FamilyName = "SmartCon Elbow", Priority = 1 }]
        };

        // Act
        var beforeSave = Repository.GetMappingRules();
        Repository.SaveMappingRules([rule]);
        var afterSave = Repository.GetMappingRules();

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(beforeSave.Count).IsEqualTo(0);
            await Assert.That(afterSave.Count).IsEqualTo(1);
            await Assert.That(afterSave[0].FromType.Value).IsEqualTo(1);
            await Assert.That(afterSave[0].ToType.Value).IsEqualTo(2);
            await Assert.That(afterSave[0].IsDirectConnect).IsTrue();
            await Assert.That(afterSave[0].FittingFamilies[0].FamilyName).IsEqualTo("SmartCon Elbow");
            await Assert.That(Repository.GetStoragePath()).StartsWith("ExtensibleStorage:");
        }
    }

    [Test]
    public async Task SaveConnectorTypes_PreservesPreviouslySavedRules()
    {
        // Arrange — merge-семантика: запись типов не затирает правила и наоборот
        Repository.SaveMappingRules(
        [
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(3),
                ToType = new ConnectionTypeCode(3)
            }
        ]);

        // Act
        Repository.SaveConnectorTypes(
        [
            new ConnectorTypeDefinition { Code = 3, Name = "Сварка", Description = "ГОСТ 16037" }
        ]);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(Repository.GetMappingRules().Count).IsEqualTo(1);
            await Assert.That(Repository.GetConnectorTypes().Count).IsEqualTo(1);
            await Assert.That(Repository.GetConnectorTypes()[0].Name).IsEqualTo("Сварка");
            await Assert.That(CountSmartConDataStorage()).IsEqualTo(1);
        }
    }

    private int CountSmartConDataStorage()
    {
        var schema = Schema.Lookup(FittingMappingSchemaGuid);
        var count = 0;
        foreach (var storage in new FilteredElementCollector(Doc)
                     .OfClass(typeof(DataStorage))
                     .Cast<DataStorage>())
        {
            using var entity = schema is null ? null : storage.GetEntity(schema);
            if (entity?.IsValid() == true)
            {
                count++;
            }
        }

        return count;
    }

    // GUID схемы SmartConFittingMappingSchema (FittingMappingSchema.cs:20) —
    // lookup по GUID, т.к. сам класс схемы internal.
    private static readonly Guid FittingMappingSchemaGuid = new("4A5C3E1F-6B2D-4E8A-9C7F-12D3E4F5A6B7");
}
