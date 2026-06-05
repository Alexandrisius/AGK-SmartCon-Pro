namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Single source of truth for system-family temp-path layout.
///
/// Both the staging .rvt producer (<c>SystemFamilyRevitOperations.CreateCleanProjectWithTypesAndInstances</c>)
/// and the cleanup consumer (<c>ActiveImportCleanupService</c>) MUST derive paths from these constants —
/// any other value risks a stranded .rvt (race between cleanup and save).
///
/// Layout (under <see cref="System.IO.Path.GetTempPath"/>):
/// <code>
/// %TEMP%\SmartCon\SystemFamilyLoadFromProject\&lt;GUID&gt;\&lt;safeName&gt;.rvt
/// </code>
/// Per-import GUID sub-folder guarantees:
/// <list type="bullet">
///   <item>no collisions between concurrent imports;</item>
///   <item>safe recursive deletion of the whole sub-folder by cleanup.</item>
/// </list>
/// </summary>
public static class SystemFamilyTempLayout
{
    /// <summary>Root temp folder shared by all SmartCon staging artefacts.</summary>
    public const string TempRoot = "SmartCon";

    /// <summary>Sub-folder for staged system-family .rvt files (one GUID sub-folder per import).</summary>
    public const string StagingSubdir = "SystemFamilyLoadFromProject";
}
