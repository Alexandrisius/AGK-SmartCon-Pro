using SmartCon.Core.Logging;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>
/// Scope-per-call logging for the job engine. A method-level scope around a
/// 45-second rollback or a family batch would violate the C15 rule (scope around
/// heavy inner work), so the category scope is opened only at the log site.
/// </summary>
internal static class JobLog
{
    internal static void Warn(string method, string message)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", method));
        SmartConLogger.Warn(message);
    }

    internal static void Error(string method, string message)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", method));
        SmartConLogger.Error(message);
    }

    internal static void Info(string method, string message)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", method));
        SmartConLogger.Info(message);
    }
}
