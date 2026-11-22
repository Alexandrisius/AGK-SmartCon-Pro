using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Classification;

/// <summary>
/// Checks a classification code against the table loaded in the document before
/// it is written. Revit itself accepts any string there and silently blanks the
/// assembly description of an unknown code, so the check lives in the write path:
/// a code that cannot be verified is refused unless the caller passes the explicit
/// opt-in — the model then has to tell the user instead of guessing.
/// </summary>
internal static class AgentClassificationGuard
{
    internal const string TableNotLoaded = "classification_table_not_loaded";
    internal const string CodeNotFound = "classification_code_not_found";

    internal sealed class Verdict
    {
        internal bool Ok { get; init; }

        internal string? ErrorCode { get; init; }

        internal string? Reason { get; init; }

        internal IReadOnlyList<AgentClassificationEntry> Suggestions { get; init; } =
            Array.Empty<AgentClassificationEntry>();

        /// <summary>The table row of the code; null when the code went through unverified.</summary>
        internal AgentClassificationEntry? Entry { get; init; }

        /// <summary>
        /// True when the code goes through unverified: the opt-in overrode a
        /// refusal. Clearing the code and non-classification parameters are not
        /// "unverified" — nothing needed a loaded row behind them.
        /// </summary>
        internal bool Unverified => Ok && ErrorCode is not null;
    }

    internal static bool IsAssemblyCode(Parameter parameter) =>
        parameter.Definition is InternalDefinition { BuiltInParameter: { } builtIn }
        && builtIn == AgentParameterResolver.AssemblyCodeBuiltIn;

    /// <summary>Full check: reads the document table once per call.</summary>
    internal static Verdict Check(
        Document doc,
        Parameter parameter,
        string? value,
        bool allowUnverified) =>
        !IsAssemblyCode(parameter)
        ? new Verdict { Ok = true }
        : Check(AgentClassificationReader.Read(doc), value, allowUnverified);

    /// <summary>Check against a table that is already read (batch callers).</summary>
    internal static Verdict Check(
        AgentClassificationTable table,
        string? value,
        bool allowUnverified)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Verdict { Ok = true };

        var code = value!.Trim();
        var entry = table.Find(code);
        if (entry is not null)
            return new Verdict { Ok = true, Entry = entry };

        if (!table.Loaded)
        {
            const string reason =
                "No classification table is loaded in this document, so the code cannot be " +
                "verified. Tell the user that the classifier is not loaded (Revit: Manage > " +
                "Classification Settings; older versions: Manage > Additional Settings > " +
                "Assembly Code) and offer to load a classifier file with load_classification_table. ";
            return new Verdict
            {
                Ok = allowUnverified,
                ErrorCode = TableNotLoaded,
                Reason = reason + OptIn(code)
            };
        }

        var suggestions = table.Suggest(code);
        return new Verdict
        {
            Ok = allowUnverified,
            ErrorCode = CodeNotFound,
            Suggestions = suggestions,
            Reason = "Code '" + code + "' is not in the classification table loaded in this " +
                     "document" +
                     (suggestions.Count > 0
                         ? "; closest: " + string.Join(
                             ", ", suggestions.Select(item => item.Code + " " + item.Breadcrumb))
                         : string.Empty) +
                     ". Look the code up with list_classification_entries. " + OptIn(code)
        };
    }

    private static string OptIn(string code) =>
        "Pass allowUnverifiedClassification=true only when the user explicitly dictated '" +
        code + "'.";
}
