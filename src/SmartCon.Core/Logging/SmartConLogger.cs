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
    private static readonly string FreezeLogPath = Path.Combine(LogDir, "freeze-diagnostic.log");

    private static readonly object _lock = new();

    private const long MaxLogSizeBytes = 5 * 1024 * 1024;
    private const int MaxBakFiles = 3;

    // Default min level is decided at type initialisation. The DEBUG
    // symbol is set by the C# compiler for every Debug.* configuration
    // (Debug, Debug.R19..R26) and unset for Release.*. That gives us the
    // "Debug build => verbose tracing, Release build => errors only"
    // behaviour without a config file or a host-side wiring. Operators
    // can override at runtime via the SMARTCON_LOG_LEVEL environment
    // variable (Debug | Info | Warn | Error). See
    // docs/architecture/logging.md for the full precedence chain.
    private static readonly LogLevel _defaultMinLevel = ComputeDefaultMinLevel();

    private static LogLevel ComputeDefaultMinLevel()
    {
#if DEBUG
        return LogLevel.Debug;
#else
        return LogLevel.Info;
#endif
    }

    private static LogLevel _minLevel = ResolveInitialMinLevel();

    private static LogLevel ResolveInitialMinLevel()
    {
        var env = Environment.GetEnvironmentVariable("SMARTCON_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(env)
            && Enum.TryParse<LogLevel>(env.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }
        return _defaultMinLevel;
    }

    /// <summary>
    /// Current minimum log level. Messages below this level are dropped
    /// before the lock is even acquired. Defaults to Debug in Debug
    /// builds, Info in Release builds, and can be overridden at runtime
    /// via the SMARTCON_LOG_LEVEL environment variable or by setting this
    /// property before any logger call.
    /// </summary>
    public static LogLevel MinLevel
    {
        get => _minLevel;
        set => _minLevel = value;
    }

    private static StreamWriter? _mainWriterField;
    private static StreamWriter? _lookupWriterField;
    private static StreamWriter? _formulaWriterField;
    private static StreamWriter? _freezeWriterField;

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

    public static void Info(string message)
    {
        if (MinLevel > LogLevel.Info) return;
        WriteMain("INF", message);
    }
    public static void Debug(string message)
    {
        if (MinLevel > LogLevel.Debug) return;
        WriteMain("DBG", message);
    }
    public static void DebugSection(string title)
    {
        if (MinLevel > LogLevel.Debug) return;
        WriteMain("DBG", $"── {title} ──");
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
    public static void Warn(string message)
    {
        if (MinLevel > LogLevel.Warn) return;
        WriteMain("WRN", message);
    }
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

    public static void Freeze(string message) => WriteFreeze("FRZ", message);

    public static void FreezeThreadPool(string operation)
    {
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
        ThreadPool.GetAvailableThreads(out var availWorker, out var availIo);
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);

        WriteFreeze("THR", $"[{operation}] ThreadPool — Available: {availWorker}/{maxWorker} workers, {availIo}/{maxIo} IO | Min: {minWorker}/{minIo}");
    }

    public static void FreezeTimer(string operation, Action action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            action();
            WriteFreeze("TIM", $"[{operation}] Completed in {sw.Elapsed.TotalMilliseconds:F1}ms");
        }
        catch (Exception ex)
        {
            WriteFreeze("ERR", $"[{operation}] Failed after {sw.Elapsed.TotalMilliseconds:F1}ms: {ex.Message}");
            throw;
        }
    }

    public static T FreezeTimer<T>(string operation, Func<T> func)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = func();
            WriteFreeze("TIM", $"[{operation}] Completed in {sw.Elapsed.TotalMilliseconds:F1}ms");
            return result;
        }
        catch (Exception ex)
        {
            WriteFreeze("ERR", $"[{operation}] Failed after {sw.Elapsed.TotalMilliseconds:F1}ms: {ex.Message}");
            throw;
        }
    }

    public static void FormulaFail(string operation, string formula, string reason)
    {
        WriteFormula("FAIL", $"[{operation}] '{formula}' → {reason}");
    }

    public static IDisposable BeginScope(string operation, params (string Key, object? Value)[] properties)
    {
        return new LogScope(operation, properties);
    }

    public static IDisposable Measure(string operation)
    {
        return new TimedScope(operation);
    }

    private sealed class LogScope : IDisposable
    {
        private readonly string _operation;
        private readonly string _opId;
        private readonly (string Key, object? Value)[] _properties;
        private readonly Stopwatch _sw;
        private bool _disposed;

        public LogScope(string operation, (string Key, object? Value)[] properties)
        {
            _operation = operation;
            _opId = Guid.NewGuid().ToString("N")[..8];
            _properties = properties;
            _sw = Stopwatch.StartNew();
            WriteMain("INF", $"[OpId={_opId}] === START {_operation} ==={FormatProps()}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sw.Stop();
            WriteMain("INF", $"[OpId={_opId}] === END {_operation} elapsed={_sw.Elapsed.TotalMilliseconds:F1}ms ===");
        }

        private string FormatProps()
        {
            if (_properties is null || _properties.Length == 0) return string.Empty;
            return " " + string.Join(" ", _properties.Select(p => $"{p.Key}={p.Value}"));
        }
    }

    private sealed class TimedScope : IDisposable
    {
        private readonly string _operation;
        private readonly Stopwatch _sw;
        private bool _disposed;

        public TimedScope(string operation)
        {
            _operation = operation;
            _sw = Stopwatch.StartNew();
            WriteFreeze("TIM", $"[{operation}] Started");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sw.Stop();
            WriteFreeze("TIM", $"[{_operation}] Completed in {_sw.Elapsed.TotalMilliseconds:F1}ms");
        }
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

    private static void WriteFreeze(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                _freezeWriterField ??= CreateWriter(FreezeLogPath);
                _freezeWriterField.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {message}");
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Write failed: {ex.Message}"); }
    }
}
