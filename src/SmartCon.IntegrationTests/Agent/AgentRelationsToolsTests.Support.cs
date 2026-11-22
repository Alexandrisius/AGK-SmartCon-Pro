using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed helpers of the relations tool tests (#260: split out of the main file).
/// Library/template loaders follow the ModelSeed.FindTitleBlockFamilyFile
/// pattern: a missing Revit library returns null so the test can skip.
/// </summary>
public sealed partial class AgentRelationsToolsTests
{
    private static FamilySymbol? FirstFamilySymbol(Document doc, BuiltInCategory category) =>
        new FilteredElementCollector(doc)
            .OfCategory(category)
            .WhereElementIsElementType()
            .Cast<FamilySymbol>()
            .FirstOrDefault();

    /// <summary>
    /// Загрузка первого подходящего семейства из библиотеки установки Revit
    /// (паттерн ModelSeed.FindTitleBlockFamilyFile): MEP-шаблон не содержит
    /// дверей/окон. Отсутствие библиотеки/семейства → null → скип-гард.
    /// Вызывать внутри транзакции.
    /// </summary>
    private static FamilySymbol? LoadSymbolFromLibrary(Document doc, params string[] markers)
    {
        var root = $@"C:\ProgramData\Autodesk\RVT {doc.Application.VersionNumber}\Libraries";
        if (!Directory.Exists(root))
            return null;

        string? file;
        try
        {
            file = Directory
                .EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories)
                .FirstOrDefault(path => markers.Any(marker => ContainsOrdinalIgnoreCase(path, marker)));
        }
        catch (Exception)
        {
            return null;
        }

        if (file is null)
            return null;

        try
        {
            if (!doc.LoadFamily(file, out var family) || family is null)
                return null;

            var symbolId = family.GetFamilySymbolIds().FirstOrDefault();
            return symbolId is null ? null : doc.GetElement(symbolId) as FamilySymbol;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Семейство из family-шаблона установки (FamilySeed-паттерн): NewFamilyDocument
    /// из .rft → SaveAs temp .rfa → LoadFamily в проект. Точные имена шаблонов
    /// двери/окна (EN/RU), чтобы не поймать Tag-шаблоны. Вызывать в транзакции.
    /// </summary>
    private static FamilySymbol? LoadSymbolFromFamilyTemplate(
        Document doc, string exactEnglish, string exactRussian)
    {
        var root = $@"C:\ProgramData\Autodesk\RVT {doc.Application.VersionNumber}\Family Templates";
        if (!Directory.Exists(root))
            return null;

        string? template;
        try
        {
            template = Directory
                .EnumerateFiles(root, "*.rft", SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), exactEnglish, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(path), exactRussian, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }

        if (template is null)
            return null;

        Document? familyDoc = null;
        try
        {
            familyDoc = doc.Application.NewFamilyDocument(template);
            if (familyDoc is null)
                return null;

            var directory = Path.Combine(Path.GetTempPath(), "SmartConRelationsTests");
            Directory.CreateDirectory(directory);
            var rfaPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(template) + ".rfa");
            if (File.Exists(rfaPath))
                File.Delete(rfaPath);

            familyDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            if (!doc.LoadFamily(rfaPath, out var family) || family is null)
                return null;

            var symbolId = family.GetFamilySymbolIds().FirstOrDefault();
            return symbolId is null ? null : doc.GetElement(symbolId) as FamilySymbol;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                "PROBE AgentRelations family template load failed: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
        finally
        {
            try
            {
                if (familyDoc is not null && familyDoc.IsValidObject)
                    familyDoc.Close(false);
            }
            catch (Exception)
            {
                // Уже закрыт — ничего не делать.
            }
        }
    }

    private static bool ContainsOrdinalIgnoreCase(string value, string fragment) =>
        value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

    private static ElementId? FirstTypeId(Document doc, Type type) =>
        new FilteredElementCollector(doc)
            .OfClass(type)
            .WhereElementIsElementType()
            .FirstElementId() is { } id && !id.Equals(ElementId.InvalidElementId) ? id : null;

    private static ElementId? SecondPipingSystemTypeId(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(PipingSystemType))
            .WhereElementIsElementType()
            .Cast<PipingSystemType>()
            .Select(systemType => systemType.Id)
            .Skip(1)
            .FirstOrDefault();

    private static ElementId? GetSystemId(Pipe pipe) =>
        pipe.MEPSystem?.Id;

    private static void RenameSystem(Document doc, Pipe pipe, string name)
    {
        var systemId = GetSystemId(pipe);
        if (systemId is null)
            return;

        RenameElement(doc, doc.GetElement(systemId), name);
    }

    private static void RenameElement(Document doc, Element element, string name)
    {
        if (element is null)
            return;

        try
        {
            element.Name = name;
        }
        catch (ArgumentException)
        {
            Console.WriteLine("PROBE AgentRelations: rename to '" + name + "' rejected");
        }
    }

    private static string? ReadSystemNameOf(Document doc, Element element)
    {
        var value = element.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
