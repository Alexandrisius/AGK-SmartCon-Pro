using Autodesk.Revit.ApplicationServices;

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
}
