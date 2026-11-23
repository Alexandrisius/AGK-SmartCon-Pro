namespace SmartCon.Core.Services.Agent;

/// <summary>
/// The optional top-level <c>documentId</c> argument (background-model routing,
/// case 9 v2 multi-model reads). It is a harness-wide convention: a tool either
/// declares it in its schema or does not read documents by it at all - there is
/// no tolerant middle. The tolerant repair must not delete an undeclared
/// documentId: the call was aimed at a background donor and has to fail loudly
/// instead of silently running against the ACTIVE document.
/// </summary>
internal static class AgentDocumentRoutingArgument
{
    /// <summary>Argument name every routed read tool declares in <c>properties</c>.</summary>
    internal const string Name = "documentId";

    /// <summary>
    /// Validator text for a call that carries the argument while the tool's
    /// schema does not declare it. Says "does not accept", not "does not
    /// read": write, job and core tools do not read documents at all, but the
    /// contract is the same for every tool (review r1, P3).
    /// </summary>
    internal const string UndeclaredError =
        "unknown property - this tool does not accept documentId; omit it";

    internal static bool IsName(string memberName) =>
        string.Equals(memberName, Name, StringComparison.Ordinal);
}
