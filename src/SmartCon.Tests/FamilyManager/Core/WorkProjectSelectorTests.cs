using SmartCon.Core.Services.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Core;

public sealed class WorkProjectSelectorTests
{
    private static OpenDocumentInfo Work(string path) =>
        new(path, IsFamilyDocument: false, IsLinked: false, IsMiniProject: false);

    private static OpenDocumentInfo Mini(string path) =>
        new(path, IsFamilyDocument: false, IsLinked: false, IsMiniProject: true);

    private static OpenDocumentInfo Family(string path) =>
        new(path, IsFamilyDocument: true, IsLinked: false, IsMiniProject: false);

    [Fact]
    public void SelectWorkProjectPath_WorkProjectPresent_ReturnsIt()
    {
        var docs = new[]
        {
            Mini(@"C:\storage\db1\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt"),
            Work(@"D:\Projects\Tower.rvt"),
        };

        var result = WorkProjectSelector.SelectWorkProjectPath(
            docs, @"C:\storage\db1\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt");

        Assert.Equal(@"D:\Projects\Tower.rvt", result);
    }

    [Fact]
    public void SelectWorkProjectPath_OnlyMiniProjectsOpen_ReturnsNull()
    {
        var docs = new[]
        {
            Mini(@"C:\storage\db1\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt"),
            Mini(@"C:\storage\db1\files\bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\v2\Стены.rvt"),
        };

        var result = WorkProjectSelector.SelectWorkProjectPath(
            docs, @"C:\storage\db1\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt");

        Assert.Null(result);
    }

    [Fact]
    public void SelectWorkProjectPath_FamilyAndMiniOnly_ReturnsNull()
    {
        var docs = new[]
        {
            Family(@"C:\storage\db1\files\cccccccccccccccccccccccccccccccc\v1\Отвод.rfa"),
            Mini(@"C:\storage\db1\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt"),
        };

        var result = WorkProjectSelector.SelectWorkProjectPath(
            docs, @"C:\storage\db1\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt");

        Assert.Null(result);
    }

    [Fact]
    public void SelectWorkProjectPath_ReferencePathItself_NeverSelected()
    {
        var reference = @"C:\storage\db1\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt";
        var docs = new[]
        {
            new OpenDocumentInfo(reference, IsFamilyDocument: false, IsLinked: false, IsMiniProject: false),
        };

        var result = WorkProjectSelector.SelectWorkProjectPath(docs, reference);

        Assert.Null(result);
    }

    [Fact]
    public void SelectWorkProjectPath_LinkedDocument_Skipped()
    {
        var docs = new[]
        {
            new OpenDocumentInfo(@"D:\Shared\Link.rvt", IsFamilyDocument: false, IsLinked: true, IsMiniProject: false),
            Work(@"D:\Projects\Tower.rvt"),
        };

        var result = WorkProjectSelector.SelectWorkProjectPath(docs, null);

        Assert.Equal(@"D:\Projects\Tower.rvt", result);
    }

    [Fact]
    public void SelectWorkProjectPath_UnsavedDocument_Skipped()
    {
        var docs = new[]
        {
            new OpenDocumentInfo(string.Empty, IsFamilyDocument: false, IsLinked: false, IsMiniProject: false),
            Work(@"D:\Projects\Tower.rvt"),
        };

        var result = WorkProjectSelector.SelectWorkProjectPath(docs, null);

        Assert.Equal(@"D:\Projects\Tower.rvt", result);
    }

    [Fact]
    public void SelectWorkProjectPath_UnmarkedDocumentMatchingReferencePath_Skipped()
    {
        // A document that is NOT marked as a mini-project but shares the
        // reference path must still never be re-activated as "work project".
        var reference = @"C:\storage\db1\files\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\v1\Трубы.rvt";
        var docs = new[]
        {
            new OpenDocumentInfo(reference, IsFamilyDocument: false, IsLinked: false, IsMiniProject: false),
            Work(@"D:\Projects\Tower.rvt"),
        };

        var result = WorkProjectSelector.SelectWorkProjectPath(docs, reference);

        Assert.Equal(@"D:\Projects\Tower.rvt", result);
    }

    [Fact]
    public void SelectWorkProjectPath_PathComparison_CaseInsensitive()
    {
        var docs = new[]
        {
            new OpenDocumentInfo(@"C:\STORAGE\Mini.rvt", IsFamilyDocument: false, IsLinked: false, IsMiniProject: false),
        };

        var result = WorkProjectSelector.SelectWorkProjectPath(docs, @"c:\storage\mini.rvt");

        Assert.Null(result);
    }

    [Fact]
    public void SelectWorkProjectPath_EmptyList_ReturnsNull()
    {
        var result = WorkProjectSelector.SelectWorkProjectPath([], null);

        Assert.Null(result);
    }
}
