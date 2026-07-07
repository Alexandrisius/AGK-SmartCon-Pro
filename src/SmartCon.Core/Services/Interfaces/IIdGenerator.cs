namespace SmartCon.Core.Services.Interfaces;

public interface IIdGenerator
{
    string NewId();
    string NewId(string format);
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public string NewId() => Guid.NewGuid().ToString();
    public string NewId(string format) => Guid.NewGuid().ToString(format);
}
