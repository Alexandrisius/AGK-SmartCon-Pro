using Autodesk.Revit.ApplicationServices;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Swaps the shared parameter file to a temp file so live agent tests never
/// touch the user's ФОП (same mechanism as the migration dogfood). The previous
/// file is restored on dispose.
/// </summary>
internal sealed class SharedParameterFileScope : IDisposable
{
    private readonly Application _application;
    private readonly string? _previous;
    private readonly string? _temporary;

    private SharedParameterFileScope(Application application, string temporary)
    {
        _application = application;
        _previous = application.SharedParametersFilename;
        _temporary = temporary;
    }

    public static SharedParameterFileScope Create(Application application)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SmartConAgentSharedFile");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "shared-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, string.Empty);
        var scope = new SharedParameterFileScope(application, path);
        application.SharedParametersFilename = path;
        return scope;
    }

    public void Dispose()
    {
        try
        {
            _application.SharedParametersFilename = _previous ?? string.Empty;
        }
        catch (Exception)
        {
            // Restoring the shared parameter file is best-effort in the test host.
        }

        try
        {
            if (_temporary is not null && File.Exists(_temporary))
                File.Delete(_temporary);
        }
        catch (Exception)
        {
            // Temp file cleanup is best-effort.
        }
    }
}
