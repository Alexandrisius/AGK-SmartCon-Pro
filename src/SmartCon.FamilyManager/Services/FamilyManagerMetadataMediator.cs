namespace SmartCon.FamilyManager.Services;

public interface IFamilyManagerMetadataMediator
{
    event Action? MetadataChanged;
    void RaiseMetadataChanged();
}

public sealed class FamilyManagerMetadataMediator : IFamilyManagerMetadataMediator
{
    public event Action? MetadataChanged;

    public void RaiseMetadataChanged() => MetadataChanged?.Invoke();
}
