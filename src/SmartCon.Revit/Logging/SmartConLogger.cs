using System;
using System.IO;

namespace SmartCon.Revit.Logging;

/// <summary>
/// Простой файловый логгер. Пишет в %APPDATA%\AGK\SmartCon\smartcon.log.
/// Потокобезопасен через lock. При ошибке записи — молча игнорирует.
/// </summary>
internal static class SmartConLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon", "smartcon.log");

    private static readonly object _lock = new();

    static SmartConLogger()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
        }
        catch { /* если не удалось создать папку — просто не пишем */ }
    }

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* запись в лог не должна ломать основную логику */ }
    }

    /// <summary>Создаёт разделитель сессий при старте команды.</summary>
    public static void LogSessionStart(string commandName)
    {
        Log($"{'=',60}");
        Log($"SESSION START: {commandName}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
        Log($"{'=',60}");
    }
}
