using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

public interface IFileNameParser
{
    string? TransformForExport(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary);
    (bool IsValid, string ErrorMessage) Validate(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary);
    ValidationResult ValidateDetailed(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary);
    Dictionary<string, string> ParseBlocks(string fileName, FileNameTemplate template);
}
