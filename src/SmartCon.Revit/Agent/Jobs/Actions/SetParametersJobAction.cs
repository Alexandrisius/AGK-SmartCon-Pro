using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

public sealed partial class SetParametersJobAction : RevitJobActionBase
{
    private const string ParameterNotFound = "parameter_not_found";
    private const string ReadOnlyParameter = "readonly_parameter";
    private const string InvalidValue = "invalid_value";
    private const string ResolutionHint = "[Action: check the parameter name/GUID and the scope, then run the preview again]";


    public override string Name => "set_parameters";
    public override JobActionScope Scope => JobActionScope.ProjectInstance;
    public override int MaxUnits => 5000;

    public override JobExpansion Expand(JobActionContext context, string specJson)
    {
        var keyError = JobSpecKeyValidator.Validate(specJson, SpecRules);
        if (keyError is not null)
            return ExpansionFail(JobErrorCodes.InvalidSpec, keyError);

        var spec = Parse<SetParametersSpec>(specJson);
        var specError = ValidateSpec(spec);
        if (specError is not null)
            return ExpansionFail(JobErrorCodes.InvalidSpec, specError);
        var select = spec!.Select!;
        if (!TryResolveCategories(context.Document, select, out var categoryIds, out var categoryError))
            return ExpansionFail(JobErrorCodes.InvalidSpec, categoryError!);
        HashSet<long>? systemMemberIds = null;
        if (!string.IsNullOrWhiteSpace(select.SystemName))
        {
            if (!TrySelectSystemMembers(
                    context.Document, select, context.CancellationToken, out systemMemberIds, out var systemError))
                return ExpansionFail("system_not_found", systemError!);
        }
        var limit = select.Limit ?? int.MaxValue;
        var cap = Math.Min(limit, MaxUnits);
        var warnings = new List<string>();
        var elements = Select(context, select, categoryIds, systemMemberIds, cap + 1, warnings);
        if (select.IncludeSubComponents == true)
            ExpandWithSubComponents(context.Document, elements, cap + 1, warnings, context.CancellationToken);
        if (elements.Count > cap)
        {
            if (limit > MaxUnits)
                return ExpansionFail(JobErrorCodes.LimitExceeded, "The selector matches more than " + MaxUnits +
                    " elements; narrow select.* or set limit");
            elements.RemoveRange(limit, elements.Count - limit);
            warnings.Add("select.limit truncated the selection to " + limit + " element(s)");
        }
        if (elements.Count == 0)
            return ExpansionFail(JobErrorCodes.SelectorEmpty,
                "The selector matched no elements; check the ids, level ids, category names and the " +
                "system/condition filters");
        elements.Sort((left, right) => left.Id.GetValue().CompareTo(right.Id.GetValue()));
        var scope = string.Equals(spec.Scope, "type", StringComparison.OrdinalIgnoreCase) ? "type" : "instance";
        var parameterKey = ParameterKey(spec);
        var resolveName = string.IsNullOrWhiteSpace(spec.BuiltInParameter)
            ? spec.Parameter
            : spec.BuiltInParameter;
        var units = new List<JobUnit>(elements.Count);
        var missing = 0;
        foreach (var element in elements)
        {
            var resolution = AgentParameterWriteHelper.Resolve(
                context.Document, element, resolveName, spec.SharedParameterGuid, scope);
            if (!resolution.Ok)
                missing++;
            units.Add(new JobUnit
            {
                UnitKey = element.UniqueId + "|" + parameterKey + "|" + scope,
                Label = scope + " " + (resolution.Parameter?.Definition?.Name ?? parameterKey) + " = " +
                        TargetText(spec.Value, spec.Unit),
                ArgsJson = Json(new SetParameterUnitArgs
                {
                    ElementId = element.Id.GetValue(),
                    UniqueId = element.UniqueId,
                    OwnerUniqueId = resolution.Ok && resolution.IsTypeScope
                        ? resolution.Parameter!.Element?.UniqueId
                        : null,
                    Parameter = spec.Parameter,
                    SharedParameterGuid = spec.SharedParameterGuid,
                    BuiltInParameter = spec.BuiltInParameter,
                    Scope = scope,
                    Value = spec.Value,
                    Unit = spec.Unit,
                    ErrorCode = resolution.Ok ? null : resolution.ErrorCode,
                    Error = resolution.Ok ? null : resolution.Reason + " " + ResolutionHint
                })
            });
        }
        if (missing > 0)
            warnings.Add(missing + " selected element(s) do not resolve parameter '" + parameterKey +
                         "' for scope " + scope + "; those units fail in the preview");
        if (string.Equals(scope, "type", StringComparison.Ordinal) && units.Count > missing)
            warnings.Add("scope=type: each write changes the type element, so every instance of that type changes");
        return new JobExpansion { Units = units, Warnings = warnings };
    }

