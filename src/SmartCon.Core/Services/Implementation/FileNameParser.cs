using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

public sealed class FileNameParser : IFileNameParser
{
    public string? TransformForExport(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary)
    {
        if (string.IsNullOrWhiteSpace(fileName) || template.Blocks.Count == 0)
            return null;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var parsed = ParseBlocks(fileName, template);

        foreach (var mapping in template.ExportMappings)
        {
            if (parsed.TryGetValue(mapping.Field, out var currentVal)
                && string.Equals(currentVal, mapping.SourceValue, StringComparison.OrdinalIgnoreCase))
            {
                parsed[mapping.Field] = mapping.TargetValue;
            }
        }

        var orderedValues = template.Blocks
            .OrderBy(b => b.Index)
            .Select(b => parsed.TryGetValue(b.Field, out var v) ? v : string.Empty);

        return string.Join("-", orderedValues) + extension;
    }

    public (bool IsValid, string ErrorMessage) Validate(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary)
    {
        if (template.Blocks.Count == 0)
            return (false, "No blocks defined in template.");

        if (string.IsNullOrWhiteSpace(fileName))
            return (false, "File name is empty.");

        return (true, string.Empty);
    }

    public ValidationResult ValidateDetailed(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary)
    {
        var (basicValid, basicError) = Validate(fileName, template, fieldLibrary);
        if (!basicValid)
            return new ValidationResult(false, basicError, []);

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var parsed = ParseBlocks(fileName, template);
        var blockResults = new List<BlockValidation>();
        var errors = new List<string>();

        foreach (var block in template.Blocks.OrderBy(b => b.Index))
        {
            var value = parsed.TryGetValue(block.Field, out var v) ? v : string.Empty;

            if (string.IsNullOrEmpty(block.Field))
            {
                blockResults.Add(new BlockValidation(block.Index, block.Field, value, false, "Block field is not selected."));
                continue;
            }

            var fieldDef = fieldLibrary.FirstOrDefault(fd =>
                string.Equals(fd.Name, block.Field, StringComparison.OrdinalIgnoreCase));

            if (fieldDef is null)
            {
                blockResults.Add(new BlockValidation(block.Index, block.Field, value, true, null));
                continue;
            }

            var (valid, error) = ValidateValue(value, fieldDef);
            if (!valid)
            {
                errors.Add(error);
                blockResults.Add(new BlockValidation(block.Index, block.Field, value, false, error));
            }
            else
            {
                blockResults.Add(new BlockValidation(block.Index, block.Field, value, true, null));
            }
        }

        var isValid = errors.Count == 0;
        var summary = isValid ? string.Empty : string.Join("; ", errors);

        return new ValidationResult(isValid, summary, blockResults);
    }

    public Dictionary<string, string> ParseBlocks(string fileName, FileNameTemplate template)
    {
        var result = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(fileName) || template.Blocks.Count == 0)
            return result;

        var remaining = Path.GetFileNameWithoutExtension(fileName);

        foreach (var block in template.Blocks.OrderBy(b => b.Index))
        {
            var (value, newRemaining) = ApplyParseRule(remaining, block.ParseRule);
            result[block.Field] = value;
            remaining = newRemaining;
        }

        return result;
    }

    public static (string Value, string Remaining) ApplyParseRule(string text, ParseRule rule)
    {
        if (string.IsNullOrEmpty(text))
            return (string.Empty, string.Empty);

        return rule.Mode switch
        {
            ParseMode.DelimiterSegment => ApplyDelimiterSegment(text, rule),
            ParseMode.FixedWidth => ApplyFixedWidth(text, rule),
            ParseMode.BetweenMarkers => ApplyBetweenMarkers(text, rule),
            ParseMode.AfterMarker => ApplyAfterMarker(text, rule),
            ParseMode.Remainder => (text, string.Empty),
            _ => (text, string.Empty)
        };
    }

    private static (string Value, string Remaining) ApplyDelimiterSegment(string text, ParseRule rule)
    {
        var segments = text.Split([rule.Delimiter], StringSplitOptions.None);

        if (rule.SegmentIndex < 0 || rule.SegmentIndex >= segments.Length)
            return (string.Empty, text);

        var value = segments[rule.SegmentIndex];
        var remainingSegments = segments
            .Where((_, i) => i != rule.SegmentIndex)
            .ToArray();

        var remaining = string.Join(rule.Delimiter, remainingSegments);
        return (value, remaining);
    }

    private static (string Value, string Remaining) ApplyFixedWidth(string text, ParseRule rule)
    {
        if (rule.CharCount <= 0)
            return (string.Empty, text);

        if (rule.CharCount >= text.Length)
            return (text, string.Empty);

        var value = text[..rule.CharCount];
        var remaining = text[rule.CharCount..];
        return (value, remaining);
    }

    private static (string Value, string Remaining) ApplyBetweenMarkers(string text, ParseRule rule)
    {
        var openIdx = text.IndexOf(rule.OpenMarker, StringComparison.Ordinal);
        if (openIdx < 0)
            return (string.Empty, text);

        var closeIdx = text.IndexOf(rule.CloseMarker, openIdx + rule.OpenMarker.Length, StringComparison.Ordinal);
        if (closeIdx < 0)
            return (string.Empty, text);

        var valueStart = openIdx + rule.OpenMarker.Length;
        var value = text[valueStart..closeIdx];

        var before = text[..openIdx];
        var after = text[(closeIdx + rule.CloseMarker.Length)..];
        var remaining = before + after;

        return (value, remaining);
    }

    private static (string Value, string Remaining) ApplyAfterMarker(string text, ParseRule rule)
    {
        var idx = text.IndexOf(rule.Marker, StringComparison.Ordinal);
        if (idx < 0)
            return (string.Empty, text);

        var value = text[(idx + rule.Marker.Length)..];
        return (value, string.Empty);
    }

    private static (bool IsValid, string Error) ValidateValue(string value, FieldDefinition fieldDef)
    {
        var mode = fieldDef.ValidationMode;

        if (mode == ValidationMode.None)
            return (true, string.Empty);

        if (mode is ValidationMode.AllowedValues or ValidationMode.AllowedValuesAndCharCount)
        {
            if (fieldDef.AllowedValues.Count > 0)
            {
                var found = fieldDef.AllowedValues.Any(av =>
                    string.Equals(av, value, StringComparison.OrdinalIgnoreCase));
                if (!found)
                {
                    var allowedStr = string.Join(", ", fieldDef.AllowedValues);
                    return (false, $"Value '{value}' is not allowed for field '{fieldDef.Name}'. Expected: {allowedStr}");
                }
            }
        }

        if (mode is ValidationMode.CharCount or ValidationMode.AllowedValuesAndCharCount)
        {
            if (fieldDef.MinLength.HasValue && value.Length < fieldDef.MinLength.Value)
                return (false, $"Value '{value}' for field '{fieldDef.Name}' is too short. Min: {fieldDef.MinLength.Value}, actual: {value.Length}");

            if (fieldDef.MaxLength.HasValue && value.Length > fieldDef.MaxLength.Value)
                return (false, $"Value '{value}' for field '{fieldDef.Name}' is too long. Max: {fieldDef.MaxLength.Value}, actual: {value.Length}");
        }

        return (true, string.Empty);
    }
}
