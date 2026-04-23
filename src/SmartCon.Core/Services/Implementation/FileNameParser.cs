using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

public sealed class FileNameParser : IFileNameParser
{
    public string? TransformStatus(string fileName, FileNameTemplate template)
    {
        if (string.IsNullOrWhiteSpace(fileName) || template.Blocks.Count == 0)
            return null;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var parts = nameWithoutExt.Split([template.Delimiter], StringSplitOptions.None);

        var statusBlock = template.Blocks.FirstOrDefault(b => b.Role == "status");
        if (statusBlock is null) return null;

        if (statusBlock.Index >= parts.Length) return null;

        var currentValue = parts[statusBlock.Index];
        var mapping = template.StatusMappings.FirstOrDefault(m => m.WipValue == currentValue);
        if (mapping is null) return null;

        parts[statusBlock.Index] = mapping.SharedValue;
        return string.Join(template.Delimiter, parts) + extension;
    }

    public (bool IsValid, string ErrorMessage) Validate(string fileName, FileNameTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.Delimiter))
            return (false, "Delimiter is not set.");

        if (template.Blocks.Count == 0)
            return (false, "No blocks defined in template.");

        var statusBlocks = template.Blocks.Where(b => b.Role == "status").ToList();
        if (statusBlocks.Count == 0)
            return (false, "No block with role 'status' defined.");

        if (template.StatusMappings.Count == 0)
            return (false, "No status mappings defined.");

        var duplicateWips = template.StatusMappings
            .GroupBy(m => m.WipValue)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateWips.Count > 0)
            return (false, $"Duplicate WIP values: {string.Join(", ", duplicateWips)}");

        if (string.IsNullOrWhiteSpace(fileName))
            return (false, "File name is empty.");

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var parts = nameWithoutExt.Split([template.Delimiter], StringSplitOptions.None);

        if (parts.Length < template.Blocks.Count)
            return (false, $"File name contains {parts.Length} blocks, but template expects {template.Blocks.Count}.");

        var statusBlock = statusBlocks[0];
        if (statusBlock.Index < parts.Length)
        {
            var currentValue = parts[statusBlock.Index];
            var found = template.StatusMappings.Any(m => m.WipValue == currentValue);
            if (!found)
                return (false, $"Current status value '{currentValue}' not found in status mappings.");
        }

        return (true, string.Empty);
    }

    public Dictionary<string, string> ParseBlocks(string fileName, FileNameTemplate template)
    {
        var result = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(fileName) || template.Blocks.Count == 0)
            return result;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var parts = nameWithoutExt.Split([template.Delimiter], StringSplitOptions.None);

        foreach (var block in template.Blocks)
        {
            if (block.Index < parts.Length)
                result[block.Role] = parts[block.Index];
        }

        return result;
    }
}
