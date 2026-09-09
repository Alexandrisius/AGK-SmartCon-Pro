using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Compatibility;

namespace SmartCon.Revit.FamilyManager;

public static partial class SystemCategoryRegistry
{
    private static Element? PlacePipe(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not PipeType pipeType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place pipe", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .First();
            created = Pipe.Create(d, sysType.Id, pipeType.Id, level.Id, start, end);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>
    /// <see cref="FlexPipe.Create"/> требует <see cref="FlexPipeType"/> и массив
    /// точек сплайна с касательными — обычный <see cref="PlacePipe"/> не подходит.
    /// </summary>
    private static Element? PlaceFlexPipe(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not FlexPipeType flexPipeType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place flex pipe", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .First();
            var points = new List<XYZ> { start, end };
            var tangent = XYZ.BasisX;
            created = FlexPipe.Create(d, sysType.Id, flexPipeType.Id, level.Id, tangent, tangent, points);
        }))
        {
            return null;
        }
        return created;
    }

    private static Element? PlaceDuct(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not DuctType ductType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place duct", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            created = Duct.Create(d, sysType.Id, ductType.Id, level.Id, start, end);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>Зеркалит <see cref="PlaceFlexPipe"/> для воздуховодов.</summary>
    private static Element? PlaceFlexDuct(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not FlexDuctType flexDuctType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place flex duct", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            var points = new List<XYZ> { start, end };
            var tangent = XYZ.BasisX;
            created = FlexDuct.Create(d, sysType.Id, flexDuctType.Id, level.Id, tangent, tangent, points);
        }))
        {
            return null;
        }
        return created;
    }

    private static Element? PlaceConduit(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        var assembly = doc.GetType().Assembly;
        var conduitType = assembly.GetType("Autodesk.Revit.DB.Electrical.ConduitType");
        var conduit = assembly.GetType("Autodesk.Revit.DB.Electrical.Conduit");
        if (conduitType is null || conduit is null) return null;
        if (!conduitType.IsInstanceOfType(type)) return null;

        var createMethod = conduit.GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Document), typeof(ElementId), typeof(XYZ), typeof(XYZ), typeof(ElementId) },
            null);
        if (createMethod is null) return null;

        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place conduit", d =>
        {
            try
            {
                created = createMethod.Invoke(null, new object[] { d, type.Id, start, end, level.Id }) as Element;
            }
            catch (TargetInvocationException tex)
            {
                SmartConLogger.Warn($"{tex.InnerException?.Message ?? tex.Message} [Action: проверьте, что тип короба/лотка валиден для двухточечного размещения]");
            }
        }))
        {
            return null;
        }
        return created;
    }

    private static Element? PlaceCableTray(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        var assembly = doc.GetType().Assembly;
        var cableTrayType = assembly.GetType("Autodesk.Revit.DB.Electrical.CableTrayType");
        var cableTray = assembly.GetType("Autodesk.Revit.DB.Electrical.CableTray");
        if (cableTrayType is null || cableTray is null) return null;
        if (!cableTrayType.IsInstanceOfType(type)) return null;

        var createMethod = cableTray.GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Document), typeof(ElementId), typeof(XYZ), typeof(XYZ), typeof(ElementId) },
            null);
        if (createMethod is null) return null;

        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place cable tray", d =>
        {
            try
            {
                created = createMethod.Invoke(null, new object[] { d, type.Id, start, end, level.Id }) as Element;
            }
            catch (TargetInvocationException tex)
            {
                SmartConLogger.Warn($"{tex.InnerException?.Message ?? tex.Message} [Action: проверьте, что тип короба/лотка валиден для двухточечного размещения]");
            }
        }))
        {
            return null;
        }
        return created;
    }

    // ── Стены (Phase 1) ─────────────────────────────────────────────────

    /// <summary>
    /// Изоляция трубы. <c>PipeInsulation.Create</c> требует host
    /// (труба/фитинг/аксессуар) — в мини-проекте изоляции хоста нет,
    /// поэтому в той же транзакции размещается метровая труба первого
    /// шаблонного типа (базовые типы системных семейств присутствуют в любом
    /// проекте и неудаляемы — инвариант продукта). Хост безвреден для
    /// extraction/хэша: они фильтруют по категории изоляций.
    /// </summary>
    private static Element? PlacePipeInsulation(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not PipeInsulationType insulationType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place pipe insulation", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .First();
            var pipeType = new FilteredElementCollector(d)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .First();
            var pipe = Pipe.Create(d, sysType.Id, pipeType.Id, level.Id, start, end);
            var thicknessFt = RevitUnitsCompat.MetersToInternal(0.025);
            created = PipeInsulation.Create(d, pipe.Id, insulationType.Id, thicknessFt);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>Зеркалит <see cref="PlacePipeInsulation"/> для воздуховодов.</summary>
    private static Element? PlaceDuctInsulation(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not DuctInsulationType insulationType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place duct insulation", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            var ductType = new FilteredElementCollector(d)
                .OfClass(typeof(DuctType))
                .Cast<DuctType>()
                .First();
            var duct = Duct.Create(d, sysType.Id, ductType.Id, level.Id, start, end);
            var thicknessFt = RevitUnitsCompat.MetersToInternal(0.025);
            created = DuctInsulation.Create(d, duct.Id, insulationType.Id, thicknessFt);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>
    /// Футеровка воздуховода (#197). <c>DuctLining.Create</c> требует host
    /// (воздуховод/фитинг/аксессуар) — как и у наружной изоляции, в той же
    /// транзакции размещается метровый воздуховод первого шаблонного типа.
    /// </summary>
    private static Element? PlaceDuctLining(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not DuctLiningType liningType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place duct lining", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            var ductType = new FilteredElementCollector(d)
                .OfClass(typeof(DuctType))
                .Cast<DuctType>()
                .First();
            var duct = Duct.Create(d, sysType.Id, ductType.Id, level.Id, start, end);
            var thicknessFt = RevitUnitsCompat.MetersToInternal(0.025);
            created = DuctLining.Create(d, duct.Id, liningType.Id, thicknessFt);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>
    /// Провод (#199). <c>Wire.Create</c> (Since 2015) требует id вида —
    /// только план этажа или RCP. В мини-проекте плана может не быть →
    /// переиспользуем существующий план уровня или создаём
    /// (<c>ViewPlan.Create</c> по ViewFamilyType с ViewFamily.FloorPlan).
    /// <c>WiringType.Chamfer</c> — полилиния через все вершины; коннекторы
    /// null (свободный провод).
    /// </summary>
    private static Element? PlaceWire(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not WireType wireType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place wire", d =>
        {
            var view = FindOrCreateFloorPlanView(d, level);
            if (view is null) return;
            created = Wire.Create(d, wireType.Id, view.Id, WiringType.Chamfer,
                new List<XYZ> { start, end }, null, null);
        }))
        {
            return null;
        }
        return created;
    }
}
