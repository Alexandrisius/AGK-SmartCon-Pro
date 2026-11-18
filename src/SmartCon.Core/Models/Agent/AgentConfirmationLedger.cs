namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Tracks single-use confirmation nonces. A nonce can be consumed exactly once;
/// a replayed approval is rejected before any side effect (L3).
/// </summary>
public sealed class AgentConfirmationLedger
{
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    public bool TryConsume(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return false;

        return _consumed.Add(nonce!);
    }

    public bool IsConsumed(string? nonce) =>
        !string.IsNullOrWhiteSpace(nonce) && _consumed.Contains(nonce!);
}
