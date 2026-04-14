using System;
using System.IO;
using System.Linq;

namespace SmartCon.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public static class SmartConLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon");

    private static readonly string LogPath = Path.Combine(LogDir, "smartcon.log");
    private static readonly string LookupLogPath = Path.Combine(LogDir, "lookup-diagnostic.log");
    private static readonly string FormulaLogPath = Path.Combine(LogDir, "formula-diagnostic.log");

    private static readonly object _lock = new();

    private const long MaxLogSizeBytes = 5 * 1024 * 1024;
    private const int MaxBakFiles = 3;

    public static LogLevel MinLevel { get; set; } = LogLevel.Debug;

    static SmartConLogger()
    {
        try { Directory.CreateDirectory(LogDir); }
        catch { }
    }

    public static void Info(string message) => Write(LogPath, "INF", message);
    public static void Debug(string message)
    {
        if (MinLevel <= LogLevel.Debug) Write(LogPath, "DBG", message);
    }
    public static void DebugSection(string title)
    {
        if (MinLevel <= LogLevel.Debug) Write(LogPath, "DBG", $"── {title} ──");
    }
    public static void DebugLines(string header, string[] lines, int maxLines = 20)
    {
        if (MinLevel > LogLevel.Debug) return;
        Write(LogPath, "DBG", $"{header} ({lines.Length} lines):");
        int count = System.Math.Min(lines.Length, maxLines);
        for (int i = 0; i < count; i++)
            Write(LogPath, "CSV", $"  [{i}] {lines[i]}");
        if (lines.Length > maxLines)
            Write(LogPath, "CSV", $"  ... ({lines.Length - maxLines} more lines hidden)");
    }
    public static void Warn(string message) => Write(LogPath, "WRN", message);
    public static void Error(string message) => Write(LogPath, "ERR", message);

    public static void LogSessionStart(string commandName)
    {
        RotateLogIfNeeded();
        CleanupOldBakFiles();

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

    public static void Lookup(string message)
    {
        Write(LookupLogPath, "LKP", message);
    }

    public static void LookupSection(string title)
    {
        var line = $"── {title} ──";
        Write(LookupLogPath, "LKP", line);
    }

    public static void LookupLines(string header, string[] lines, int maxLines = 20)
    {
        Write(LookupLogPath, "LKP", $"{header} ({lines.Length} lines):");
        int count = System.Math.Min(lines.Length, maxLines);
        for (int i = 0; i < count; i++)
            Write(LookupLogPath, "CSV", $"  [{i}] {lines[i]}");
        if (lines.Length > maxLines)
            Write(LookupLogPath, "CSV", $"  ... ({lines.Length - maxLines} more lines hidden)");
    }

    public static void Formula(string message)
    {
        Write(FormulaLogPath, "FRM", message);
    }

    public static void FormulaOk(string operation, string formula, string detail)
    {
        Write(FormulaLogPath, " OK", $"[{operation}] '{formula}' → {detail}");
    }

    public static void FormulaFail(string operation, string formula, string reason)
    {
        Write(FormulaLogPath, "FAIL", $"[{operation}] '{formula}' → {reason}");
    }

    private static void RotateLogIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var fi = new FileInfo(LogPath);
            if (fi.Length < MaxLogSizeBytes) return;

            var bakPath = Path.Combine(LogDir, "smartcon.log.bak");
            File.Delete(bakPath);
            File.Move(LogPath, bakPath);
        }
        catch { }
    }

    private static void CleanupOldBakFiles()
    {
        try
        {
            var bakFiles = Directory.GetFiles(LogDir, "*.bak")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(MaxBakFiles)
                .ToList();

            foreach (var f in bakFiles)
            {
                try { f.Delete(); }
                catch { }
            }
        }
        catch { }
    }

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
        catch { }
    }
}
