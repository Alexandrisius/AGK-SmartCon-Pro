using System.Linq;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Pure C# парсер Type Catalog (.txt) семейств Revit.
/// Без зависимости от Revit API. Thread-safe.
/// </summary>
public static class TypeCatalogParser
{
    /// <summary>
    /// Парсит Type Catalog из строки.
    /// Первый символ первой строки — разделитель.
    /// </summary>
    public static TypeCatalogParseResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new TypeCatalogParseResult([], []);

        var lines = SplitLines(content);
        if (lines.Count == 0)
            return new TypeCatalogParseResult([], []);

        var delimiter = DetectDelimiter(lines[0]);
        var headerFields = ParseCsvLine(lines[0], delimiter);

        // Первый элемент заголовка — имя типа (обычно пустой в .txt),
        // остальные — параметры формата ParamName##Type##Units
        var parameterNames = headerFields
            .Skip(1)
            .Select(h => h.Split(new[] { "##" }, StringSplitOptions.None)[0].Trim())
            .Where(h => !string.IsNullOrEmpty(h))
            .ToList();

        var entries = new List<TypeCatalogEntry>();
        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseCsvLine(line, delimiter);
            if (fields.Count == 0)
                continue;

            var typeName = fields[0].Trim();
            if (string.IsNullOrEmpty(typeName))
                continue;

            var paramValues = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var j = 1; j < fields.Count && j - 1 < parameterNames.Count; j++)
            {
                var paramName = parameterNames[j - 1];
                var value = fields[j].Trim();
                paramValues[paramName] = value;
            }

            entries.Add(new TypeCatalogEntry(typeName, paramValues));
        }

        return new TypeCatalogParseResult(parameterNames, entries);
    }

    private static char DetectDelimiter(string firstLine)
    {
        if (string.IsNullOrEmpty(firstLine))
            return ',';
        return firstLine[0];
    }

    private static List<string> SplitLines(string content)
    {
        var lines = new List<string>();
        var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// Ручной парсинг CSV-строки с поддержкой кавычек.
    /// Двойные кавычки ("") внутри поля = экранированная кавычка.
    /// </summary>
    private static List<string> ParseCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Проверяем следующий символ
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++; // Пропускаем вторую кавычку
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }
}
