using System.IO;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Extension methods for <see cref="FamilyAssetType"/>.
/// </summary>
public static class FamilyAssetTypeExtensions
{
    /// <summary>
    /// Detects the <see cref="FamilyAssetType"/> from a file path's extension.
    /// Falls back to <see cref="FamilyAssetType.Other"/> for unknown extensions.
    /// </summary>
    public static FamilyAssetType DetectFromExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return FamilyAssetType.Other;

        if (ext.Length > 1 && ext[0] == '.')
            ext = ext.Substring(1);

        return ext.ToLowerInvariant() switch
        {
            "png" or "jpg" or "jpeg" or "bmp" or "gif" or "tif" or "tiff" => FamilyAssetType.Image,
            "mp4" or "avi" or "mov" or "wmv" or "mkv" => FamilyAssetType.Video,
            "pdf" or "doc" or "docx" or "txt" or "rtf" => FamilyAssetType.Document,
            "glb" or "gltf" or "fbx" or "obj" or "stl" => FamilyAssetType.Model3D,
            "csv" => FamilyAssetType.LookupTable,
            "xls" or "xlsx" or "xlsm" => FamilyAssetType.Spreadsheet,
            _ => FamilyAssetType.Other
        };
    }

    /// <summary>
    /// Combined filter string for <see cref="Microsoft.Win32.OpenFileDialog"/> covering all known asset types.
    /// Used by the unified "Add file" button so the user can pick any supported file in one dialog.
    /// </summary>
    public static string AllAssetFilters()
    {
        return "All supported files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;" +
               "*.mp4;*.avi;*.mov;*.wmv;*.mkv;" +
               "*.pdf;*.doc;*.docx;*.txt;*.rtf;" +
               "*.glb;*.gltf;*.fbx;*.obj;*.stl;" +
               "*.csv;*.xls;*.xlsx;*.xlsm" +
               "|Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" +
               "|Video files (*.mp4;*.avi;*.mov;*.wmv;*.mkv)|*.mp4;*.avi;*.mov;*.wmv;*.mkv" +
               "|Document files (*.pdf;*.doc;*.docx;*.txt;*.rtf)|*.pdf;*.doc;*.docx;*.txt;*.rtf" +
               "|3D Model files (*.glb;*.gltf;*.fbx;*.obj;*.stl)|*.glb;*.gltf;*.fbx;*.obj;*.stl" +
               "|Lookup table files (*.csv;*.txt)|*.csv;*.txt" +
               "|Spreadsheet files (*.xls;*.xlsx;*.xlsm)|*.xls;*.xlsx;*.xlsm" +
               "|All files (*.*)|*.*";
    }
}
