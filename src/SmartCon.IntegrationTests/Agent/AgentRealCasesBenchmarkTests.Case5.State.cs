using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using SmartCon.IntegrationTests.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed verification and the post-run state read-back of case 5: the seed is
/// re-read from the project after every EditFamily/LoadFamily (values per type,
/// formula active, instance scope, no stray parameters), and the dump renders
/// the new-parameter values per family type for the transcript. Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private void VerifyCase5Seed()
    {
        foreach (var family in _case5Families)
        {
            var seed = Case5SeedOf(family.Name);
            if (seed is null)
            {
                Skip.Test("Сид-обёртка «" + family.Name + "» потеряна");
                return;
            }

            if (family.Kind == Case5FamilyKind.NoOld)
            {
                if (seed.GetProjectParameter(FamilySeed.Type1, Case5OldMarkName) is not null)
                {
                    Skip.Test("«" + family.Name + "» не должен содержать старых параметров");
                    return;
                }

                continue;
            }

            var perType = new[]
            {
                (FamilySeed.Type1, family.Mark1, family.Note1, family.Length1Mm),
                (FamilySeed.Type2, family.Mark2, family.Note2, family.Length2Mm)
            };
            foreach (var (typeName, mark, note, lengthMm) in perType)
            {
                var markParameter = seed.GetProjectParameter(typeName, Case5OldMarkName);
                var noteParameter = seed.GetProjectParameter(typeName, Case5OldNoteName);
                var lengthParameter = seed.GetProjectParameter(typeName, Case5OldLengthName);
                if (markParameter?.AsString() is not { } markActual ||
                    noteParameter?.AsString() is not { } noteActual ||
                    lengthParameter is null)
                {
                    Skip.Test("«" + family.Name + "/" + typeName + "»: старые параметры не читаются");
                    return;
                }

                if (!string.Equals(markActual, mark, StringComparison.Ordinal) ||
                    !string.Equals(noteActual, note, StringComparison.Ordinal) ||
                    Math.Abs(Case5FeetToMm(lengthParameter.AsDouble()) - lengthMm) > Case5MmTolerance)
                {
                    Skip.Test("«" + family.Name + "/" + typeName +
                              "»: значения старых параметров не совпадают с сидом");
                    return;
                }
            }

            // Новых параметров в чистых семействах быть не должно; в формульных
            // формула уже гоняет значение (2000/3000 мм).
            var forbidden = family.Kind == Case5FamilyKind.FormulaTarget
                ? new[] { Case5NewMarkName, Case5NewNoteName }
                : family.Kind == Case5FamilyKind.ScopeConflict
                    ? new[] { Case5NewMarkName, Case5NewLengthName }
                    : new[] { Case5NewMarkName, Case5NewNoteName, Case5NewLengthName };
            foreach (var name in forbidden)
            {
                if (seed.GetProjectParameter(FamilySeed.Type1, name) is not null)
                {
                    Skip.Test("«" + family.Name + "» уже содержит «" + name + "» — сид некорректен");
                    return;
                }
            }

            if (family.Kind == Case5FamilyKind.FormulaTarget)
            {
                var driven = seed.GetProjectParameter(FamilySeed.Type1, Case5NewLengthName);
                if (driven is null ||
                    Math.Abs(Case5FeetToMm(driven.AsDouble()) - Case5FormulaDriven1Mm) >
                    Case5MmTolerance)
                {
                    Skip.Test("«" + family.Name + "»: формула «" + Case5TargetFormula +
                              "» не гонит «" + Case5NewLengthName + "» (сид некорректен)");
                    return;
                }
            }
        }
    }

    // ── State read-back ─────────────────────────────────────────────────

    private static double Case5MmToFeet(double millimeters) => millimeters / 304.8;

    private static double Case5FeetToMm(double feet) => feet * 304.8;

    private string Case5DescribeNewValue(string familyName, string typeName, string parameterName)
    {
        var parameter = Case5SeedOf(familyName)?.GetProjectParameter(typeName, parameterName);
        if (parameter is null)
            return "(нет)";
        if (!parameter.IsShared)
            return "family!";
        var suffix = parameter.GUID switch
        {
            var g when g == Case5NewMarkGuid || g == Case5NewNoteGuid || g == Case5NewLengthGuid
                => string.Empty,
            _ => "!GUID"
        };
        var value = parameter.StorageType == StorageType.Double
            ? Case5FeetToMm(parameter.AsDouble()).ToString("F0", CultureInfo.InvariantCulture) + " мм"
            : parameter.AsString() ?? "(null)";
        return value + suffix;
    }

    private string DumpCase5State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Семейство | Случай | " + Case5NewMarkName +
                          " Т1/Т2 | " + Case5NewNoteName + " Т1/Т2 | " + Case5NewLengthName +
                          " Т1/Т2 |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var family in _case5Families)
        {
            builder.AppendLine(
                "| " + family.Name + " | " + family.KindLabel + " | " +
                Case5DescribeNewValue(family.Name, FamilySeed.Type1, Case5NewMarkName) + " / " +
                Case5DescribeNewValue(family.Name, FamilySeed.Type2, Case5NewMarkName) + " | " +
                Case5DescribeNewValue(family.Name, FamilySeed.Type1, Case5NewNoteName) + " / " +
                Case5DescribeNewValue(family.Name, FamilySeed.Type2, Case5NewNoteName) + " | " +
                Case5DescribeNewValue(family.Name, FamilySeed.Type1, Case5NewLengthName) + " / " +
                Case5DescribeNewValue(family.Name, FamilySeed.Type2, Case5NewLengthName) + " |");
        }

        builder.AppendLine();
        builder.AppendLine("Ожидание после миграции: чистые 01–06 и формульные 07–08 — новые " +
                          "равны старым (по типам), у 07–08 формула снята; 09–10 — пропуск " +
                          "«" + Case5NewNoteName + "» (instance); 11–12 — ничего не создано.");
        return builder.ToString();
    }
}
