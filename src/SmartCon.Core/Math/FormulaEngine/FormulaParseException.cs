namespace SmartCon.Core.Math.FormulaEngine;

/// <summary>
/// Исключение при ошибке парсинга формулы Revit.
/// </summary>
public sealed class FormulaParseException : Exception
{
    public FormulaParseException(string message) : base(message) { }
    public FormulaParseException(string message, Exception inner) : base(message, inner) { }
}
