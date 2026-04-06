using System.Text.RegularExpressions;

namespace SmartCon.Core.Math.FormulaEngine;

/// <summary>
/// Удаляет единицы измерения из строки формулы Revit перед токенизацией.
/// Revit API возвращает формулы в Internal Units (decimal feet),
/// единицы — декоративные суффиксы (mm, m, ft, in, мм, см, м и т.д.).
/// </summary>
internal static class UnitStripper
{
    // Паттерн: число (целое/дробное) + пробел? + единица измерения + граница слова.
    // Порядок: длинные суффиксы первыми, чтобы "mm" не поглотило часть "мм".
    // Группа 1 = число, группа 2 = единица — оставляем только число.
    private static readonly Regex UnitSuffixRegex = new(
        @"(\d+\.?\d*)\s*"
        + @"(м²|м³|мм|см|дм|mm|cm|ft|in|м)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Revit-паттерн конвертации единиц: "/ 1)" или "* 1)" — убираем
    private static readonly Regex UnitConversionRegex = new(
        @"[*/]\s*1\s*\)",
        RegexOptions.Compiled);

    internal static string Strip(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return formula ?? string.Empty;

        // 1. Убрать суффиксы единиц после чисел
        var result = UnitSuffixRegex.Replace(formula, "$1");

        // 2. Убрать паттерны конвертации типа "/ 1)", но сохранить закрывающую скобку
        result = UnitConversionRegex.Replace(result, ")");

        return result;
    }
}
