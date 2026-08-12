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

    /// <summary>
    /// Путь к family-шаблону (.rft) для NewFamilyDocument. Предпочитает
    /// Generic Model (самый беспроблемный для сидирования), иначе первый
    /// доступный .rft. Отсутствующая папка шаблонов → null → Skip.Test.
    /// </summary>
    public static string? FindFamilyTemplate(Application app)
    {
        var root = $@"C:\ProgramData\Autodesk\RVT {app.VersionNumber}\Family Templates";
        if (!Directory.Exists(root))
        {
            return null;
        }

        string[] templates;
        try
        {
            templates = Directory.GetFiles(root, "*.rft", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return null;
        }

        if (templates.Length == 0)
        {
            return null;
        }

        // A MODEL-family template is required (nesting, extrusions, type
        // catalogs) — an annotation/detail template breaks such seeds with
        // "operation is not permitted in this type of family", and an
        // ADAPTIVE generic model ("Metric Generic Model Adaptive.rft")
        // rejects plain extrusions the same way. Exact localized file names
        // first, then a marker match that excludes adaptive templates.
        string[] exactNames =
        [
            "Metric Generic Model.rft",
            "Метрическая система, типовая модель.rft",
        ];
        foreach (var exact in exactNames)
        {
            var hit = Array.Find(templates, t =>
                string.Equals(Path.GetFileName(t), exact, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        string[] genericModelMarkers =
        [
            "Generic Model",
            "система, типовая модель",
            "Allgemeines Modell",
            "Modèle générique",
            "Modelo generico",
            "Modelo genérico",
            "Model ogolny",
            "Modello generico",
        ];
        foreach (var marker in genericModelMarkers)
        {
            var localized = Array.Find(templates, t =>
#if NET8_0_OR_GREATER
                t.Contains(marker, StringComparison.OrdinalIgnoreCase)
                && !t.Contains("Adaptive", StringComparison.OrdinalIgnoreCase));
#else
                t.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0
                && t.IndexOf("Adaptive", StringComparison.OrdinalIgnoreCase) < 0);
#endif
            if (localized is not null)
            {
                return localized;
            }
        }
        return templates[0];
    }
}
