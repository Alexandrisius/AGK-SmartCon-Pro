namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Универсальный AST-парсер и решатель формул Revit (ADR-005).
/// Поддерживает: if(), and(), or(), арифметику, сравнения, тригонометрию.
/// Реализация: SmartCon.Core/Services/Implementation/FormulaSolver.cs
/// </summary>
public interface IFormulaSolver
{
    /// <summary>
    /// Прямое вычисление формулы при заданных параметрах (Internal Units).
    /// </summary>
    double Evaluate(string formula, IReadOnlyDictionary<string, double> parameterValues);

    /// <summary>
    /// Обратное решение: найти значение variableName при котором formula = targetValue.
    /// Линейные — алгебраическая инверсия. Сложные (if, trig) — бисекция/Ньютон.
    /// </summary>
    double SolveFor(string formula, string variableName, double targetValue,
                    IReadOnlyDictionary<string, double> otherValues);

    /// <summary>
    /// Парсинг size_lookup(...) — извлечение имени таблицы и порядка параметров.
    /// </summary>
    (string TableName, IReadOnlyList<string> ParameterOrder) ParseSizeLookup(string formula);
}
