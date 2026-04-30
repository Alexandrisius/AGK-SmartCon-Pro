namespace SmartCon.Core.Services.Interfaces;

public interface IRevitFileInfoReader
{
    int? ReadRevitVersion(string filePath);
}
