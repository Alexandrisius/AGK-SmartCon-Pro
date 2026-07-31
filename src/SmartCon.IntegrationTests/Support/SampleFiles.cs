using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Доступ к sample-файлам установленного Revit (.rfa/.rvt из Samples).
/// Путь зависит от версии Revit, в которую инъектирован тест — вычисляется
/// в рантайме. Отсутствующий файл → null → вызывающий hook обязан Skip.Test.
/// </summary>
internal static class SampleFiles
{
    public static string? FindSample(Application app, string fileName)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Autodesk", $"Revit {app.VersionNumber}", "Samples");

        var path = Path.Combine(directory, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Пустой дефолтный шаблон НЕ содержит типов изоляции (они материализуются
    /// только при UI-клике «Add Insulation», CF-4720 — API их не создаёт).
    /// MEP-шаблоны установки Revit содержат готовые типы (проверено зондом
    /// PROBE T на R25: Systems/Mechanical/Plumbing — PipeIns=2, DuctIns=2).
    /// Отсутствующий/залоченный шаблон → null → вызывающий обязан Skip.Test.
    /// </summary>
    public static Document? NewMepTemplateDocument(Application app)
    {
        var root = $@"C:\ProgramData\Autodesk\RVT {app.VersionNumber}\Templates";
        var candidates = new[]
        {
            $@"{root}\Russian\Systems-DefaultRUSRUS.rte",
            $@"{root}\Russian\Mechanical-DefaultRUSRUS.rte",
            $@"{root}\Russian\Plumbing-DefaultRUSRUS.rte",
            $@"{root}\English\Systems-Default_Metric.rte",
            $@"{root}\English\Mechanical-Default_Metric.rte",
            $@"{root}\English\Plumbing-Default_Metric.rte",
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;

            try
            {
                return app.NewProjectDocument(path);
            }
            catch (Exception)
            {
                // Шаблон залочен/битый/новее текущей версии — пробуем следующий.
            }
        }
        return null;
    }
}
