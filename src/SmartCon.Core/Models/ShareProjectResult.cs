namespace SmartCon.Core.Models;

public sealed record ShareProjectResult
{
    public bool Success { get; init; }
    public string SharedFilePath { get; init; } = string.Empty;
    public double ElapsedSeconds { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public int ElementsDeleted { get; init; }
    public int PurgedElementsCount { get; init; }

    public static ShareProjectResult Failed(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };
}
