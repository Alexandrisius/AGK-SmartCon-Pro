using SmartCon.Core.Services.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Core;

public sealed class MiniProjectPathPatternTests
{
    [Theory]
    [InlineData(@"C:\Users\user\AppData\Roaming\SmartCon\FamilyManager\default\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt")]
    [InlineData(@"D:\Bases\project-db\files\AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\v12\Стены.rvt")]
    [InlineData(@"D:\Bases\project-db\files\aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\v2\Крыша.rvt")]
    public void IsMiniProjectPath_ManagedStorageShape_ReturnsTrue(string path)
    {
        Assert.True(MiniProjectPathPattern.IsMiniProjectPath(path));
    }

    [Theory]
    [InlineData(@"D:\Projects\Tower.rvt")]
    [InlineData(@"C:\Users\user\AppData\Roaming\SmartCon\FamilyManager\default\catalog.db")]
    [InlineData(@"D:\Bases\project-db\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Отвод.rfa")]
    [InlineData(@"D:\Bases\project-db\files\shortid\v1\Трубы.rvt")]
    [InlineData(@"D:\Bases\project-db\backups\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt")]
    [InlineData(@"D:\Bases\project-db\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\Трубы.rvt")]
    [InlineData(@"\\server\share\Projects\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Вложенная\Трубы.rvt")]
    public void IsMiniProjectPath_NonMatchingShapes_ReturnsFalse(string path)
    {
        Assert.False(MiniProjectPathPattern.IsMiniProjectPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsMiniProjectPath_NullOrEmpty_ReturnsFalse(string? path)
    {
        Assert.False(MiniProjectPathPattern.IsMiniProjectPath(path));
    }
}
