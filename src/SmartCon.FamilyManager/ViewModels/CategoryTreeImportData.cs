namespace SmartCon.FamilyManager.ViewModels;

public sealed class CategoryTreeImportData
{
    public int Version { get; set; } = 1;
    public List<CategoryImportItem>? Categories { get; set; }

    public sealed class CategoryImportItem
    {
        public string Name { get; set; } = string.Empty;
        public List<CategoryImportItem>? Children { get; set; }
    }
}
