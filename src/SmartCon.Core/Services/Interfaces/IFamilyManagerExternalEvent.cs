namespace SmartCon.Core.Services.Interfaces;

public interface IFamilyManagerExternalEvent
{
    void Raise(Action action);

    void RaiseWithApplication(Action<object> actionWithApp);
}
