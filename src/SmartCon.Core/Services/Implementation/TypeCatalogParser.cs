using System.Linq;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

public static class TypeCatalogParser
{
    public static TypeCatalogParseResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new TypeCatalogParseResult([], []);

        var records = ParseCsvRecords(content);
        if (records.Count == 0)
            return new TypeCatalogParseResult([], []);

        var delimiter = DetectDelimiter(records[0]);
        var headerFields = SplitCsvFields(records[0], delimiter);

        var parameterNames = headerFields
            .Skip(1)
            .Select(h => h.Split(new[] { "##" }, StringSplitOptions.None)[0].Trim())
            .Where(h => !string.IsNullOrEmpty(h))
            .ToList();

        var entries = new List<TypeCatalogEntry>();
        for (var i = 1; i < records.Count; i++)
        {
            var fields = SplitCsvFields(records[i], delimiter);
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

    private static char DetectDelimiter(string firstRecord)
    {
        if (string.IsNullOrEmpty(firstRecord))
            return ',';
        return firstRecord[0];
    }

    private static List<string> ParseCsvRecords(string content)
    {
        var records = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (inQuotes)
            {
                sb.Append(c);
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        sb.Append(content[i + 1]);
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                    sb.Append(c);
                }
                else if (c == '\r')
                {
                    continue;
                }
                else if (c == '\n')
                {
                    if (sb.Length > 0)
                    {
                        records.Add(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        if (sb.Length > 0)
            records.Add(sb.ToString());

        return records;
    }

    private static List<string> SplitCsvFields(string record, char delimiter)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < record.Length; i++)
        {
            var c = record[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < record.Length && record[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
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
