namespace SmartCon.Core.Deployment;

/// <summary>Сведения об уже загруженной в процесс сборке-зависимости.</summary>
public sealed record LoadedDependencyInfo(string Name, Version Version, string Location);

/// <summary>Конфликт: загружена версия ниже той, с которой собран SmartCon.</summary>
public sealed record DependencyConflict(string Name, Version LoadedVersion, Version MinimumVersion, string Location);

/// <summary>
/// Анализатор конфликтов зависимостей в окружении Revit (ADR-051, Issue #134).
/// После внедрения изоляции (ILRepack на net48, AssemblyLoadContext на net8)
/// конфликт не должен ломать SmartCon — отчёт носит диагностический характер
/// и помогает идентифицировать чужой адд-ин, влияющий на процесс.
/// </summary>
public static class DependencyConflictAnalyzer
{
    /// <summary>Минимальные assembly-версии, с которыми собран SmartCon.</summary>
    public static IReadOnlyDictionary<string, Version> MinimumVersions { get; } =
        new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
        {
            ["CommunityToolkit.Mvvm"] = new Version(8, 4, 0, 0),
            ["Microsoft.Extensions.DependencyInjection"] = new Version(8, 0, 0, 1),
            ["Microsoft.Bcl.AsyncInterfaces"] = new Version(10, 0, 0, 1),
            ["System.Text.Json"] = new Version(8, 0, 0, 6),
            ["System.Threading.Tasks.Extensions"] = new Version(4, 2, 4, 0),
            ["System.Runtime.CompilerServices.Unsafe"] = new Version(6, 0, 3, 0),
        };

    public static IReadOnlyList<DependencyConflict> Analyze(IEnumerable<LoadedDependencyInfo> loadedAssemblies)
    {
        var conflicts = new List<DependencyConflict>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var loaded in loadedAssemblies)
        {
            if (!MinimumVersions.TryGetValue(loaded.Name, out var minimum))
                continue;
            if (!seen.Add(loaded.Name))
                continue;
            if (loaded.Version < minimum)
                conflicts.Add(new DependencyConflict(loaded.Name, loaded.Version, minimum, loaded.Location));
        }

        return conflicts;
    }
}
