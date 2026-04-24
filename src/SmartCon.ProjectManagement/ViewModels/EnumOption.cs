namespace SmartCon.ProjectManagement.ViewModels;

public sealed class EnumOption<T>
{
    public T Value { get; init; } = default!;
    public string Display { get; init; } = "";
    public string Description { get; init; } = "";

    public override string ToString() => Display;
}
