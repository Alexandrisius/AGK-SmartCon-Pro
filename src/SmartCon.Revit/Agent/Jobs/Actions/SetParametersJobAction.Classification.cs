using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Classification;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Classification half of set_parameters: an Assembly Code is only written when
/// a row of the classification table loaded in the document backs it, unless the
/// spec opts in. The runner hands out one context per slice, so the table and
/// the verdict of the job's single value are read once per slice instead of per
/// element; an apply slice gets a context of its own and re-checks instead of
/// trusting the preview.
/// </summary>
public sealed partial class SetParametersJobAction
{
    private static readonly AgentClassificationGuard.Verdict NotAClassification = new() { Ok = true };

    // Weak on purpose: the context owns the document, which a cache must not
    // keep alive past its slice.
    private WeakReference<JobActionContext>? _classificationSlice;
    private AgentClassificationTable? _classificationTable;
    private string? _verdictValue;
    private bool _verdictAllowUnverified;
    private AgentClassificationGuard.Verdict? _verdict;

    private AgentClassificationGuard.Verdict CheckClassification(
        JobActionContext context,
        Parameter parameter,
        SetParameterUnitArgs args)
    {
        if (!AgentClassificationGuard.IsAssemblyCode(parameter))
            return NotAClassification;

        if (_classificationSlice is null
            || !_classificationSlice.TryGetTarget(out var slice)
            || !ReferenceEquals(slice, context))
        {
            _classificationSlice = new WeakReference<JobActionContext>(context);
            _classificationTable = null;
            _verdict = null;
        }

        var allowUnverified = args.AllowUnverifiedClassification ?? false;
        if (_verdict is null
            || !string.Equals(_verdictValue, args.Value, StringComparison.Ordinal)
            || _verdictAllowUnverified != allowUnverified)
        {
            _classificationTable ??= AgentClassificationReader.Read(context.Document);
            _verdictValue = args.Value;
            _verdictAllowUnverified = allowUnverified;
            _verdict = AgentClassificationGuard.Check(_classificationTable, args.Value, allowUnverified);
        }

        return _verdict;
    }

    private static string UnverifiedWarning(AgentClassificationGuard.Verdict verdict, string? value) =>
        "Classification code '" + (value ?? string.Empty).Trim() + "' is written unverified (" +
        verdict.ErrorCode + "): no row of a classification table loaded in the document backs it — " +
        "confirm the code with the user.";
}
