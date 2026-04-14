namespace SmartCon.Core.Math.FormulaEngine;

/// <summary>
/// Exception thrown when Revit formula parsing fails.
/// </summary>
public sealed class FormulaParseException : Exception
{
    public FormulaParseException(string message) : base(message) { }
    public FormulaParseException(string message, Exception inner) : base(message, inner) { }
}
