namespace SmartCon.Core.Models;

public sealed record BlockValidation(
    int Index,
    string Field,
    string Value,
    bool IsValid,
    string? Error
);

public sealed record ValidationResult(
    bool IsValid,
    string Summary,
    List<BlockValidation> Blocks
);
