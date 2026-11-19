using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>
/// Base class for Revit-backed job actions: common JSON helpers and typed
/// failures. Actions never decide "done" on their own — the runner read-backs
/// through <see cref="Verify"/>.
/// </summary>
public abstract class RevitJobActionBase : IJobAction
{
    public abstract string Name { get; }

    public abstract JobActionScope Scope { get; }

    public virtual int MaxUnits => 1000;

    public abstract JobExpansion Expand(JobActionContext context, string specJson);

    public abstract JobUnitPreview Preview(JobActionContext context, JobUnit unit);

    public abstract JobUnitApply Apply(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview,
        int attempt,
        Action<string>? persistUndo);

    public abstract JobUnitVerification Verify(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview);

    public virtual JobUnitUndo Undo(
        JobActionContext context,
        JobUnit unit,
        string? undoJson,
        bool dryRun) =>
        new()
        {
            ErrorCode = JobErrorCodes.RollbackUnavailable,
            ErrorDetail = "Action '" + Name + "' does not support per-unit rollback"
        };

    protected static T? Parse<T>(string? json) where T : class => JobJson.Deserialize<T>(json);

    protected static string Json<T>(T value) => JobJson.Serialize(value);

    /// <summary>Serializes a raw string as a JSON string literal (the expected/actual contract).</summary>
    protected static string Value(string? text) => JobJson.Serialize(text ?? string.Empty);

    /// <summary>Reads a value previously written with <see cref="Value"/>.</summary>
    protected static string? ReadValue(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    protected static JobUnitPreview Fail(string code, string detail) => new()
    {
        ErrorCode = code,
        ErrorDetail = detail
    };

    protected static JobExpansion ExpansionFail(string code, string detail) => new()
    {
        ErrorCode = code,
        ErrorDetail = detail
    };

    protected static bool TryFindByUniqueId<T>(
        Document document,
        string uniqueId,
        out T? element,
        out JobError? error) where T : Element
    {
        element = null;
        error = null;

        Element? raw;
        try
        {
            raw = document.GetElement(uniqueId);
        }
        catch (Exception ex)
        {
            error = new JobError(
                JobErrorCodes.ItemFailed,
                "Element '" + uniqueId + "' could not be resolved: " + ex.Message,
                "the element was deleted or the document changed; run the preview again");
            return false;
        }

        if (raw is null)
        {
            error = new JobError(
                JobErrorCodes.ItemFailed,
                "Element '" + uniqueId + "' no longer exists",
                "the element was deleted after the preview; run the preview again");
            return false;
        }

        if (raw is not T typed)
        {
            error = new JobError(
                JobErrorCodes.ItemFailed,
                "Element '" + uniqueId + "' is " + raw.GetType().Name + ", not " + typeof(T).Name,
                "run the preview again with the correct selector");
            return false;
        }

        element = typed;
        return true;
    }
}