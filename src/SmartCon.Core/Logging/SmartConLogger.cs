using System;
using System.IO;

namespace SmartCon.Core.Logging;

/// <summary>
/// File logger for SmartCon.
/// Writes to %APPDATA%\AGK\SmartCon\smartcon.log (general) and
/// %APPDATA%\AGK\SmartCon\lookup-diagnostic.log (LookupTable/ParameterResolver only).
/// Thread-safe via lock. Silently ignores write errors.
/// Accessible from all assemblies (SmartCon.Core, SmartCon.Revit, SmartCon.PipeConnect, SmartCon.App).
/// </summary>
public static class SmartConLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon");

    private static readonly string LogPath = Path.Combine(LogDir, "smartcon.log");
    private static readonly string LookupLogPath = Path.Combine(LogDir, "lookup-diagnostic.log");
    private static readonly string FormulaLogPath = Path.Combine(LogDir, "formula-diagnostic.log");

    private static readonly object _lock = new();

    static SmartConLogger()
    {
        try { Directory.CreateDirectory(LogDir); }
        catch { /* if folder creation fails — just don't write */ }
    }

    // ── Main log ──────────────────────────────────────────────────────

    public static void Info(string message) => Write(LogPath, "INF", message);
    public static void Debug(string message) => Write(LogPath, "DBG", message);
    public static void DebugSection(string title) => Write(LogPath, "DBG", $"── {title} ──");
    public static void DebugLines(string header, string[] lines, int maxLines = 20)
    {
        Write(LogPath, "DBG", $"{header} ({lines.Length} lines):");
        int count = System.Math.Min(lines.Length, maxLines);
        for (int i = 0; i < count; i++)
            Write(LogPath, "CSV", $"  [{i}] {lines[i]}");
        if (lines.Length > maxLines)
            Write(LogPath, "CSV", $"  ... ({lines.Length - maxLines} more lines hidden)");
    }
    public static void Warn(string message) => Write(LogPath, "WRN", message);
    public static void Error(string message) => Write(LogPath, "ERR", message);

    /// <summary>Session separator at command start — writes to all log files.</summary>
    public static void LogSessionStart(string commandName)
    {
        var header = $"{'=',70}";
        var line = $"SESSION START: {commandName}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]";
        Write(LogPath, "INF", header);
        Write(LogPath, "INF", line);
        Write(LogPath, "INF", header);
        Write(LookupLogPath, "INF", header);
        Write(LookupLogPath, "INF", line);
        Write(LookupLogPath, "INF", header);
        Write(FormulaLogPath, "INF", header);
        Write(FormulaLogPath, "INF", line);
        Write(FormulaLogPath, "INF", header);
    }

    // ── LookupTable / ParameterResolver diagnostics ───────────────────────

    /// <summary>
    /// Writes ONLY to lookup-diagnostic.log (not to smartcon.log).
    /// Used in RevitLookupTableService, RevitParameterResolver, FamilyParameterAnalyzer.
    /// </summary>
    public static void Lookup(string message)
    {
        Write(LookupLogPath, "LKP", message);
    }

    /// <summary>Section separator within LookupTable diagnostics.</summary>
    public static void LookupSection(string title)
    {
        var line = $"── {title} ──";
        Write(LookupLogPath, "LKP", line);
    }

    /// <summary>Write an array of strings (e.g. CSV rows) to lookup-diagnostic.log.</summary>
    public static void LookupLines(string header, string[] lines, int maxLines = 20)
    {
        Write(LookupLogPath, "LKP", $"{header} ({lines.Length} lines):");
        int count = System.Math.Min(lines.Length, maxLines);
        for (int i = 0; i < count; i++)
            Write(LookupLogPath, "CSV", $"  [{i}] {lines[i]}");
        if (lines.Length > maxLines)
            Write(LookupLogPath, "CSV", $"  ... ({lines.Length - maxLines} more lines hidden)");
    }

    // ── Formula diagnostics ───────────────────────────────────────────────

    /// <summary>
    /// Writes ONLY to formula-diagnostic.log.
    /// Used by FormulaSolver to track all formula operations:
    /// which succeeded, which failed, with what parameters.
    /// Builds a knowledge base of all encountered formulas for future edits.
    /// </summary>
    public static void Formula(string message)
    {
        Write(FormulaLogPath, "FRM", message);
    }

    /// <summary>Log a successful formula operation.</summary>
    public static void FormulaOk(string operation, string formula, string detail)
    {
        Write(FormulaLogPath, " OK", $"[{operation}] '{formula}' → {detail}");
    }

    /// <summary>Log a failed formula operation.</summary>
    public static void FormulaFail(string operation, string formula, string reason)
    {
        Write(FormulaLogPath, "FAIL", $"[{operation}] '{formula}' → {reason}");
    }

    // ── Internal write ─────────────────────────────────────────────────

    private static void Write(string path, string level, string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {message}{Environment.NewLine}");
            }
        }
        catch { /* log write must not break main logic */ }
    }
}
