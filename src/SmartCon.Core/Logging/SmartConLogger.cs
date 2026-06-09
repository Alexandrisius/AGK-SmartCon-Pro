using System.Diagnostics;
using System.Text;

namespace SmartCon.Core.Logging;

/// <summary>
/// Application-wide structured logger. Writes to two files under
/// <c>%AppData%\AGK\SmartCon\</c>:
/// <list type="bullet">
///   <item><c>smartcon.log</c> — main event log (Info/Warn/Error, plus
///     Debug when enabled). 5 MB rotation, 3 .bak generations.</item>
///   <item><c>formula-diagnostic.log</c> — append-only trace of every
///     Revit formula the engine sees (resolved / unresolved / failed).
///     No rotation: this file is intentionally cumulative so we can
///     collect statistics on which formulas the PipeConnect module
///     encounters and how each one was resolved.</item>
/// </list>
/// Active <see cref="LogScope"/> instances decorate every line with
/// <c>[OpId=… Op=…]</c> for correlation across awaits and threads.
/// </summary>
public static class SmartConLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon");

    private static readonly string LogPath = Path.Combine(LogDir, "smartcon.log");
    private static readonly string FormulaLogPath = Path.Combine(LogDir, "formula-diagnostic.log");

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

    private static volatile LogLevel _minLevel = ResolveInitialMinLevel();

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
        WriteFormula("INF", header);
        WriteFormula("INF", line);
        WriteFormula("INF", header);
    }

    /// <summary>
    /// Close a session previously opened by <see cref="LogSessionStart"/>.
    /// Emits a matching <c>SESSION END</c> banner into both log files with
    /// the timestamp at which the session ended. The duration since
    /// <see cref="LogSessionStart"/> is shown in seconds (rounded to
    /// milliseconds) so an operator can grep the file for the session
    /// lifecycle at a glance.
    /// </summary>
    public static void LogSessionEnd(string commandName, DateTime startedAt)
    {
        var header = new string('=', 70);
        var duration = (DateTime.Now - startedAt).TotalSeconds;
        var line = $"SESSION END:   {commandName}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]  duration={duration:F3}s";
        WriteMain("INF", header);
        WriteMain("INF", line);
        WriteMain("INF", header);
        WriteFormula("INF", header);
        WriteFormula("INF", line);
        WriteFormula("INF", header);
    }

    public static void Formula(string message) => WriteFormula("FRM", message);

    public static void FormulaOk(string operation, string formula, string detail)
    {
        WriteFormula(" OK", $"[{operation}] '{formula}' → {detail}");
    }

    public static void FormulaFail(string operation, string formula, string reason)
    {
        WriteFormula("FAIL", $"[{operation}] '{formula}' → {reason}");
    }

    /// <summary>
    /// Open a structured logging scope. Every log line written while the
    /// scope is open (including across <c>await</c> boundaries) is prefixed
    /// with <c>[OpId=… Op=…]</c> and the supplied <paramref name="properties"/>.
    /// Dispose the returned <see cref="IDisposable"/> to close the scope
    /// and emit the elapsed-time footer.
    /// </summary>
    public static IDisposable BeginScope(string operation, params (string Key, object? Value)[] properties)
    {
        var opId = Guid.NewGuid().ToString("N")[..8];
        var scope = new LogScope(operation, properties, opId);
        WriteMain("INF", $"{scope.FormatPrefix()} === START ===");
        return LogScopeProvider.Push(scope);
    }

    /// <summary>
    /// Open a scope that records elapsed time on dispose, but does not
    /// emit a START/END pair (suitable for fire-and-forget timers that
    /// just want the <c>elapsed=</c> footer line). Returns a
    /// <see cref="MeasureScope"/> which is <see cref="IDisposable"/> and
    /// also exposes <see cref="MeasureScope.GetElapsedMilliseconds"/>
    /// for callers that want to read the elapsed time before the scope
    /// is disposed.
    /// </summary>
    public static MeasureScope Measure(string operation)
    {
        var sw = Stopwatch.StartNew();
        var opId = Guid.NewGuid().ToString("N")[..8];
        var scope = new MeasureScope(operation, Array.Empty<(string Key, object? Value)>(), opId, sw);
        LogScopeProvider.Push(scope);
        return scope;
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

    /// <summary>
    /// Compose the scope prefix for a write. Joins all active scopes
    /// (outermost first) with a single space so a deeply-nested call
    /// shows the full chain: <c>[OpId=… Op=Outer] [OpId=… Op=Inner] message</c>.
    /// Returns an empty string when no scope is active.
    /// </summary>
    private static string ComposeScopePrefix()
    {
        var scopes = LogScopeProvider.EnumerateFromRoot().ToList();
        if (scopes.Count == 0) return string.Empty;
        var sb = new StringBuilder(scopes.Count * 48);
        foreach (var s in scopes)
        {
            sb.Append(s.FormatPrefix()).Append(' ');
        }
        return sb.ToString();
    }

    internal static void WriteMain(string level, string message)
    {
        try
        {
            var prefix = ComposeScopePrefix();
            lock (_lock)
            {
                _mainWriterField ??= CreateWriter(LogPath);
                _mainWriterField.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {prefix}{message}");
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Write failed: {ex.Message}"); }
    }

    internal static void WriteFormula(string level, string message)
    {
        try
        {
            var prefix = ComposeScopePrefix();
            lock (_lock)
            {
                _formulaWriterField ??= CreateWriter(FormulaLogPath);
                _formulaWriterField.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {prefix}{message}");
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] Write failed: {ex.Message}"); }
    }
}
