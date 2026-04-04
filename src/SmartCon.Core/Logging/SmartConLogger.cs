using System;
using System.IO;

namespace SmartCon.Core.Logging;

/// <summary>
/// Файловый логгер SmartCon.
/// Пишет в %APPDATA%\AGK\SmartCon\smartcon.log (общий) и
/// %APPDATA%\AGK\SmartCon\lookup-diagnostic.log (только LookupTable/ParameterResolver).
/// Потокобезопасен через lock. При ошибке записи — молча игнорирует.
/// Доступен из всех сборок (SmartCon.Core, SmartCon.Revit, SmartCon.PipeConnect, SmartCon.App).
/// </summary>
public static class SmartConLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon");

    private static readonly string LogPath        = Path.Combine(LogDir, "smartcon.log");
    private static readonly string LookupLogPath  = Path.Combine(LogDir, "lookup-diagnostic.log");

    private static readonly object _lock = new();

    static SmartConLogger()
    {
        try { Directory.CreateDirectory(LogDir); }
        catch { /* если не удалось создать папку — просто не пишем */ }
    }

    // ── Основной лог ──────────────────────────────────────────────────────

    public static void Info(string message)  => Write(LogPath, "INF", message);
    public static void Warn(string message)  => Write(LogPath, "WRN", message);
    public static void Error(string message) => Write(LogPath, "ERR", message);

    /// <summary>Разделитель сессий при старте команды — пишет в оба файла.</summary>
    public static void LogSessionStart(string commandName)
    {
        var header = $"{'=',70}";
        var line   = $"SESSION START: {commandName}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]";
        Write(LogPath,       "INF", header);
        Write(LogPath,       "INF", line);
        Write(LogPath,       "INF", header);
        Write(LookupLogPath, "INF", header);
        Write(LookupLogPath, "INF", line);
        Write(LookupLogPath, "INF", header);
    }

    // ── LookupTable / ParameterResolver диагностика ───────────────────────

    /// <summary>
    /// Пишет ТОЛЬКО в lookup-diagnostic.log (не в smartcon.log).
    /// Используется в RevitLookupTableService, RevitParameterResolver, FamilyParameterAnalyzer.
    /// </summary>
    public static void Lookup(string message)
    {
        Write(LookupLogPath, "LKP", message);
    }

    /// <summary>Секция-разделитель внутри LookupTable диагностики.</summary>
    public static void LookupSection(string title)
    {
        var line = $"── {title} ──";
        Write(LookupLogPath, "LKP", line);
    }

    /// <summary>Записать массив строк (например, строки CSV) в lookup-diagnostic.log.</summary>
    public static void LookupLines(string header, string[] lines, int maxLines = 20)
    {
        Write(LookupLogPath, "LKP", $"{header} ({lines.Length} строк):");
        int count = System.Math.Min(lines.Length, maxLines);
        for (int i = 0; i < count; i++)
            Write(LookupLogPath, "CSV", $"  [{i}] {lines[i]}");
        if (lines.Length > maxLines)
            Write(LookupLogPath, "CSV", $"  ... (ещё {lines.Length - maxLines} строк скрыто)");
    }

    // ── Внутренняя запись ─────────────────────────────────────────────────

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
        catch { /* запись в лог не должна ломать основную логику */ }
    }
}
