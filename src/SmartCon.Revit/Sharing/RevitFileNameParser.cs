using SmartCon.Core.Models;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Sharing;

public sealed class RevitFileNameParser : IFileNameParser
{
    private readonly FileNameParser _inner = new();

    public string? TransformForExport(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary)
        => _inner.TransformForExport(fileName, template, fieldLibrary);

    public (bool IsValid, string ErrorMessage) Validate(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary)
        => _inner.Validate(fileName, template, fieldLibrary);

    public Dictionary<string, string> ParseBlocks(string fileName, FileNameTemplate template)
        => _inner.ParseBlocks(fileName, template);

    public ValidationResult ValidateDetailed(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary)
        => _inner.ValidateDetailed(fileName, template, fieldLibrary);
}
