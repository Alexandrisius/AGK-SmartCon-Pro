using System.IO;
using System.Runtime.CompilerServices;

namespace SmartCon.Tests;

/// <summary>
/// Лог-изоляция юнит-тестов: без этого каждый `dotnet test` пишет (и ротирует,
/// затирая) операторский %APPDATA%\AGK\SmartCon\smartcon.log — 2026-09-11 так
/// был потерян лог ручного теста владельца в Revit. ModuleInitializer выполняется
/// до любого теста, до первого касания SmartConLogger (LogDir читается в
/// static-инициализации). Интеграционные тесты (внутри Revit) НЕ перенаправляют —
/// их лог и должен жить в прод-пути.
/// </summary>
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("SMARTCON_LOG_DIR",
            Path.Combine(Path.GetTempPath(), "SmartConTests", "log"));
    }
}
