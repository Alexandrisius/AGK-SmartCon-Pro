namespace SmartCon.Core.Models;

/// <summary>User-configurable settings for the GitHub-based auto-update system.</summary>
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
