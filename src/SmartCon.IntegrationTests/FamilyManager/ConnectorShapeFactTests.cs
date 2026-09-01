using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Revit.FamilyManager;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Баг 8 (стресс-тест владельца 2026-09-01): факт <c>connector_shape</c>
/// (битмаска Round=1/Rectangular=2/Oval=4) извлекается из коннекторов
/// семейства — основа фильтра пикера по форме коннектора хоста. Круглый
/// фитинг → маска 1; двухформенный переход → обе биты (пикер матчит по
/// любому концу). Библиотека владельца — skip-guard при отсутствии.
/// </summary>
public sealed class ConnectorShapeFactTests : RevitApiTest
{
    private const string LibraryRoot
        = @"d:\Project\dotNET\00_Архив\Библиотеки семейств\Тест";

    [Test]
    public async Task Extract_PipeFitting_RoundConnectors_MaskIsRound()
    {
        var path = FindByNameFragment("KAN-therm_Inox.rfa");
        if (path is null) { Skip.Test("Тестовая библиотека недоступна"); return; }

        var doc = OpenGuarded(path);
        if (doc is null) { Skip.Test("Библиотека сохранена в более новой версии Revit"); return; }
        try
        {
            var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);
            var fact = snapshot.Facts?.FirstOrDefault(f => f.FactKey == FamilyFactRuleSet.ConnectorShapeFactKey);
            await Assert.That(fact).IsNotNull();
            await Assert.That(fact!.ValueKey).IsEqualTo("1"); // Round only
            await Assert.That(fact.ValueDisplay).IsEqualTo("Round");
        }
        finally
        {
            doc.Close(false);
        }
    }

    [Test]
    public async Task Extract_DuctFitting_ConnectorShapeMatchesConnectors()
    {
        if (!Directory.Exists(LibraryRoot)) { Skip.Test("Тестовая библиотека недоступна"); return; }

        // Ищем первое duct-fitting семейство библиотеки: маска факта обязана
        // совпасть с битами коннекторов, прочитанными напрямую из документа.
        var anySkippedByVersion = false;
        foreach (var path in Directory.EnumerateFiles(LibraryRoot, "*.rfa", SearchOption.AllDirectories))
        {
            var doc = OpenGuarded(path);
            if (doc is null) { anySkippedByVersion = true; continue; }
            try
            {
                if (doc.OwnerFamily?.FamilyCategory?.Id.GetValue() != (int)BuiltInCategory.OST_DuctFitting)
                    continue;

                var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);
                var fact = snapshot.Facts?.FirstOrDefault(f => f.FactKey == FamilyFactRuleSet.ConnectorShapeFactKey);
                await Assert.That(fact).IsNotNull();

                var expectedMask = 0;
                foreach (var connector in new FilteredElementCollector(doc).OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>())
                {
                    expectedMask |= connector.Shape switch
                    {
                        ConnectorProfileType.Round => 1,
                        ConnectorProfileType.Rectangular => 2,
                        ConnectorProfileType.Oval => 4,
                        _ => 0,
                    };
                }
                await Assert.That(fact!.ValueKey).IsEqualTo(expectedMask.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            finally
            {
                doc.Close(false);
            }
        }

        Skip.Test(anySkippedByVersion
            ? "Библиотека сохранена в более новой версии Revit"
            : "В тестовой библиотеке нет duct-fitting семейств");
    }

    private static string? FindByNameFragment(string fragment)
        => Directory.Exists(LibraryRoot)
            ? Directory
                .EnumerateFiles(LibraryRoot, "*.rfa", SearchOption.AllDirectories)
#pragma warning disable CA2249 // IndexOf for net48 compat — string.Contains(string, StringComparison) is net8+ only
                .FirstOrDefault(p => p.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
#pragma warning restore CA2249
            : null;

    /// <summary>
    /// Библиотека владельца сохранена в Revit 2025 — на net48-прогоне
    /// (Revit 2021–2024) OpenDocumentFile бросает CorruptModelException
    /// ("saved by a later version"). <c>null</c> = версия не подходит,
    /// вызывающий Skip.Test'ит (факт проверяется на R25-прогоне).
    /// </summary>
    private Document? OpenGuarded(string path)
    {
        try
        {
            return Application.OpenDocumentFile(path);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"ConnectorShapeFact: '{path}' не открывается в Revit {Application.VersionNumber}: {ex.GetType().Name}");
            return null;
        }
    }
}
