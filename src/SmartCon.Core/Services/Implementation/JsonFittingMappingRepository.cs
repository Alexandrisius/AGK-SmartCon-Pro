using System.Text.Json;
using System.Text.Json.Serialization;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Хранение типов коннекторов и правил маппинга в JSON-файле AppData.
/// Путь: %APPDATA%\AGK\SmartCon\connector-mapping.json
/// </summary>
public sealed class JsonFittingMappingRepository : IFittingMappingRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;

    public JsonFittingMappingRepository()
        : this(BuildDefaultPath()) { }

    public JsonFittingMappingRepository(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static string BuildDefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "AGK", "SmartCon", "connector-mapping.json");
    }

    public string GetStoragePath() => _filePath;

    public IReadOnlyList<ConnectorTypeDefinition> GetConnectorTypes()
    {
        var root = ReadFile();
        return (root?.ConnectorTypes ?? [])
            .Select(d => new ConnectorTypeDefinition
            {
                Code = d.Code,
                Name = d.Name ?? string.Empty,
                Description = d.Description ?? string.Empty,
            })
            .ToList();
    }

    public void SaveConnectorTypes(IReadOnlyList<ConnectorTypeDefinition> types)
    {
        var root = ReadFile() ?? new MappingFileRoot();
        root.ConnectorTypes = types
            .Select(t => new ConnectorTypeDto { Code = t.Code, Name = t.Name, Description = t.Description })
            .ToList();
        // Сохраняем и правила тоже — не перетираем их
        if (root.MappingRules == null || root.MappingRules.Count == 0)
            root.MappingRules = [];
        WriteFile(root);
    }

    public IReadOnlyList<FittingMappingRule> GetMappingRules()
    {
        var root = ReadFile();
        return (root?.MappingRules ?? [])
            .Select(r => new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(r.FromType),
                ToType = new ConnectionTypeCode(r.ToType),
                IsDirectConnect = r.IsDirectConnect,
                FittingFamilies = (r.FittingFamilies ?? [])
                    .Select(f => new FittingMapping
                    {
                        FamilyName = f.FamilyName ?? string.Empty,
                        SymbolName = f.SymbolName ?? "*",
                        Priority = f.Priority,
                    })
                    .ToList(),
                ReducerFamilies = (r.ReducerFamilies ?? [])
                    .Select(f => new FittingMapping
                    {
                        FamilyName = f.FamilyName ?? string.Empty,
                        SymbolName = f.SymbolName ?? "*",
                        Priority = f.Priority,
                    })
                    .ToList(),
            })
            .ToList();
    }

    public void SaveMappingRules(IReadOnlyList<FittingMappingRule> rules)
    {
        var root = ReadFile() ?? new MappingFileRoot();
        root.MappingRules = rules
            .Select(r => new MappingRuleDto
            {
                FromType = r.FromType.Value,
                ToType = r.ToType.Value,
                IsDirectConnect = r.IsDirectConnect,
                FittingFamilies = r.FittingFamilies
                    .Select(f => new FittingMappingDto
                    {
                        FamilyName = f.FamilyName,
                        SymbolName = f.SymbolName,
                        Priority = f.Priority,
                    })
                    .ToList(),
                ReducerFamilies = r.ReducerFamilies
                    .Select(f => new FittingMappingDto
                    {
                        FamilyName = f.FamilyName,
                        SymbolName = f.SymbolName,
                        Priority = f.Priority,
                    })
                    .ToList(),
            })
            .ToList();
        if (root.ConnectorTypes == null || root.ConnectorTypes.Count == 0)
            root.ConnectorTypes = [];
        WriteFile(root);
    }

    private MappingFileRoot? ReadFile()
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<MappingFileRoot>(json, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private void WriteFile(MappingFileRoot root)
    {
        var json = JsonSerializer.Serialize(root, SerializerOptions);
        File.WriteAllText(_filePath, json);
    }

    // ── DTO-классы для сериализации ────────────────────────────────────────

    private sealed class MappingFileRoot
    {
        public List<ConnectorTypeDto> ConnectorTypes { get; set; } = [];
        public List<MappingRuleDto> MappingRules { get; set; } = [];
    }

    private sealed class ConnectorTypeDto
    {
        public int Code { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    private sealed class FittingMappingDto
    {
        public string? FamilyName { get; set; }
        public string? SymbolName { get; set; }
        public int Priority { get; set; }
    }

    private sealed class MappingRuleDto
    {
        public int FromType { get; set; }
        public int ToType { get; set; }
        public bool IsDirectConnect { get; set; }
        public List<FittingMappingDto>? FittingFamilies { get; set; }
        public List<FittingMappingDto>? ReducerFamilies { get; set; }
    }
}
