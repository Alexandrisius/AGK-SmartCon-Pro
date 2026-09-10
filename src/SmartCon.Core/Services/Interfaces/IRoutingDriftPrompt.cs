namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// ADR-072 World B placement prompt: when placing a system type whose LIVE
/// routing preferences differ from the catalog's item-level routing links,
/// the user must explicitly confirm the overwrite (the catalog import
/// changes the project's routing settings). <c>true</c> — apply the catalog
/// routing and continue; <c>false</c> — cancel the placement entirely.
/// </summary>
public interface IRoutingDriftPrompt
{
    bool ConfirmRoutingOverwrite(string typeName);
}
