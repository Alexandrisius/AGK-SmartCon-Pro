using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

public interface IFileNameParser
{
    string? TransformStatus(string fileName, FileNameTemplate template);
    (bool IsValid, string ErrorMessage) Validate(string fileName, FileNameTemplate template);
    Dictionary<string, string> ParseBlocks(string fileName, FileNameTemplate template);
}
