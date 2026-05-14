using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public sealed record FamilyExtractionTypeResult(
    string TypeName,
    int SortOrder);

public sealed record FamilyExtractionValueResult(
    string ParameterName,
    AttributeScope? ParameterScope,
    string? StorageType,
    string? ValueText,
    string? ValueRaw,
    double? ValueNumber,
    string? UnitTypeId,
    AttributeValueStatus Status,
    string? Message);

public sealed record FamilyExtractionTypeValues(
    string TypeName,
    int SortOrder,
    IReadOnlyList<FamilyExtractionValueResult> Values);

public sealed record FamilyExtractionResult(
    bool Success,
    IReadOnlyList<FamilyExtractionTypeValues> Types,
    IReadOnlyList<FamilyExtractionValueResult>? UntypedValues,
    string? ErrorMessage,
    int RevitMajorVersion);

public interface IFamilyDataExtractionService
{
    FamilyExtractionResult Extract(string rfaFilePath, IReadOnlyList<string> expectedParameterNames);
}
