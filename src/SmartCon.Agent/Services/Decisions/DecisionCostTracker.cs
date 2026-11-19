namespace SmartCon.Agent.Services.Decisions;

/// <summary>
/// Accumulates the USD cost reported by decision-model responses
/// (<c>usage.cost</c>) so the session counter can add it to the chat-model
/// cost. The Decisions API reports the exact charged amount per request.
/// </summary>
public sealed class DecisionCostTracker
{
    private readonly object _gate = new();
    private decimal _totalUsd;

    public decimal TotalUsd
    {
        get
        {
            lock (_gate)
                return _totalUsd;
        }
    }

    /// <summary>Raised with the cost of one request, in USD.</summary>
    public event Action<decimal>? CostAdded;

    public void Add(decimal usd)
    {
        if (usd <= 0m)
            return;

        lock (_gate)
            _totalUsd += usd;

        CostAdded?.Invoke(usd);
    }
}