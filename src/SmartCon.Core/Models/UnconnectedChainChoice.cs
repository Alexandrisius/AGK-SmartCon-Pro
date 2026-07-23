namespace SmartCon.Core.Models;

/// <summary>
/// User's choice in the "unconnected chain elements" confirmation dialog shown
/// when Connect is pressed while part of the network chain is still detached.
/// </summary>
public enum UnconnectedChainChoice
{
    /// <summary>Attach all remaining chain elements, then finish the connect.</summary>
    ConnectAll,

    /// <summary>Finish the connect as-is, leaving the rest of the chain detached.</summary>
    ConnectAsIs,

    /// <summary>Return to the editor without connecting.</summary>
    GoBack,
}
