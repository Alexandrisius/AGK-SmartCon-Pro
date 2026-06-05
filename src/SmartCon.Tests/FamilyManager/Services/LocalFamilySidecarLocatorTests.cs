using System.IO;
using System.Reflection;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public sealed class LocalFamilySidecarLocatorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LocalFamilySidecarLocator _sut;

    public LocalFamilySidecarLocatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"SidecarLocator_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _sut = new LocalFamilySidecarLocator();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string CreateRfa(string relativePath)
    {
        var full = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "FAKE_RFA_CONTENT");
        return full;
    }

    private string CreateTxt(string relativePath, string content = "FAKE_TXT_CONTENT")
    {
        var full = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void FindSidecarPath_TxtExists_ReturnsPath()
    {
        var rfa = CreateRfa("Family1.rfa");
        var txt = CreateTxt("Family1.txt");

        var result = _sut.FindSidecarPath(rfa);

        Assert.Equal(txt, result);
    }

    [Fact]
    public void FindSidecarPath_TxtMissing_ReturnsNull()
    {
        var rfa = CreateRfa("Loner.rfa");

        var result = _sut.FindSidecarPath(rfa);

        Assert.Null(result);
    }

    [Fact]
    public void FindSidecarPath_EmptyInput_ReturnsNull()
    {
        Assert.Null(_sut.FindSidecarPath(""));
        Assert.Null(_sut.FindSidecarPath("   "));
        Assert.Null(_sut.FindSidecarPath(null));
    }

    [Fact]
    public void FindSidecarPath_OriginalNotExists_ReturnsNull()
    {
        var ghost = Path.Combine(_tempRoot, "Ghost.rfa");

        var result = _sut.FindSidecarPath(ghost);

        Assert.Null(result);
    }

    [Fact]
    public void FindSidecarPath_CaseInsensitiveExtensionMatch_ReturnsPath()
    {
        // .RFA uppercase + .TXT uppercase.
        // Windows file system is case-insensitive: the path we get back will
        // use the casing from the file system entry we actually created.
        var rfa = CreateRfa("Mixed.RFA");
        var txt = CreateTxt("Mixed.TXT");

        var result = _sut.FindSidecarPath(rfa);

        Assert.NotNull(result);
        Assert.Equal(Path.GetDirectoryName(txt), Path.GetDirectoryName(result));
        Assert.Equal(
            Path.GetFileName(txt),
            Path.GetFileName(result),
            ignoreCase: true);
    }

    [Fact]
    public void FindSidecarPath_CaseInsensitiveBasename_ReturnsPath()
    {
        // rfa "MyFamily" + txt "myfamily.txt" — same case-insensitive name
        var rfa = CreateRfa("MyFamily.rfa");
        var txt = CreateTxt("myfamily.txt");

        var result = _sut.FindSidecarPath(rfa);

        Assert.NotNull(result);
        Assert.Equal(Path.GetDirectoryName(txt), Path.GetDirectoryName(result));
        Assert.Equal(
            Path.GetFileName(txt),
            Path.GetFileName(result),
            ignoreCase: true);
    }

    [Fact]
    public void FindSidecarPath_DifferentName_NotMatched()
    {
        var rfa = CreateRfa("Real.rfa");
        CreateTxt("Other.txt");

        var result = _sut.FindSidecarPath(rfa);

        Assert.Null(result);
    }

    [Fact]
    public async Task CopySidecarAsync_Success_ReturnsDestPath()
    {
        var source = CreateTxt("Source.txt", "CONTENT");
        var destDir = Path.Combine(_tempRoot, "out");

        var result = await _sut.CopySidecarAsync(source, destDir);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(destDir, "Source.txt"), result);
        Assert.True(File.Exists(result));
        Assert.Equal("CONTENT", await File.ReadAllTextAsync(result));
    }

    [Fact]
    public async Task CopySidecarAsync_SourceNotExists_ReturnsNull()
    {
        var destDir = Path.Combine(_tempRoot, "out");

        var result = await _sut.CopySidecarAsync(Path.Combine(_tempRoot, "missing.txt"), destDir);

        Assert.Null(result);
    }

    [Fact]
    public async Task CopySidecarAsync_EmptyArgs_ReturnsNull()
    {
        var destDir = Path.Combine(_tempRoot, "out");

        Assert.Null(await _sut.CopySidecarAsync("", destDir));
        Assert.Null(await _sut.CopySidecarAsync(null!, destDir));
        Assert.Null(await _sut.CopySidecarAsync("x.txt", ""));
        Assert.Null(await _sut.CopySidecarAsync("x.txt", null!));
    }

    [Fact]
    public async Task CopySidecarAsync_CreatesDestDirIfMissing()
    {
        var source = CreateTxt("src.txt");
        var destDir = Path.Combine(_tempRoot, "nested", "deep", "out");

        var result = await _sut.CopySidecarAsync(source, destDir);

        Assert.NotNull(result);
        Assert.True(Directory.Exists(destDir));
        Assert.True(File.Exists(result));
    }

    [Fact]
    public async Task CopySidecarAsync_OverwritesExistingDest()
    {
        var source = CreateTxt("src.txt", "NEW");
        var destDir = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(destDir);
        var destFile = Path.Combine(destDir, "src.txt");
        await File.WriteAllTextAsync(destFile, "OLD");

        var result = await _sut.CopySidecarAsync(source, destDir);

        Assert.NotNull(result);
        Assert.Equal("NEW", await File.ReadAllTextAsync(result));
    }

    [Fact]
    public async Task CopySidecarAsync_Cancelled_Throws()
    {
        var source = CreateTxt("src.txt");
        var destDir = Path.Combine(_tempRoot, "out");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.CopySidecarAsync(source, destDir, cts.Token));
    }
}
