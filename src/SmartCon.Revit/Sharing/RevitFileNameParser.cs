using SmartCon.Core.Models;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Sharing;

public sealed class RevitFileNameParser : IFileNameParser
{
    private readonly FileNameParser _inner = new();

    public string? TransformStatus(string fileName, FileNameTemplate template)
        => _inner.TransformStatus(fileName, template);

    public (bool IsValid, string ErrorMessage) Validate(string fileName, FileNameTemplate template)
        => _inner.Validate(fileName, template);

    public Dictionary<string, string> ParseBlocks(string fileName, FileNameTemplate template)
        => _inner.ParseBlocks(fileName, template);
}
