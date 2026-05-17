using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

public sealed class RevitUserIdentityService : IUserIdentityService
{
    private readonly IRevitContext _revitContext;

    public RevitUserIdentityService(IRevitContext revitContext)
    {
        _revitContext = revitContext;
    }

    public UserIdentity GetCurrentUser()
    {
        var userName = Environment.UserName;
        var machineName = Environment.MachineName;
        var userId = $"{userName}@{machineName}";

        string displayName;
        try
        {
            displayName = _revitContext.GetUsername();
        }
        catch
        {
            displayName = userName;
        }

        return new UserIdentity(userId, displayName, machineName, userName);
    }
}
