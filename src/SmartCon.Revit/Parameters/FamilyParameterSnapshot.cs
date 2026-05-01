using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Parameters;

/// <summary>
/// Pre-cached snapshot of FamilyManager parameters and their formulas.
/// Must be built BEFORE calling FamilyParameterAnalyzer to avoid COM collection corruption
/// when iterating AssociatedParameters.
/// </summary>
internal sealed class FamilyParameterSnapshot
{
    /// <summary>Ordered list of (Name, Formula) for all family parameters.</summary>
    public IReadOnlyList<(string? Name, string? Formula)> Parameters { get; }

    /// <summary>Dictionary of parameter name → formula string (case-insensitive keys).</summary>
    public IReadOnlyDictionary<string, string> FormulaByName { get; }

    private FamilyParameterSnapshot(
        IReadOnlyList<(string? Name, string? Formula)> parameters,
        IReadOnlyDictionary<string, string> formulaByName)
    {
        Parameters = parameters;
        FormulaByName = formulaByName;
    }

    /// <summary>
    /// Build a snapshot from the given <see cref="FamilyManager"/>.
    /// Safe to call once — the resulting snapshot can be reused without COM issues.
    /// </summary>
    public static FamilyParameterSnapshot Build(Autodesk.Revit.DB.FamilyManager fm)
    {
        var paramList = new List<(string? Name, string? Formula)>();
        var formulaDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var currentType = fm.CurrentType;

        foreach (FamilyParameter fp in fm.Parameters)
        {
            var name = fp.Definition?.Name;
            var formula = fp.Formula;
            paramList.Add((name, formula));
            if (name is not null)
            {
                if (!string.IsNullOrEmpty(formula))
                {
                    formulaDict.TryAdd(name, formula);
                }
                else if (fp.StorageType == StorageType.String && currentType is not null)
                {
                    var strValue = currentType.AsString(fp);
                    if (!string.IsNullOrEmpty(strValue))
                        formulaDict.TryAdd(name, "\"" + strValue + "\"");
                }
            }
        }

        return new FamilyParameterSnapshot(paramList, formulaDict);
    }
}
