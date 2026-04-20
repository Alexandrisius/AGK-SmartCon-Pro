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

    private static readonly Lazy<StreamWriter> _mainWriter;
    private static readonly Lazy<StreamWriter> _lookupWriter;
    private static readonly Lazy<StreamWriter> _formulaWriter;

    static SmartConLogger()
    {
        try { Directory.CreateDirectory(LogDir); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Init failed: {ex.Message}"); }

        _mainWriter = new Lazy<StreamWriter>(() => CreateWriter(LogPath));
        _lookupWriter = new Lazy<StreamWriter>(() => CreateWriter(LookupLogPath));
        _formulaWriter = new Lazy<StreamWriter>(() => CreateWriter(FormulaLogPath));
    }

    private static StreamWriter CreateWriter(string path)
        => new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        { AutoFlush = true };

    public static void Info(string message) => Write(_mainWriter, "INF", message);
    public static void Debug(string message)
    {
        if (MinLevel <= LogLevel.Debug) Write(_mainWriter, "DBG", message);
    }
    public static void DebugSection(string title)
    {
        if (MinLevel <= LogLevel.Debug) Write(_mainWriter, "DBG", $"── {title} ──");
    }
    public static void DebugLines(string header, string[] lines, int maxLines = 20)
    {
        if (MinLevel > LogLevel.Debug) return;
        Write(_mainWriter, "DBG", $"{header} ({lines.Length} lines):");
        int count = System.Math.Min(lines.Length, maxLines);
        for (int i = 0; i < count; i++)
            Write(_mainWriter, "CSV", $"  [{i}] {lines[i]}");
        if (lines.Length > maxLines)
            Write(_mainWriter, "CSV", $"  ... ({lines.Length - maxLines} more lines hidden)");
    }
    public static void Warn(string message) => Write(_mainWriter, "WRN", message);
    public static void Error(string message) => Write(_mainWriter, "ERR", message);

    public static void LogSessionStart(string commandName)
    {
        RotateLogIfNeeded();
        CleanupOldBakFiles();

        var header = $"{'=',70}";
        var line = $"SESSION START: {commandName}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]";
        Write(_mainWriter, "INF", header);
        Write(_mainWriter, "INF", line);
        Write(_mainWriter, "INF", header);
        Write(_lookupWriter, "INF", header);
        Write(_lookupWriter, "INF", line);
        Write(_lookupWriter, "INF", header);
        Write(_formulaWriter, "INF", header);
        Write(_formulaWriter, "INF", line);
        Write(_formulaWriter, "INF", header);
    }

    public static void Lookup(string message)
    {
        Write(_lookupWriter, "LKP", message);
    }



    public static void Formula(string message)
    {
        Write(_formulaWriter, "FRM", message);
    }

    public static void FormulaOk(string operation, string formula, string detail)
    {
        Write(_formulaWriter, " OK", $"[{operation}] '{formula}' → {detail}");
    }

    public static void FormulaFail(string operation, string formula, string reason)
    {
        Write(_formulaWriter, "FAIL", $"[{operation}] '{formula}' → {reason}");
    }

    private static void RotateLogIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var fi = new FileInfo(LogPath);
            if (fi.Length < MaxLogSizeBytes) return;

            if (_mainWriter.IsValueCreated)
            {
                lock (_lock)
                {
                    _mainWriter.Value.Flush();
                    _mainWriter.Value.Dispose();
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

    private static void Write(Lazy<StreamWriter> writer, string level, string message)
    {
        try
        {
            lock (_lock)
            {
                writer.Value.WriteLine(
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {message}");
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Write failed: {ex.Message}"); }
    }
}
