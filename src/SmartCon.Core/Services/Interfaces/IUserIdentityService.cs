using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IUserIdentityService
{
    UserIdentity GetCurrentUser();
}
