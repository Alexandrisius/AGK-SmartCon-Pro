using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Seeds a small model family (two types, instance/type parameters, a formula)
/// from an installed .rft template, saves it as .rfa and loads it into the test
/// project. Used by the family editing pack integration tests (H1d).
/// </summary>
internal sealed class FamilySeed : IDisposable
{
    public const string WidthParam = "Ширина";
    public const string HeightParam = "Высота";
    public const string CodeParam = "Марка";
    public const string CountParam = "Количество";
    public const string FlagParam = "Флаг";
    public const string AreaParam = "Площадь";
    public const string SizeParam = "Размер";
    public const string PressureParam = "Давление";
    public const string InstanceParam = "Примечание_экз";
    public const string Type1 = "Тип1";
    public const string Type2 = "Тип2";
    public const double Type1WidthMm = 1000.0;
    public const double Type2WidthMm = 1500.0;

    private FamilySeed()
    {
    }

    public required Document Project { get; init; }

    public required string FamilyName { get; init; }

    public required string RfaPath { get; init; }

    /// <summary>
    /// Current ElementId of the family. LoadFamily may replace the family element,
    /// so the id is resolved by name on every read.
    /// </summary>
    public long FamilyId
    {
        get
        {
            var collector = new FilteredElementCollector(Project).OfClass(typeof(RevitFamily));
            foreach (var element in collector)
            {
                if (element is RevitFamily family &&
                    string.Equals(family.Name, FamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    return family.Id.GetValue();
                }
            }

            return -1;
        }
    }

    public static FamilySeed? Create(
        Application app,
        Document project,
        string familyName,
        out string? skipReason)
    {
        skipReason = null;
        var template = SampleFiles.FindFamilyTemplate(app);
        if (template is null)
        {
            skipReason = "В установленном Revit нет подходящего .rft шаблона семейства";
            return null;
        }

        var directory = Path.Combine(Path.GetTempPath(), "SmartConFamilyTests");
        Directory.CreateDirectory(directory);
        var rfaPath = Path.Combine(directory, familyName + ".rfa");
        if (File.Exists(rfaPath))
            File.Delete(rfaPath);

        Document? familyDoc = null;
        try
        {
            familyDoc = app.NewFamilyDocument(template);
            if (familyDoc is null)
            {
                skipReason = "Revit не создал семейство из шаблона " + template;
                return null;
            }

            using (var transaction = new Transaction(familyDoc, "Seed family"))
            {
                transaction.Start();
                var manager = familyDoc.FamilyManager;

                var width = manager.AddParameter(
                    WidthParam, GroupTypeId.General, SpecTypeId.Length, false);
                var height = manager.AddParameter(
                    HeightParam, GroupTypeId.General, SpecTypeId.Length, false);
                var area = manager.AddParameter(
                    AreaParam, GroupTypeId.General, SpecTypeId.Area, false);
                var size = manager.AddParameter(
                    SizeParam, GroupTypeId.General, SpecTypeId.Length, false);
                var code = manager.AddParameter(
                    CodeParam, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                var count = manager.AddParameter(
                    CountParam, GroupTypeId.General, SpecTypeId.Int.Integer, false);
                var flag = manager.AddParameter(
                    FlagParam, GroupTypeId.General, SpecTypeId.Boolean.YesNo, false);
                manager.AddParameter(
                    PressureParam, GroupTypeId.General, SpecTypeId.HvacPressure, false);
                manager.AddParameter(
                    InstanceParam, GroupTypeId.IdentityData, SpecTypeId.String.Text, true);

                FamilyType type1;
                if (manager.CurrentType is not null)
                {
                    manager.RenameCurrentType(Type1);
                    type1 = manager.CurrentType!;
                }
                else
                {
                    type1 = manager.NewType(Type1);
                }

                var type2 = manager.NewType(Type2);

                manager.CurrentType = type1;
                manager.Set(width, Type1WidthMm / 304.8);
                manager.Set(height, 2000.0 / 304.8);
                manager.Set(code, "A-1");
                manager.Set(count, 1);
                manager.Set(flag, 1);
                manager.Set(size, 500.0 / 304.8);

                manager.CurrentType = type2;
                manager.Set(width, Type2WidthMm / 304.8);
                manager.Set(height, 2100.0 / 304.8);
                manager.Set(code, "B-2");
                manager.Set(count, 2);
                manager.Set(flag, 0);
                manager.Set(size, 750.0 / 304.8);

                manager.SetFormula(area, WidthParam + " * " + HeightParam);
                transaction.Commit();
            }

            familyDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            var family = familyDoc.LoadFamily(project, new OverwriteFamilyLoadOptions());
            if (family is null)
            {
                skipReason = "Revit не загрузил сидированное семейство в проект";
                return null;
            }

            familyDoc.Close(false);
            familyDoc = null;

            return new FamilySeed
            {
                Project = project,
                FamilyName = familyName,
                RfaPath = rfaPath
            };
        }
        catch (Exception ex)
        {
            skipReason = "Не удалось сидировать семейство: " + ex.GetType().Name + ": " +
                         ex.Message;
            return null;
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
            {
                try
                {
                    familyDoc.Close(false);
                }
                catch (Exception)
                {
                    // The document is already closed or invalid — nothing to do.
                }
            }
        }
    }

    /// <summary>Reads the raw parameter value (internal units) of a project family type.</summary>
    public Parameter? GetProjectParameter(string typeName, string parameterName)
    {
        RevitFamily? family = null;
        var familyCollector = new FilteredElementCollector(Project).OfClass(typeof(RevitFamily));
        foreach (var element in familyCollector)
        {
            if (element is RevitFamily candidate &&
                string.Equals(candidate.Name, FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                family = candidate;
                break;
            }
        }

        if (family is null)
            return null;

        foreach (var typeId in family.GetFamilySymbolIds())
        {
            if (Project.GetElement(typeId) is not FamilySymbol symbol)
                continue;

            if (!string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase))
                continue;

            return symbol.LookupParameter(parameterName);
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(RfaPath))
                File.Delete(RfaPath);
        }
        catch (Exception)
        {
            // Temp file cleanup is best-effort.
        }
    }
}
/// <summary>Overwrite-on-load options used by the family test seeds.</summary>
internal sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        overwriteParameterValues = true;
        return true;
    }

    public bool OnSharedFamilyFound(
        RevitFamily sharedFamily,
        bool familyInUse,
        out FamilySource source,
        out bool overwriteParameterValues)
    {
        source = FamilySource.Family;
        overwriteParameterValues = true;
        return true;
    }
}
