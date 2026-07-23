namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One Revit-variant row of a catalog version label (ADR-054, actualization
/// engine). The content is identical across variants of the same label —
/// the engine opens ONE variant and tasks apply results to ALL of them.
/// </summary>
public sealed record ActualizationVariant(
    string VersionId,
    string FileId,
    int RevitMajorVersion,
    string RelativePath,
    string FileName);
