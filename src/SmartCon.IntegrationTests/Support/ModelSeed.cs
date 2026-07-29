using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Сидирование тестовой модели в in-memory документе.
/// Все методы вызываются внутри открытой транзакции на Revit-потоке.
///
/// Содержимое дефолтного шаблона проекта зависит от машины (NewProjectDocument
/// использует шаблон из настроек Revit), поэтому методы возвращают null вместо
/// исключения — вызывающий hook обязан превратить это в Skip.Test, а не в падение
/// (пропуск честно сообщает об отсутствующем предусловии, см. revit-test-fixtures).
/// </summary>
internal static class ModelSeed
{
    private const double WallHeight = 10.0;

    public static bool HasWallType(Document document)
    {
        return !new FilteredElementCollector(document)
            .OfClass(typeof(WallType))
            .WhereElementIsElementType()
            .FirstElementId()
            .Equals(ElementId.InvalidElementId);
    }

    public static bool HasPipeTypes(Document document)
    {
        return HasType(document, typeof(PipingSystemType)) && HasType(document, typeof(PipeType));
    }

    public static Level CreateLevel(Document document, double elevation = 0.0)
    {
        return Level.Create(document, elevation);
    }

    public static Wall? TryCreateWall(Document document, ElementId levelId)
    {
        var wallTypeId = FirstTypeId(document, typeof(WallType));
        if (wallTypeId is null)
        {
            return null;
        }

        var line = Line.CreateBound(new XYZ(0, 0, 0), new XYZ(10, 0, 0));
        return Wall.Create(document, line, wallTypeId, levelId, WallHeight, 0.0, false, false);
    }

    public static Pipe? TryCreatePipe(Document document, ElementId levelId, XYZ start, XYZ end)
    {
        var systemTypeId = FirstTypeId(document, typeof(PipingSystemType));
        var pipeTypeId = FirstTypeId(document, typeof(PipeType));
        if (systemTypeId is null || pipeTypeId is null)
        {
            return null;
        }

        return Pipe.Create(document, systemTypeId, pipeTypeId, levelId, start, end);
    }

    /// <summary>Первый коннектор трубы, ближайший к заданной точке (допуск 1e-4 ft).</summary>
    public static Connector FindConnectorAt(Pipe pipe, XYZ point)
    {
        const double tolerance = 1e-4;
        foreach (Connector connector in pipe.ConnectorManager.Connectors)
        {
            if (connector.Origin.DistanceTo(point) < tolerance)
            {
                return connector;
            }
        }

        throw new InvalidOperationException(
            $"У трубы {pipe.Id} не найден коннектор в точке {point} — сидирование модели некорректно.");
    }

    public static int CountInstances<T>(Document document) where T : Element
    {
        return new FilteredElementCollector(document)
            .OfClass(typeof(T))
            .WhereElementIsNotElementType()
            .ToElementIds()
            .Count;
    }

    // ── Виды и ведомости (Share Project) ────────────────────────────────

    public static bool HasFloorPlanViewType(Document document)
    {
        return FindFloorPlanViewTypeId(document) is not null;
    }

    public static ViewPlan CreateViewPlanChecked(Document document, ElementId levelId, string name)
    {
        var viewFamilyTypeId = FindFloorPlanViewTypeId(document)
            ?? throw new InvalidOperationException("ViewFamilyType (FloorPlan) недоступен после проверки HasFloorPlanViewType");

        var viewPlan = ViewPlan.Create(document, viewFamilyTypeId, levelId);
        viewPlan.Name = name;
        return viewPlan;
    }

    public static ViewSchedule CreateScheduleChecked(Document document)
    {
        return ViewSchedule.CreateSchedule(document, new ElementId(BuiltInCategory.OST_Walls));
    }

    private static ElementId? FindFloorPlanViewTypeId(Document document)
    {
        return new FilteredElementCollector(document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan)
            ?.Id;
    }

    // ── Листы (Share Project) ───────────────────────────────────────────

    public static ElementId? FindTitleBlockTypeId(Document document)
    {
        var id = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsElementType()
            .FirstElementId();
        return id.Equals(ElementId.InvalidElementId) ? null : id;
    }

    /// <summary>
    /// Пробник .rfa основной надписи в контент-библиотеке Revit
    /// (C:\ProgramData\Autodesk\RVT {version}\Libraries) — дефолтный шаблон
    /// проекта может не содержать ни одного Title Block.
    /// </summary>
    public static string? FindTitleBlockFamilyFile(string revitMajorVersion)
    {
        var libraries = $@"C:\ProgramData\Autodesk\RVT {revitMajorVersion}\Libraries";
        if (!Directory.Exists(libraries))
        {
            return null;
        }

        return Directory.EnumerateFiles(libraries, "*.rfa", SearchOption.AllDirectories)
            .FirstOrDefault(p => ContainsOrdinalIgnoreCase(p, "Title Block")
                              || ContainsOrdinalIgnoreCase(p, "Надпис"));
    }

    // string.Contains(StringComparison) недоступен на net48, IndexOf запрещён
    // анализатором CA2249 на net8 — кросс-TFM хелпер.
    private static bool ContainsOrdinalIgnoreCase(string value, string part)
    {
#if NET8_0_OR_GREATER
        return value.Contains(part, StringComparison.OrdinalIgnoreCase);
#else
        return value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    private static bool HasType(Document document, Type type)
    {
        return !new FilteredElementCollector(document)
            .OfClass(type)
            .WhereElementIsElementType()
            .FirstElementId()
            .Equals(ElementId.InvalidElementId);
    }

    private static ElementId? FirstTypeId(Document document, Type type)
    {
        var id = new FilteredElementCollector(document)
            .OfClass(type)
            .WhereElementIsElementType()
            .FirstElementId();
        return id.Equals(ElementId.InvalidElementId) ? null : id;
    }
}