    public override JobUnitPreview Preview(JobActionContext context, JobUnit unit)
    {
        if (!TryPrepare(context, unit, out var args, out var parameter, out var code, out var detail))
            return Fail(code, detail);
        var name = NameOf(parameter, args);
        if (parameter.IsReadOnly)
            return Fail(ReadOnlyParameter, "Parameter '" + name + "' is read-only " + Hint("choose a writable parameter"));
        if (!TryNormalizeTarget(parameter, args, out var expectedRaw, out var invalidReason))
            return Fail(InvalidValue, invalidReason);
        var currentRaw = AgentParameterWriteHelper.RawValue(parameter);
        return new JobUnitPreview
        {
            Skipped = RawMatches(parameter.StorageType, currentRaw, expectedRaw),
            ExpectedJson = Value(expectedRaw),
            ActualJson = Value(currentRaw),
            UndoJson = Value(currentRaw),
            Label = name + ": " + AgentParameterWriteHelper.Describe(parameter) +
                    " → " + TargetText(args.Value, args.Unit)
        };
    }

    public override JobUnitApply Apply(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview,
        int attempt,
        Action<string>? persistUndo)
    {
        if (!TryPrepare(context, unit, out var args, out var parameter, out var code, out var detail))
            return ApplyFail(code, detail);
        var expectedRaw = ReadValue(preview.ExpectedJson);
        if (expectedRaw is null)
        {
            if (!TryNormalizeTarget(parameter, args, out var normalized, out var invalidReason))
                return ApplyFail(InvalidValue, invalidReason);
            expectedRaw = normalized;
        }
        var currentRaw = AgentParameterWriteHelper.RawValue(parameter);
        if (RawMatches(parameter.StorageType, currentRaw, expectedRaw))
        {
            return new JobUnitApply
            {
                Success = true,
                Skipped = true,
                ExpectedJson = Value(expectedRaw),
                UndoJson = string.IsNullOrWhiteSpace(preview.UndoJson) ? Value(currentRaw) : preview.UndoJson
            };
        }

        var previewActual = ReadValue(preview.ActualJson);
        if (previewActual is not null && !RawMatches(parameter.StorageType, currentRaw, previewActual))
        {
            return ApplyFail(
                JobErrorCodes.PreviewStale,
                JobErrors.PreviewStale(unit.UnitKey).ToMessage() +
                " (the parameter now reads '" + currentRaw + "')");
        }

        var undoJson = string.IsNullOrWhiteSpace(preview.UndoJson) ? Value(currentRaw) : preview.UndoJson!;
        persistUndo?.Invoke(undoJson);
        try
        {
            if (!AgentParameterWriteHelper.TrySet(
                    parameter, args.Value, args.Unit, out var errorCode, out var reason))
            {
                return ApplyFail(errorCode, reason + " " + Hint("fix the value/unit and run the preview again"));
            }
        }
        catch (Exception ex)
        {
            return ApplyFail("set_failed", "Writing parameter '" + NameOf(parameter, args) + "' failed: " +
                ex.GetType().Name + ": " + ex.Message + " " + Hint("check that the parameter accepts this value"));
        }
        return new JobUnitApply { Success = true, ExpectedJson = Value(expectedRaw), UndoJson = undoJson };
    }

