namespace SmartCon.Core.Logging;

/// <summary>Log severity level for SmartCon diagnostics.</summary>
public enum LogLevel
{
    /// <summary>Verbose diagnostic output (disabled in production).</summary>
    Debug,

    /// <summary>General informational messages.</summary>
    Info,

    /// <summary>Non-critical issues that deserve attention.</summary>
    Warn,

    /// <summary>Critical errors.</summary>
    Error
}
