using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Import Validation Gate на реальных данных Revit (ADR-059): цепочка
/// .rfa → RevitFamilySnapshotExtractor → FamilyValidationInput →
/// FamilyValidationEngine. Доказывает, что реальные display-строки
/// параметров («300 mm», «1/2"») корректно парсятся DisplayValueParser'ом
/// и числовые правила в display-единицах сравниваются с реальными
/// значениями (DisplayNumber-first).
/// Маппинг snapshot→input продублирован инлайн: production-маппер
/// (SnapshotValidationMapper) живёт в SmartCon.FamilyManager, которая не
/// референсится тест-хостом; он покрыт юнит-тестами.
/// </summary>
public sealed class ValidationChainOnRealDataTests : RevitApiTest
{
    [Test]
    public async Task RealSnapshot_NumericParameters_ParseToDisplayNumbers()
    {
        // Arrange
        var snapshot = ExtractSampleSnapshot("rme_basic_sample_family.rfa");
        if (snapshot is null)
        {
            return; // Skip.Test уже вызван
        }

        // Act — реальные display-строки Revit → DisplayNumber
        var displays = snapshot.Types
            .SelectMany(t => t.Values)
            .Where(v => v.HasValue && !string.IsNullOrWhiteSpace(v.ValueDisplay))
            .Select(v => (v.ParameterName, v.ValueDisplay!, Parsed: DisplayValueParser.TryParseNumber(v.ValueDisplay)))
            .ToList();

        // Assert — хотя бы часть реальных display-строк обязана парситься
        using (Assert.Multiple())
        {
            await Assert.That(displays.Count).IsGreaterThan(0);
            await Assert.That(displays.Count(d => d.Parsed.HasValue)).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task EndToEnd_HasValueRule_OnExistingParameter_PassesAllTypes()
    {
        // Arrange
        var snapshot = ExtractSampleSnapshot("rme_basic_sample_family.rfa");
        if (snapshot is null)
        {
            return;
        }

        var input = ToValidationInput(snapshot);
        var filledName = snapshot.Types
            .SelectMany(t => t.Values)
            .Where(v => v.HasValue)
            .GroupBy(v => v.ParameterName)
            .Where(g => g.Count() == snapshot.Types.Count)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();
        if (filledName is null)
        {
            Skip.Test("В sample-семействе нет параметра, заполненного на всех типах");
        }

        var rules = new[]
        {
            MakeRule(filledName!, ValidationRuleOperator.HasValue),
        };

        // Act
        var report = new FamilyValidationEngine().Validate(input, rules);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(report.IsValid).IsTrue();
            await Assert.That(report.RulesEvaluated).IsEqualTo(input.Types.Count);
            await Assert.That(report.Violations.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task EndToEnd_HasValueRule_OnMissingParameter_FailsEveryType()
    {
        // Arrange — жёсткий гейт: правило на параметре, которого нет
        // в семействе, валит КАЖДЫЙ тип
        var snapshot = ExtractSampleSnapshot("rme_basic_sample_family.rfa");
        if (snapshot is null)
        {
            return;
        }

        var input = ToValidationInput(snapshot);
        var rules = new[]
        {
            MakeRule("SMARTCON_NO_SUCH_PARAMETER", ValidationRuleOperator.HasValue),
        };

        // Act
        var report = new FamilyValidationEngine().Validate(input, rules);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(report.IsValid).IsFalse();
            await Assert.That(report.Violations.Count).IsEqualTo(input.Types.Count);
            await Assert.That(report.Violations[0].AttributeName).IsEqualTo("SMARTCON_NO_SUCH_PARAMETER");
        }
    }

    [Test]
    public async Task EndToEnd_BetweenRule_ComparesInDisplayUnits()
    {
        // Arrange — находим реальный числовой параметр с парсящимся
        // display-значением; правило Between в display-единицах вокруг
        // значения обязано пройти, а узкий диапазон вне — упасть
        var snapshot = ExtractSampleSnapshot("rme_basic_sample_family.rfa");
        if (snapshot is null)
        {
            return;
        }

        var input = ToValidationInput(snapshot);
        var candidate = input.Types
            .SelectMany(t => t.Values)
            .Where(v => v.DisplayNumber.HasValue && v.DisplayNumber.Value > 0)
            .GroupBy(v => v.ParameterName)
            .Where(g => g.Count() == input.Types.Count)
            .SelectMany(g => g)
            .FirstOrDefault();
        if (candidate is null)
        {
            Skip.Test("В sample-семействе нет числового параметра с парсящимся display-значением на всех типах");
        }

        // Диапазоны — от min/max значений параметра по ВСЕМ типам:
        // wide накрывает каждый тип, narrow заведомо вне любого значения
        var allValues = input.Types
            .SelectMany(t => t.Values)
            .Where(v => string.Equals(v.ParameterName, candidate!.ParameterName, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.DisplayNumber!.Value)
            .ToList();
        var min = allValues.Min();
        var max = allValues.Max();
        var span = global::System.Math.Max(max - min, 1.0);
        var wide = new[] { MakeBetweenRule(candidate!.ParameterName, min - span, max + span) };
        var narrow = new[] { MakeBetweenRule(candidate.ParameterName, max + span * 2, max + span * 3) };

        // Act
        var engine = new FamilyValidationEngine();
        var wideReport = engine.Validate(input, wide);
        var narrowReport = engine.Validate(input, narrow);

        // Assert — широкий диапазон: типы с этим значением проходят;
        // узкий (вне значения): хотя бы одно нарушение
        using (Assert.Multiple())
        {
            await Assert.That(
                wideReport.Violations.Count(v => v.AttributeName == candidate.ParameterName)).IsEqualTo(0);
            await Assert.That(
                narrowReport.Violations.Count(v => v.AttributeName == candidate.ParameterName)).IsGreaterThan(0);
        }
    }

    private FamilySnapshot? ExtractSampleSnapshot(string fileName)
    {
        var path = SampleFiles.FindSample(Application, fileName);
        if (path is null)
        {
            Skip.Test($"Sample-семейство {fileName} не найдено");
            return null;
        }

        var document = Application.OpenDocumentFile(path);
        try
        {
            return new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(document);
        }
        finally
        {
            document.Close(false);
        }
    }

    private static FamilyValidationInput ToValidationInput(FamilySnapshot snapshot)
    {
        var types = snapshot.Types
            .Select(t => new FamilyTypeValidationData(
                t.Name,
                t.Values.Select(ToValidationValue).ToList()))
            .ToList();

        return new FamilyValidationInput(types);
    }

    private static ParameterValidationValue ToValidationValue(FamilyParameterValue v) =>
        new(
            v.ParameterName,
            IsPresent: true,
            ValueText: v.HasValue ? v.ValueText : null,
            ValueNumber: v.HasValue ? v.ValueNumber : null,
            UnitTypeId: v.UnitTypeId,
            DisplayNumber: v.HasValue ? DisplayValueParser.TryParseNumber(v.ValueDisplay) : null);

    private static EffectiveValidationRule MakeRule(string attributeName, ValidationRuleOperator op) =>
        new(attributeName, IsInherited: false,
            new ValidationRule(
                Id: "rule-1",
                BindingId: "binding-1",
                Operator: op,
                ValueText: null,
                ValueNumber: null,
                MinValue: null,
                MaxValue: null,
                UnitTypeId: null,
                SortOrder: 0,
                IsEnabled: true));

    private static EffectiveValidationRule MakeBetweenRule(string attributeName, double min, double max) =>
        new(attributeName, IsInherited: false,
            new ValidationRule(
                Id: "rule-2",
                BindingId: "binding-1",
                Operator: ValidationRuleOperator.Between,
                ValueText: null,
                ValueNumber: null,
                MinValue: min,
                MaxValue: max,
                UnitTypeId: null,
                SortOrder: 0,
                IsEnabled: true));
}
