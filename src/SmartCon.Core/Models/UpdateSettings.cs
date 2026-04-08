namespace SmartCon.Core.Models;

public sealed record UpdateSettings(
    bool CheckOnStartup,
    string? GitHubToken,
    string GitHubOwner,
    string GitHubRepo
)
{
    public static UpdateSettings Default => new(
        CheckOnStartup: true,
        GitHubToken: null,
        GitHubOwner: "Alexandrisius",
        GitHubRepo: "AGK-SmartCon-Pro"
    );
}
