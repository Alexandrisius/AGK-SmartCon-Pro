using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SmartCon.IntegrationTests.Agent.Support;

/// <summary>
/// Base document of a benchmark seed: with <c>SMARTCON_BENCH_TEMPLATE</c> set to
/// a .rvt file every seeded case runs on a fresh copy of the owner's real model
/// (opened with <c>OpenDocumentFile</c>), otherwise on the default metric
/// project document. Copies live under the benchmark results root and are
/// deleted by <see cref="DeleteTrackedCopies"/> in the test cleanup.
/// </summary>
internal static class BenchDocuments
{
    private static readonly List<string> TrackedCopies = new();
    private static readonly object Gate = new();

    /// <summary>Template path from SMARTCON_BENCH_TEMPLATE, or null for the synthetic base.</summary>
    public static string? TemplatePath()
    {
        var path = Environment.GetEnvironmentVariable("SMARTCON_BENCH_TEMPLATE");
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public static Document NewBase(Application application)
    {
        var template = TemplatePath();
        if (template is null)
            return application.NewProjectDocument(UnitSystem.Metric);

        if (!File.Exists(template))
            throw new FileNotFoundException(
                "SMARTCON_BENCH_TEMPLATE указывает на несуществующий файл: " + template, template);

        var directory = Path.Combine(BenchRunArtifactWriter.ResultsRoot(), "base");
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "base-" + Guid.NewGuid().ToString("N") + ".rvt");
        File.Copy(template, copy);

        lock (Gate)
        {
            TrackedCopies.Add(copy);
        }

        return application.OpenDocumentFile(copy);
    }

    /// <summary>Deletes the template copies of this test class run (documents must be closed first).</summary>
    public static void DeleteTrackedCopies()
    {
        List<string> copies;
        lock (Gate)
        {
            copies = TrackedCopies.ToList();
            TrackedCopies.Clear();
        }

        foreach (var copy in copies)
        {
            try
            {
                if (File.Exists(copy))
                    File.Delete(copy);
            }
            catch (IOException)
            {
                // Best effort: a still-open document keeps the copy on disk.
            }
        }
    }
}
