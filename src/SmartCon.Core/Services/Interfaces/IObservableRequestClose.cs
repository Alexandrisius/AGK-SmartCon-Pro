namespace SmartCon.Core.Services.Interfaces;

public interface IObservableRequestClose
{
    event Action<bool?>? RequestClose;
}
