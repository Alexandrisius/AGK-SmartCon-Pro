using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// RevitFamilyHealthChecker (ADR-059): единственный Revit-boundary компонент
/// Import Validation Gate. Проверяет механику на реальном Revit: per-type
/// CurrentType + Regenerate внутри TransactionGroup с полным rollback
/// (документ НЕ модифицируется), silent rollback без error-диалогов,
/// warnings-only путь для активного документа (UC-2).
/// Битое семейство (битая формула/констрейнт) детерминированно не сеется
/// через API — Revit валидирует формулы при присвоении и не даёт удалить
/// параметр, используемый в формуле. Путь ошибок покрыт юнит-тестами
/// FamilyHealthReport.FromIssues.
/// </summary>
public sealed class RevitFamilyHealthCheckerTests : RevitApiTest
{
    // Лениво: инициализатор поля с типом из SmartCon.Revit грузит RevitAPI
    // ДО инъекции и ломает инжектор (правило #1, Nice3point/RevitUnit#78)
    private static RevitFamilyHealthChecker Checker => new();

    [Test]
    public async Task CheckFamilyDocument_HealthySample_ReturnsHealthy()
    {
        // Arrange
        var path = SampleFiles.FindSample(Application, "rac_advanced_sample_family.rfa");
        if (path is null)
        {
            Skip.Test("Sample-семейство rac_advanced_sample_family.rfa не найдено");
        }

        var document = Application.OpenDocumentFile(path);
        try
        {
            // Act
            var report = Checker.CheckFamilyDocument(document);

            // Assert
            using (Assert.Multiple())
            {
                await Assert.That(report.IsHealthy).IsTrue();
                await Assert.That(
                    report.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error)).IsEqualTo(0);
            }
        }
        finally
        {
            document.Close(false);
        }
    }

    [Test]
    public async Task CheckFamilyDocument_MultiTypeSample_LeavesDocumentUnmodified()
    {
        // Arrange — health check обязан откатить TransactionGroup:
        // IsModified документа не меняется, CurrentType не запоминается
        var path = SampleFiles.FindSample(Application, "rme_advanced_sample_family.rfa");
        if (path is null)
        {
            Skip.Test("Sample-семейство rme_advanced_sample_family.rfa не найдено");
        }

        var document = Application.OpenDocumentFile(path);
        try
        {
            var wasModified = document.IsModified;

            // Act
            var report = Checker.CheckFamilyDocument(document);

            // Assert
            using (Assert.Multiple())
            {
                await Assert.That(report.IsHealthy).IsTrue();
                await Assert.That(document.IsModified).IsEqualTo(wasModified);
            }
        }
        finally
        {
            document.Close(false);
        }
    }

    [Test]
    public async Task CheckFamilyDocument_NonFamilyDocument_ReturnsReportWithoutThrow()
    {
        // Arrange — project-документ: геттер FamilyManager бросает
        // InvalidOperationException (не возвращает null) → гард
        // IsFamilyDocument, только document warnings, без исключения
        var document = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            // Act
            var report = Checker.CheckFamilyDocument(document);

            // Assert
            using (Assert.Multiple())
            {
                await Assert.That(report).IsNotNull();
                await Assert.That(
                    report.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error)).IsEqualTo(0);
            }
        }
        finally
        {
            document.Close(false);
        }
    }

    [Test]
    public async Task CheckFamilyDocument_CancelledToken_ThrowsOperationCanceled()
    {
        // Arrange
        var path = SampleFiles.FindSample(Application, "rac_advanced_sample_family.rfa");
        if (path is null)
        {
            Skip.Test("Sample-семейство rac_advanced_sample_family.rfa не найдено");
        }

        var document = Application.OpenDocumentFile(path);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act + Assert
            await Assert.That(() => Checker.CheckFamilyDocument(document, cts.Token))
                .Throws<OperationCanceledException>();
        }
        finally
        {
            document.Close(false);
        }
    }

    [Test]
    public async Task CheckActiveFamilyDocument_HealthySample_WarningsOnlyHealthy()
    {
        // Arrange — UC-2: активный family-документ, только накопленные
        // warnings, без переключения типов
        var path = SampleFiles.FindSample(Application, "rac_basic_sample_family.rfa");
        if (path is null)
        {
            Skip.Test("Sample-семейство rac_basic_sample_family.rfa не найдено");
        }

        var document = Application.OpenDocumentFile(path);
        try
        {
            // Act
            var report = Checker.CheckActiveFamilyDocument(document);

            // Assert
            using (Assert.Multiple())
            {
                await Assert.That(report.IsHealthy).IsTrue();
                await Assert.That(
                    report.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error)).IsEqualTo(0);
            }
        }
        finally
        {
            document.Close(false);
        }
    }
}
