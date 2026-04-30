namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Type of auxiliary asset attached to a family catalog item.
/// </summary>
public enum FamilyAssetType
{
    /// <summary>Preview image, thumbnail, specification image.</summary>
    Image = 0,

    /// <summary>Video instruction, tutorial.</summary>
    Video = 1,

    /// <summary>PDF specification, instruction, changelog.</summary>
    Document = 2,

    /// <summary>3D model preview: FBX, OBJ, etc.</summary>
    Model3D = 3,

    /// <summary>Lookup table: CSV, TXT size lookup.</summary>
    LookupTable = 4,

    /// <summary>Other file type.</summary>
    Other = 5
}
