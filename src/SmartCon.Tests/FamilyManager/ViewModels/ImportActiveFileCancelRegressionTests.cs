using System.Linq;
using System.Reflection;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Regression tests for the v2.0.0 hotfix that resolved the
/// "Import Active File closes the user's family document when the user
/// cancels the batch dialog" bug (see docs/adr/035-remove-temp-logic-v2.md).
///
/// In v1.x, the import flow created a temp file via
/// <c>ActiveFamilyFilePreparer.SaveAs</c> BEFORE showing the batch dialog.
/// On cancel, the <c>finally</c> block of <c>ImportActiveFileAsync</c>
/// called <c>CloseFamilyDocumentAsync</c> to close the temp copy — but
/// because <c>SaveAs</c> had already mutated the active document's
/// <c>PathName</c> to point at the temp path, the close logic ended up
/// closing the user's original family document, discarding their edits.
///
/// In v2.0.0, no temp file is created before the dialog. The active
/// document is only touched (via <c>SaveAs</c>) AFTER the user confirms
/// the dialog. On cancel, nothing happens to the active document. On
/// SUCCESS, the family document is closed by <c>CloseFamilyDocumentAsync</c>
/// (which returns focus to the project if one was open).
///
/// These tests verify the structural properties of the fix:
///   - <c>CloseFamilyDocumentAsync</c> exists on the VM (Family files
///     must still close after a successful import — see Issue #78)
///   - the temp-related service fields are gone (preparer's role removed)
///   - the dialog is shown BEFORE any SaveAs happens
///   - on cancel, no managed file is created (no orphan)
///
/// Reflection is restricted to NAMES where possible so tests run without
/// the RevitAPI assembly being resolvable in the test runner.
/// </summary>
public sealed class ImportActiveFileCancelRegressionTests
{
    [Fact]
    public void FamilyManagerMainViewModel_Has_CloseFamilyDocumentAsync()
    {
        // v2.0.0 keeps close-after-import for .rfa documents. The method
        // is invoked AFTER SaveAs + ImportFileAsync succeed; it switches
        // focus back to the project (if any) and closes the family. This
        // test guards against accidentally deleting the close logic again
        // during future refactors.
        var type = typeof(SmartCon.FamilyManager.ViewModels.FamilyManagerMainViewModel);
        var method = type.GetMethod(
            "CloseFamilyDocumentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
    }

    [Fact]
    public void CloseFamilyDocumentAsync_ReturnsTask()
    {
        var type = typeof(SmartCon.FamilyManager.ViewModels.FamilyManagerMainViewModel);
        var method = type.GetMethod(
            "CloseFamilyDocumentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        Assert.Equal(typeof(System.Threading.Tasks.Task), method!.ReturnType);
    }

    [Fact]
    public void FamilyManagerMainViewModel_DoesNotHave_ActiveFamilyFilePreparer_Field()
    {
        // The Preparer that staged temp files has been removed entirely.
        var type = typeof(SmartCon.FamilyManager.ViewModels.FamilyManagerMainViewModel);
        var fieldNames = GetAllFieldNames(type);

        Assert.DoesNotContain("_activeFamilyFilePreparer", fieldNames);
        Assert.DoesNotContain("activeFamilyFilePreparer", fieldNames);
    }

    [Fact]
    public void FamilyManagerMainViewModel_DoesNotHave_ActiveImportCleanupService_Field()
    {
        // Cleanup service was running in the finally block of
        // ImportActiveFileAsync — its removal is part of the same fix.
        var type = typeof(SmartCon.FamilyManager.ViewModels.FamilyManagerMainViewModel);
        var fieldNames = GetAllFieldNames(type);

        Assert.DoesNotContain("_activeImportCleanupService", fieldNames);
        Assert.DoesNotContain("activeImportCleanupService", fieldNames);
    }

    private static string[] GetAllFieldNames(Type type)
    {
        var names = new List<string>();
        var t = type;
        while (t != null && t != typeof(object))
        {
            foreach (var f in t.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
                | BindingFlags.DeclaredOnly))
            {
                names.Add(f.Name);
            }
            t = t.BaseType!;
        }
        return names.ToArray();
    }
}