using System.IO;

namespace SmartCon.Agent.Services.Llm;

internal static class AgentEnvFile
{
    internal static string SecretsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon", "Agent", "secrets.env");

    internal static IReadOnlyDictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = SecretsPath;
            if (!File.Exists(path))
                return result;

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    continue;

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                if (value.Length >= 2
                    && ((value[0] == '"' && value[value.Length - 1] == '"')
                        || (value[0] == '\'' && value[value.Length - 1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                if (key.Length > 0)
                    result[key] = value;
            }
        }
        catch (Exception ex)
        {
            SmartCon.Core.Logging.SmartConLogger.Warn(
                "AgentEnvFile.Load failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: set LLM credentials as environment variables]");
        }

        return result;
    }

    internal static bool Save(IReadOnlyDictionary<string, string> values)
    {
        try
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in Load())
                merged[pair.Key] = pair.Value;

            foreach (var pair in values)
                merged[pair.Key] = pair.Value;

            var path = SecretsPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var lines = new List<string>();
            foreach (var pair in merged.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                lines.Add(pair.Key + "=" + pair.Value);

            File.WriteAllLines(path, lines);
            return true;
        }
        catch (Exception ex)
        {
            SmartCon.Core.Logging.SmartConLogger.Warn(
                "AgentEnvFile.Save failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: check write access to %APPDATA%\\AGK\\SmartCon\\Agent]");
            return false;
        }
    }
}
