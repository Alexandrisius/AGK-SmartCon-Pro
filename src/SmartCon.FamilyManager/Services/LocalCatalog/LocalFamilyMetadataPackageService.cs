using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyMetadataPackageService : IFamilyMetadataPackageService
{
    private readonly ICategoryRepository _categoryRepository;
    private readonly IAttributeDefinitionRepository _attributeRepository;
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly LocalCatalogDatabase _database;
    private readonly LocalCatalogMigrator _migrator;
    private string? _migratedDbPath;

    public LocalFamilyMetadataPackageService(
        ICategoryRepository categoryRepository,
        IAttributeDefinitionRepository attributeRepository,
        ICategoryAttributeBindingService bindingService,
        LocalCatalogDatabase database,
        LocalCatalogMigrator migrator)
    {
        _categoryRepository = categoryRepository;
        _attributeRepository = attributeRepository;
        _bindingService = bindingService;
        _database = database;
        _migrator = migrator;
    }

    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        var currentPath = _database.GetDatabaseRoot();
        if (_migratedDbPath == currentPath) return;
        await _migrator.MigrateAsync(ct);
        _migratedDbPath = currentPath;
    }

    public async Task<FamilyMetadataPackage> ExportCategoriesAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        var categories = await _categoryRepository.GetAllAsync(ct);
        var tree = new CategoryTree(categories);
        var rootNodes = BuildExportTree(tree, null);

        return new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true, Attributes = false, Bindings = false },
            Categories = rootNodes
        };
    }

    public async Task<FamilyMetadataPackage> ExportAttributesAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        var attributes = await _attributeRepository.GetAllAsync(ct);
        var metadataAttributes = attributes.Select(a => new FamilyMetadataAttribute
        {
            Name = a.Name,
            Group = a.Group
        }).ToList();

        return new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = false, Attributes = true, Bindings = false },
            Attributes = metadataAttributes
        };
    }

    public async Task<FamilyMetadataPackage> ExportFullAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        var categories = await _categoryRepository.GetAllAsync(ct);
        var tree = new CategoryTree(categories);
        var rootNodes = BuildExportTree(tree, null);

        var attributes = await _attributeRepository.GetAllAsync(ct);
        var metadataAttributes = attributes.Select(a => new FamilyMetadataAttribute
        {
            Name = a.Name,
            Group = a.Group
        }).ToList();

        var attrById = attributes.ToDictionary(a => a.Id, a => a, StringComparer.OrdinalIgnoreCase);

        var bindings = new List<FamilyMetadataBinding>();
        foreach (var cat in categories)
        {
            var directBindings = await _bindingService.GetDirectBindingsAsync(cat.Id, ct);
            foreach (var binding in directBindings)
            {
                var catPath = tree.BuildFullPath(binding.CategoryId);
                var attrName = attrById.TryGetValue(binding.AttributeId, out var attr)
                    ? attr.Name
                    : null;

                if (attrName is null) continue;

                bindings.Add(new FamilyMetadataBinding
                {
                    CategoryPath = catPath,
                    AttributeName = attrName,
                    SortOrder = binding.SortOrder,
                    IsEnabled = binding.IsEnabled
                });
            }
        }

        return new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true, Attributes = true, Bindings = true },
            Categories = rootNodes,
            Attributes = metadataAttributes,
            Bindings = bindings
        };
    }

    public async Task<FamilyMetadataImportResult> ImportAsync(FamilyMetadataPackage package, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        var categoriesImported = 0;
        var attributesImported = 0;
        var bindingsImported = 0;
        var bindingsSkipped = 0;
        var warnings = new List<string>();

        if (package.Sections.Categories && package.Categories.Count > 0)
        {
            var flatNodes = FlattenCategoryTree(package.Categories, null, 0);
            await _categoryRepository.ReplaceAllAsync(flatNodes, ct);
            categoriesImported = flatNodes.Count;
        }

        if (package.Sections.Attributes)
        {
            foreach (var attr in package.Attributes)
            {
                var exists = await _attributeRepository.NameExistsAsync(attr.Name, null, ct);
                if (exists)
                {
                    warnings.Add($"Attribute '{attr.Name}' already exists, skipped.");
                    continue;
                }

                await _attributeRepository.CreateAsync(attr.Name, attr.Group, ct);
                attributesImported++;
            }
        }

        if (package.Sections.Bindings)
        {
            var allCategories = await _categoryRepository.GetAllAsync(ct);
            var pathToCategory = allCategories.ToDictionary(c => c.FullPath, c => c, StringComparer.OrdinalIgnoreCase);

            var allAttributes = await _attributeRepository.GetAllAsync(ct);
            var nameToAttr = allAttributes.ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);

            var importedPaths = FlattenCategoryPaths(package.Categories, "");
            var pathLookup = importedPaths.ToDictionary(p => p.Path, p => p.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var binding in package.Bindings)
            {
                if (!pathToCategory.TryGetValue(binding.CategoryPath, out var category))
                {
                    warnings.Add($"Binding skipped: category '{binding.CategoryPath}' not found.");
                    bindingsSkipped++;
                    continue;
                }

                if (!nameToAttr.TryGetValue(binding.AttributeName, out var attribute))
                {
                    warnings.Add($"Binding skipped: attribute '{binding.AttributeName}' not found.");
                    bindingsSkipped++;
                    continue;
                }

                var existingBindings = await _bindingService.GetDirectBindingsAsync(category.Id, ct);
                if (existingBindings.Any(b => b.AttributeId == attribute.Id))
                {
                    warnings.Add($"Binding skipped: '{binding.AttributeName}' already bound to '{binding.CategoryPath}'.");
                    bindingsSkipped++;
                    continue;
                }

                await _bindingService.CreateBindingAsync(category.Id, attribute.Id, binding.SortOrder, ct);
                bindingsImported++;
            }
        }

        return new FamilyMetadataImportResult
        {
            CategoriesImported = categoriesImported,
            AttributesImported = attributesImported,
            BindingsImported = bindingsImported,
            BindingsSkipped = bindingsSkipped,
            Warnings = warnings.AsReadOnly()
        };
    }

    private static List<FamilyMetadataCategoryNode> BuildExportTree(CategoryTree tree, string? parentId)
    {
        var result = new List<FamilyMetadataCategoryNode>();
        foreach (var node in tree.GetChildren(parentId))
        {
            result.Add(new FamilyMetadataCategoryNode
            {
                Name = node.Name,
                Children = BuildExportTree(tree, node.Id)
            });
        }
        return result;
    }

    private static List<CategoryNode> FlattenCategoryTree(List<FamilyMetadataCategoryNode> nodes, string? parentId, int startOrder)
    {
        var result = new List<CategoryNode>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var id = Guid.NewGuid().ToString();
            result.Add(new CategoryNode(id, node.Name, parentId, startOrder + i, node.Name, DateTimeOffset.UtcNow));
            if (node.Children.Count > 0)
            {
                var childPathPrefix = node.Name;
                result.AddRange(FlattenCategoryTreeWithParentPath(node.Children, id, 0, childPathPrefix));
            }
        }
        return result;
    }

    private static List<CategoryNode> FlattenCategoryTreeWithParentPath(List<FamilyMetadataCategoryNode> nodes, string parentId, int startOrder, string parentPath)
    {
        var result = new List<CategoryNode>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var id = Guid.NewGuid().ToString();
            var fullPath = $"{parentPath} > {node.Name}";
            result.Add(new CategoryNode(id, node.Name, parentId, startOrder + i, fullPath, DateTimeOffset.UtcNow));
            if (node.Children.Count > 0)
            {
                result.AddRange(FlattenCategoryTreeWithParentPath(node.Children, id, 0, fullPath));
            }
        }
        return result;
    }

    private static List<(string Path, string Name)> FlattenCategoryPaths(List<FamilyMetadataCategoryNode> nodes, string parentPath)
    {
        var result = new List<(string Path, string Name)>();
        foreach (var node in nodes)
        {
            var fullPath = string.IsNullOrEmpty(parentPath) ? node.Name : $"{parentPath} > {node.Name}";
            result.Add((fullPath, node.Name));
            if (node.Children.Count > 0)
            {
                result.AddRange(FlattenCategoryPaths(node.Children, fullPath));
            }
        }
        return result;
    }
}