    private static bool TryNormalizeTarget(
        Parameter parameter,
        SetParameterUnitArgs args,
        out string expectedRaw,
        out string reason)
    {
        expectedRaw = string.Empty;
        reason = string.Empty;
        var text = args.Value ?? string.Empty;
        var name = parameter.Definition?.Name ?? "?";
        switch (parameter.StorageType)
        {
            case StorageType.String:
                expectedRaw = text;
                return true;
            case StorageType.Integer:
                if (bool.TryParse(text, out var flag))
                    text = flag ? "1" : "0";
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    reason = "'" + text + "' is not a valid integer for parameter '" + name + "' " +
                             Hint("pass an integer or true/false");
                    return false;
                }
                expectedRaw = integer.ToString(CultureInfo.InvariantCulture);
                return true;
            case StorageType.Double:
                return TryNormalizeDouble(text, args.Unit, name, parameter, out expectedRaw, out reason);
            case StorageType.ElementId:
                if (string.IsNullOrWhiteSpace(text) ||
                    string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
                {
                    expectedRaw = "-1";
                    return true;
                }
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    reason = "'" + text + "' is not a valid element id for parameter '" + name + "' " +
                             Hint("pass a numeric element id, none, or an empty string");
                    return false;
                }
                expectedRaw = id.ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                reason = "Parameter '" + name + "' has storage type " + parameter.StorageType + " and cannot be written " +
                         Hint("choose a writable parameter type");
                return false;
        }
    }

    private static bool TryNormalizeDouble(
        string text,
        string? unit,
        string name,
        Parameter parameter,
        out string expectedRaw,
        out string reason)
    {
        expectedRaw = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(unit))
        {
            reason = "Numeric value '" + text + "' for parameter '" + name + "' needs an explicit unit " +
                     Hint("pass mm, cm, m, ft, m2, m3, or raw for a dimensionless parameter");
            return false;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            reason = "'" + text + "' is not a valid number for parameter '" + name + "' " + Hint("pass a number");
            return false;
        }
        var normalized = unit!.Trim().ToLowerInvariant();
        var unitType = normalized switch
        {
            "mm" or "millimeter" or "millimeters" => UnitTypeId.Millimeters,
            "cm" => UnitTypeId.Centimeters,
            "m" => UnitTypeId.Meters,
            "ft" or "foot" or "feet" => UnitTypeId.Feet,
            "in" or "inch" or "inches" => UnitTypeId.Inches,
            "m2" or "sqm" => UnitTypeId.SquareMeters,
            "m3" => UnitTypeId.CubicMeters,
            _ => null
        };
        if (unitType is null && normalized is not ("raw" or "internal"))
        {
            reason = "Unit '" + unit + "' is not supported for parameter '" + name + "' " +
                     Hint("pass mm, cm, m, ft, in, m2, m3 or raw");
            return false;
        }

        // A unit of the wrong dimension silently wrote a meaningless value (m2 for a
        // length); validate against the parameter spec before converting.
        if (unitType is not null)
        {
            ForgeTypeId? spec = null;
            try
            {
                spec = parameter.Definition?.GetDataType();
            }
            catch (Exception)
            {
                // No spec: the dimension cannot be checked.
            }

            if (spec is not null && !UnitUtils.IsMeasurableSpec(spec))
            {
                reason = "Parameter '" + name + "' is dimensionless; omit unit or pass unit=\"raw\".";
                return false;
            }

            if (spec is not null && !UnitUtils.IsValidUnit(spec, unitType))
            {
                reason = "Unit '" + unit + "' does not match parameter '" + name +
                         "' (spec " + spec.TypeId + ").";
                return false;
            }
        }

        var raw = unitType is null ? number : UnitUtils.ConvertToInternalUnits(number, unitType);
        expectedRaw = raw.ToString("R", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool RawMatches(StorageType storageType, string actual, string? expected)
    {
        if (expected is null)
            return false;
        if (storageType != StorageType.Double)
            return string.Equals(actual, expected, StringComparison.Ordinal);
        if (!double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualNumber) ||
            !double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumber))
            return false;
        return !double.IsNaN(actualNumber) && !double.IsNaN(expectedNumber) &&
               Math.Abs(actualNumber - expectedNumber) <= Math.Max(1e-12, Math.Abs(expectedNumber) * 1e-9);
    }

}
