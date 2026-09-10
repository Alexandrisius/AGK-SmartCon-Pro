using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilyTypeCatalogBaker
{
    private static Dictionary<string, FamilyParameter> BuildParameterMap(Autodesk.Revit.DB.FamilyManager fm)
    {
        var map = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (FamilyParameter param in fm.Parameters)
        {
            if (param.Definition?.Name is string name)
            {
                map[name] = param;
            }
        }

        return map;
    }

    private List<FormulaParameterState> CollectFormulaState(
        Autodesk.Revit.DB.FamilyManager fm,
        Dictionary<string, FamilyParameter> paramMap,
        HashSet<string> catalogParamNames)
    {
        var states = new List<FormulaParameterState>();
        foreach (FamilyParameter param in fm.Parameters)
        {
            if (param.Definition?.Name is not string name) continue;
            if (string.IsNullOrWhiteSpace(param.Formula)) continue;
            if (!param.CanAssignFormula) continue;

            var variables = _formulaSolver.ExtractVariables(param.Formula);
            if (!variables.Any(v => catalogParamNames.Contains(v)))
            {
                continue;
            }

            states.Add(new FormulaParameterState(
                param,
                name,
                param.Formula,
                variables));
        }

        if (states.Count == 0)
        {
            SmartConLogger.Warn(
                $"Type Catalog bake: no formula-driven parameters reference catalog names " +
                $"[Action: verify that .txt column names match the variable names used in formulas]");
        }

        return states;
    }

    private static void EnsureCurrentType(Autodesk.Revit.DB.FamilyManager fm)
    {
        if (fm.CurrentType is not null)
        {
            return;
        }

        if (fm.Types.Size > 0)
        {
            foreach (FamilyType existing in fm.Types)
            {
                fm.CurrentType = existing;
                return;
            }
        }

        fm.CurrentType = fm.NewType(AnchorTypeName);
    }

    private static void RemoveOtherTypes(Autodesk.Revit.DB.FamilyManager fm, FamilyType anchor)
    {
        var typesToDelete = new List<FamilyType>();
        foreach (FamilyType familyType in fm.Types)
        {
            if (!string.Equals(familyType.Name, anchor.Name, StringComparison.Ordinal))
            {
                typesToDelete.Add(familyType);
            }
        }

        foreach (var type in typesToDelete)
        {
            try
            {
                fm.CurrentType = type;
                fm.DeleteCurrentType();
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Failed to delete existing type '{type.Name}': {ex.Message} " +
                    "[Action: continue baking, this type may be inherited from the template]");
            }
        }
    }

    private static void RemoveAnchorType(Autodesk.Revit.DB.FamilyManager fm)
    {
        var anchor = fm.Types.Cast<FamilyType>()
            .FirstOrDefault(t => string.Equals(t.Name, AnchorTypeName, StringComparison.Ordinal));

        if (anchor is null)
        {
            return;
        }

        if (fm.Types.Size == 1)
        {
            fm.CurrentType = anchor;
            fm.RenameCurrentType("DefaultType");
            return;
        }

        fm.CurrentType = anchor;
        fm.DeleteCurrentType();
    }

    private static void DisableFormulas(Autodesk.Revit.DB.FamilyManager fm, List<FormulaParameterState> states)
    {
        var currentType = fm.CurrentType ?? throw new InvalidOperationException("No current family type");

        foreach (var state in states)
        {
            try
            {
                var valueBefore = ReadParameterValue(currentType, state.Parameter);
                SmartConLogger.Debug(
                    $"[FormulaDisable] param='{state.ParameterName}' formula='{state.Formula}' " +
                    $"valueBefore={FormatValue(valueBefore)}");

                fm.SetFormula(state.Parameter, null);
                var valueAfterFormulaRemove = ReadParameterValue(currentType, state.Parameter);
                SmartConLogger.Debug(
                    $"[FormulaDisable] param='{state.ParameterName}' after SetFormula(null) " +
                    $"valueAfter={FormatValue(valueAfterFormulaRemove)}");

                if (valueBefore is not null)
                {
                    ApplyTypedValue(fm, state.Parameter, valueBefore);
                    var valueAfterSet = ReadParameterValue(currentType, state.Parameter);
                    SmartConLogger.Debug(
                        $"[FormulaDisable] param='{state.ParameterName}' after Set(valueBefore) " +
                        $"valueAfterSet={FormatValue(valueAfterSet)}");
                }

                SmartConLogger.Debug(
                    $"Disabled formula for '{state.ParameterName}' and preserved its current value");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Failed to disable formula for '{state.ParameterName}': {ex.Message} " +
                    "[Action: bake may fail or produce an invalid family]");
                throw;
            }
        }
    }

    private static void RestoreFormulas(Autodesk.Revit.DB.FamilyManager fm, List<FormulaParameterState> states)
    {
        var ordered = OrderByDependency(states);

        foreach (var state in ordered)
        {
            try
            {
                var valueBefore = ReadParameterValue(fm.CurrentType!, state.Parameter);
                fm.SetFormula(state.Parameter, state.Formula);
                var valueAfter = ReadParameterValue(fm.CurrentType!, state.Parameter);
                SmartConLogger.Debug(
                    $"[FormulaRestore] param='{state.ParameterName}' formula='{state.Formula}' " +
                    $"before={FormatValue(valueBefore)} after={FormatValue(valueAfter)}");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Failed to restore formula for '{state.ParameterName}': {ex.Message} " +
                    "[Action: the baked family may be invalid]");
                throw;
            }
        }
    }

    private static List<FormulaParameterState> OrderByDependency(List<FormulaParameterState> states)
    {
        var stateByName = states.ToDictionary(s => s.ParameterName, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FormulaParameterState>();

        foreach (var state in states)
        {
            Visit(state, stateByName, visited, result);
        }

        return result;
    }

    private static void Visit(
        FormulaParameterState state,
        Dictionary<string, FormulaParameterState> stateByName,
        HashSet<string> visited,
        List<FormulaParameterState> result)
    {
        if (!visited.Add(state.ParameterName))
        {
            return;
        }

        foreach (var variable in state.ReferencedVariables)
        {
            if (stateByName.TryGetValue(variable, out var dependency))
            {
                Visit(dependency, stateByName, visited, result);
            }
        }

        result.Add(state);
    }
}
