namespace SmartCon.IntegrationTests.Agent;

/// <summary>Shared parameter file scope used by the shared-parameter tests.</summary>
public sealed partial class AgentFamilyWriteToolsTests
{
    private static SharedFileScope CreateSharedFileScope() =>
        SharedFileScope.Create(Application);

    private sealed class SharedFileScope : IDisposable
    {
        private readonly Autodesk.Revit.ApplicationServices.Application _application;
        private readonly string? _previous;
        private readonly string? _temporary;

        public string? FilePath => _temporary;

        private SharedFileScope(
            Autodesk.Revit.ApplicationServices.Application application,
            string? temporary)
        {
            _application = application;
            _previous = application.SharedParametersFilename;
            _temporary = temporary;
        }

        public static SharedFileScope Create(
            Autodesk.Revit.ApplicationServices.Application application)
        {
            var directory = Path.Combine(Path.GetTempPath(), "SmartConFamilyTests");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "shared-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, string.Empty);
            var scope = new SharedFileScope(application, path);
            application.SharedParametersFilename = path;
            return scope;
        }

        public static SharedFileScope PointToMissingFile(
            Autodesk.Revit.ApplicationServices.Application application)
        {
            var scope = new SharedFileScope(application, temporary: null);
            application.SharedParametersFilename = Path.Combine(
                Path.GetTempPath(),
                "SmartConFamilyTests",
                "missing",
                "shared-parameters.txt");
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

            if (_temporary is null)
                return;

            try
            {
                if (File.Exists(_temporary))
                    File.Delete(_temporary);
            }
            catch (Exception)
            {
                // Temp file cleanup is best-effort.
            }
        }
    }

}
