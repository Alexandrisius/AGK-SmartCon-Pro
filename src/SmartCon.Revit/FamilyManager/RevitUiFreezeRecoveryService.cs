using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

/// <inheritdoc cref="IUiFreezeRecoveryService"/>
public sealed class RevitUiFreezeRecoveryService : IUiFreezeRecoveryService
{
    public void Nudge(string message)
    {
        RevitBalloonNudge.Nudge(message);
    }
}
