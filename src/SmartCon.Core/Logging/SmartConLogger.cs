using System.Diagnostics;

namespace SmartCon.Core.Logging;

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

    private static StreamWriter? _mainWriterField;
    private static StreamWriter? _lookupWriterField;
    private static StreamWriter? _formulaWriterField;

    static SmartConLogger()
    {
        try { Directory.CreateDirectory(LogDir); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Init failed: {ex.Message}"); }
    }

    private static StreamWriter CreateWriter(string path)
        => new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        { AutoFlush = true };

       public static void TruncateMainLog()
    {
        lock (_lock)
        {
            if (_mainWriterField != null)
            {
                try
                {
                    _mainWriterField.Flush();
                    _mainWriterField.Dispose();
                }
                catch { }
                _mainWriterField = null;
            }

            try
            {
                if (File.Exists(LogPath))
                    File.WriteAllText(LogPath, string.Empty);
            }
            catch { }
        }
    }

    public static void Info(string message) => WriteMain("INF", message);
    public static void Debug(string message)
    {
        if (MinLevel <= LogLevel.Debug) WriteMain("DBG", message);
    }
    public static void DebugSection(string title)
    {
        if (MinLevel <= LogLevel.Debug) WriteMain("DBG", $"── {title} ──");
    }
    public static void DebugLines(string header, string[] lines, int maxLines = 20)
    {
        if (MinLevel > LogLevel.Debug) return;
        WriteMain("DBG", $"{header} ({lines.Length} lines):");
        int count = System.Math.Min(lines.Length, maxLines);
        for (int i = 0; i < count; i++)
            WriteMain("CSV", $"  [{i}] {lines[i]}");
        if (lines.Length > maxLines)
            WriteMain("CSV", $"  ... ({lines.Length - maxLines} more lines hidden)");
    }
    public static void Warn(string message) => WriteMain("WRN", message);
    public static void Error(string message) => WriteMain("ERR", message);

    public static void LogSessionStart(string commandName)
    {
        RotateLogIfNeeded();
        CleanupOldBakFiles();

        var header = new string('=', 70);
        var line = $"SESSION START: {commandName}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]";
        WriteMain("INF", header);
        WriteMain("INF", line);
        WriteMain("INF", header);
        WriteLookup("INF", header);
        WriteLookup("INF", line);
        WriteLookup("INF", header);
        WriteFormula("INF", header);
        WriteFormula("INF", line);
        WriteFormula("INF", header);
    }

    public static void Lookup(string message) => WriteLookup("LKP", message);

    public static void Formula(string message) => WriteFormula("FRM", message);

    public static void FormulaOk(string operation, string formula, string detail)
    {
        WriteFormula(" OK", $"[{operation}] '{formula}' → {detail}");
    }

    public static void FormulaFail(string operation, string formula, string reason)
    {
        WriteFormula("FAIL", $"[{operation}] '{formula}' → {reason}");
    }

    private static void RotateLogIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var fi = new FileInfo(LogPath);
            if (fi.Length < MaxLogSizeBytes) return;

            lock (_lock)
            {
                if (_mainWriterField != null)
                {
                    _mainWriterField.Flush();
                    _mainWriterField.Dispose();
                    _mainWriterField = null;
                }
            }

            var bakPath = Path.Combine(LogDir, "smartcon.log.bak");
            File.Delete(bakPath);
            File.Move(LogPath, bakPath);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Rotate failed: {ex.Message}"); }
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
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Cleanup failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Cleanup failed: {ex.Message}"); }
    }

    private static void WriteMain(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                _mainWriterField ??= CreateWriter(LogPath);
                _mainWriterField.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {message}");
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Write failed: {ex.Message}"); }
    }

    private static void WriteLookup(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                _lookupWriterField ??= CreateWriter(LookupLogPath);
                _lookupWriterField.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {message}");
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Write failed: {ex.Message}"); }
    }

    private static void WriteFormula(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                _formulaWriterField ??= CreateWriter(FormulaLogPath);
                _formulaWriterField.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {message}");
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Write failed: {ex.Message}"); }
    }
}
