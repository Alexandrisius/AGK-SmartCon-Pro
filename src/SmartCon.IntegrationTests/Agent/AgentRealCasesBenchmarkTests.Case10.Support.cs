using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Classification;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed support of the v2 wave (real-cases benchmark v2, cases 5/10 →
/// Case10/Case15): the classifier text written next to the model, the
/// family-template creators shared by both seeds (FamilySeed-паттерн:
/// NewFamilyDocument из .rft → SaveAs temp → LoadFamily; библиотека .rfa этой
/// машины MEP-семейств не содержит — проверено живым прогоном 2026-11-22,
/// поэтому семейства создаются из Family Templates, которых 1328) and
/// the case 10 verdict helpers (code read-back, JSON parsing, state dump).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    /// <summary>
    /// Классификатор кейса 5 (v2): ~110 строк, 3 уровня, коды «XX.YY.ZZ»,
    /// ветки АР (стены/проёмы/каркас/свет/отделка) и MEP (трубопроводы/
    /// арматура/насосы) + материальная ветка труб (сталь/медь/полимер).
    /// Формат ест парсер ClassifierTextParser: «код название» + строки-дети
    /// с отступом (детали, не отдельные коды).
    /// </summary>
    private const string Case10ClassifierText =
        "# Классификатор проекта «Образец-1» (3 уровня: раздел › подраздел › элемент)\n" +
        "# Формат строки: код название. Строки без кода с отступом — уточнения элемента.\n" +
        "\n" +
        "10 Архитектурные решения\n" +
        "\t10.10 Стены и перегородки\n" +
        "\t\t10.10.10 Стены несущие\n" +
        "\t\t\tСтены наружные несущие\n" +
        "\t\t\tСтены внутренние несущие\n" +
        "\t\t10.10.20 Перегородки\n" +
        "\t\t\tПерегородки кирпичные\n" +
        "\t\t\tПерегородки пазогребневые\n" +
        "\t\t\tПерегородки гипсокартонные\n" +
        "\t\t10.10.30 Элементы стен\n" +
        "\t\t\tБалконные плиты\n" +
        "\t\t\tКарнизы и парапеты\n" +
        "\t10.20 Проёмные элементы\n" +
        "\t\t10.20.10 Двери\n" +
        "\t\t\tДвери однопольные\n" +
        "\t\t\tДвери двупольные\n" +
        "\t\t\tДвери противопожарные\n" +
        "\t\t10.20.20 Окна\n" +
        "\t\t\tОкна с однокамерным стеклопакетом\n" +
        "\t\t\tОкна с двухкамерным стеклопакетом\n" +
        "\t\t10.20.30 Ворота и въезды\n" +
        "\t\t\tВорота откатные\n" +
        "\t\t\tВорота распашные\n" +
        "\t10.30 Каркас\n" +
        "\t\t10.30.10 Колонны\n" +
        "\t\t\tКолонны железобетонные\n" +
        "\t\t\tКолонны стальные\n" +
        "\t\t10.30.20 Балки и ригели\n" +
        "\t\t\tБалки подкрановые\n" +
        "\t\t\tРигели каркасные\n" +
        "\t\t10.30.90 Каркас прочий\n" +
        "\t10.40 Освещение\n" +
        "\t\t10.40.10 Светильники\n" +
        "\t\t\tСветильники растровые встроенные\n" +
        "\t\t\tСветильники накладные\n" +
        "\t\t\tСветильники точечные\n" +
        "\t\t10.40.20 Источники света\n" +
        "\t\t\tЛампы светодиодные\n" +
        "\t\t\tЛампы люминесцентные\n" +
        "\t10.50 Отделка\n" +
        "\t\t10.50.10 Полы\n" +
        "\t\t10.50.20 Отделка стен\n" +
        "\t\t10.50.30 Потолки подвесные\n" +
        "\t\t10.50.40 Окраска и штукатурка\n" +
        "\t10.60 Кровля и фасады\n" +
        "\t\t10.60.10 Кровля\n" +
        "\t\t10.60.20 Фасады\n" +
        "\n" +
        "20 Отделочные работы\n" +
        "\t20.10 Временные здания и сооружения\n" +
        "\t20.20 Леса и подмости\n" +
        "\t20.90 Прочие временные работы\n" +
        "\n" +
        "30 Инженерные системы\n" +
        "\t30.10 Трубопроводы\n" +
        "\t\t30.10.10 Трубы стальные\n" +
        "\t\t\tТрубы стальные водогазопроводные\n" +
        "\t\t\tТрубы стальные электросварные\n" +
        "\t\t\tТрубы стальные оцинкованные\n" +
        "\t\t30.10.20 Трубы медные\n" +
        "\t\t\tТрубы медные отожжённые\n" +
        "\t\t\tТрубы медные неотожжённые\n" +
        "\t\t30.10.30 Трубы полимерные\n" +
        "\t\t\tТрубы из ПВХ\n" +
        "\t\t\tТрубы полипропиленовые\n" +
        "\t\t\tТрубы полиэтиленовые\n" +
        "\t\t30.10.90 Трубы прочие\n" +
        "\t30.20 Трубопроводная арматура\n" +
        "\t\t30.20.10 Краны шаровые\n" +
        "\t\t\tКраны шаровые муфтовые\n" +
        "\t\t\tКраны шаровые фланцевые\n" +
        "\t\t\tКраны шаровые седельные\n" +
        "\t\t30.20.20 Задвижки\n" +
        "\t\t\tЗадвижки чугунные\n" +
        "\t\t\tЗадвижки стальные клиновые\n" +
        "\t\t\tЗадвижки шиберные\n" +
        "\t\t30.20.30 Фильтры сетчатые\n" +
        "\t\t\tФильтры сетчатые латунные муфтовые\n" +
        "\t\t\tФильтры сетчатые чугунные фланцевые\n" +
        "\t\t30.20.40 Обратные клапаны\n" +
        "\t\t30.20.50 Краны вентильные\n" +
        "\t\t\tКраны вентильные запорные\n" +
        "\t\t30.20.90 Арматура прочая\n" +
        "\t30.30 Насосное оборудование\n" +
        "\t\t30.30.10 Насосы\n" +
        "\t\t\tНасосы циркуляционные\n" +
        "\t\t\tНасосы консольные\n" +
        "\t\t\tНасосы погружные\n" +
        "\t\t30.30.20 Насосные установки\n" +
        "\t\t\tУстановки повышения давления\n" +
        "\t30.40 Теплообменное оборудование\n" +
        "\t\t30.40.10 Теплообменники\n" +
        "\t30.50 Вентиляция\n" +
        "\t\t30.50.10 Вентиляторы\n" +
        "\t\t30.50.20 Воздуховоды\n" +
        "\t30.60 Электротехнические системы\n" +
        "\t\t30.60.10 Щиты и шкафы\n" +
        "\t\t30.60.20 Кабельные конструкции\n" +
        "\n" +
        "40 Технологическое оборудование\n" +
        "\t40.10 Ёмкости и резервуары\n" +
        "\t40.20 Подъёмно-транспортное оборудование\n" +
        "\n" +
        "90 Прочее\n" +
        "\t90.10 Общие требования\n" +
        "\t90.90 Позиции вне классификации";

    /// <summary>
    /// 8 семейств кейса 5 (v2): family-шаблон установки, имя семейства,
    /// имена типоразмеров и ожидаемый код классификатора (истина сида).
    /// Семейства создаются из .rft (библиотека .rfa машины MEP-семейств не
    /// содержит), семантика классификации живёт в имени семейства/типа.
    /// «Кран шаровой» — ДВА типоразмера (одинаковый код в КАЖДЫЙ); вилка
    /// кодов-обманок в классификаторе — 30.20.50 «Краны вентильные».
    /// </summary>
    private static (string Label, string[] Templates, string[] Types, string ExpectedCode)[] Case10FamilySpecs() =>
        new[]
        {
            ("Кран шаровой", new[] { "Metric Plumbing Fixture.rft" }, new[] { "Ду15", "Ду25" }, "30.20.10"),
            ("Задвижка", new[] { "Metric Plumbing Fixture.rft" }, new[] { "Ду50" }, "30.20.20"),
            ("Насос", new[] { "Metric Mechanical Equipment.rft" }, new[] { "К 20-30" }, "30.30.10"),
            ("Фильтр сетчатый", new[] { "Metric Plumbing Fixture.rft" }, new[] { "Ду40" }, "30.20.30"),
            ("Дверь", new[] { "Metric Door.rft", "Метрическая система, дверь.rft" }, new[] { "ДГ 21-7" }, "10.20.10"),
            ("Окно", new[] { "Metric Window.rft", "Метрическая система, окно.rft" }, new[] { "ОР 15-12" }, "10.20.20"),
            ("Колонна", new[] { "Metric Column.rft" }, new[] { "400x400" }, "10.30.10"),
            ("Светильник", new[] { "Metric Lighting Fixture.rft" }, new[] { "ЛПО 2x36" }, "10.40.10")
        };

    /// <summary>Ожидаемые коды трёх типоразмеров системной трубы (истина сида).</summary>
    private static readonly string[] Case10PipeTypeNames = { "Сталь", "Медь", "ПВХ" };

    private static string Case10PipeCodeOf(string typeName) => typeName switch
    {
        "Сталь" => "30.10.10",
        "Медь" => "30.10.20",
        _ => "30.10.30"
    };

    // ── Создание семейств из family-шаблонов (FamilySeed-паттерн) ────────

    /// <summary>
    /// Создание модельного семейства из family-шаблона установки:
    /// NewFamilyDocument из .rft → переименование/создание типоразмеров →
    /// SaveAs temp .rfa (имя файла = имя семейства в проекте) → LoadFamily.
    /// Семейство пустое (без геометрии) — кейсу нужны имя/категория/типы;
    /// MEP-коннекторов у шаблонных семейств НЕТ. Отсутствие шаблона → null →
    /// вызывающий превращает это в сид-ошибку/скип. Вызывать в транзакции.
    /// </summary>
    private static RevitFamily? LoadBenchFamilyFromTemplate(
        Document doc,
        string familyName,
        string[] typeNames,
        params string[] templateNames)
    {
        if (typeNames.Length == 0)
            return null;

        var root = $@"C:\ProgramData\Autodesk\RVT {doc.Application.VersionNumber}\Family Templates";
        if (!Directory.Exists(root))
            return null;

        string? template;
        try
        {
            template = Directory
                .EnumerateFiles(root, "*.rft", SearchOption.AllDirectories)
                .FirstOrDefault(path => templateNames.Any(name =>
                    string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)));
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

            using (var transaction = new Transaction(familyDoc, "Seed bench family types"))
            {
                transaction.Start();
                var manager = familyDoc.FamilyManager;
                if (manager.CurrentType is not null)
                    manager.RenameCurrentType(typeNames[0]);
                else
                    manager.NewType(typeNames[0]);
                foreach (var typeName in typeNames.Skip(1))
                    manager.NewType(typeName);
                if (transaction.Commit() != TransactionStatus.Committed)
                    return null;
            }

            var directory = Path.Combine(Path.GetTempPath(), "SmartConBenchSeeds");
            Directory.CreateDirectory(directory);
            var rfaPath = Path.Combine(directory, familyName + ".rfa");
            if (File.Exists(rfaPath))
                File.Delete(rfaPath);

            familyDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            return doc.LoadFamily(rfaPath, out var family) ? family : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                "PROBE bench family template load failed for '" + familyName + "': " +
                ex.GetType().Name + ": " + ex.Message);
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

    private static ElementId? BenchFirstTypeId(Document doc, Type type) =>
        new FilteredElementCollector(doc)
            .OfClass(type)
            .WhereElementIsElementType()
            .FirstElementId() is { } id && !id.Equals(ElementId.InvalidElementId) ? id : null;

    // ── Case10: the same classifier as the table of the document ─────────

    private const string Case10TableFileName = "classifier-table.txt";

    private List<AgentClassificationEntry> _case10ClassifierRows = null!;

    private int _case10TableEntries;

    private string? _case10TablePath;

    /// <summary>
    /// Гейт сида без LLM: таблица документа несёт весь классификатор — по
    /// строке на код (строке таблицы нужен код) и с читаемыми названиями.
    /// </summary>
    private async Task Case10AssertClassifierTable()
    {
        var table = AgentClassificationReader.Read(Doc);
        await Assert.That(table.Loaded).IsTrue();
        await Assert.That(table.Entries.Count).IsGreaterThanOrEqualTo(50);
        await Assert.That(table.Entries.Count).IsEqualTo(_case10ClassifierCodes.Count);
        await Assert.That(table.Source).IsEqualTo(_case10TablePath);
        foreach (var row in _case10ClassifierRows)
        {
            var loaded = table.Find(row.Code);
            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.Description).IsEqualTo(row.Description);
        }

        foreach (var code in _case10Types.Select(item => item.ExpectedCode).Distinct())
        {
            await Assert.That(table.Find(code)).IsNotNull();
            await Assert.That(table.Find(code)!.IsLeaf).IsTrue();
        }
    }

    /// <summary>
    /// Loads the seed classifier into the document as its classification
    /// table — the state of a real project, where the classifier is connected
    /// to the model and the «Код по классификатору» picker shows it. The rows
    /// come from the classifier the seed already parsed; the file is the table
    /// format of Revit's own export: four tab-separated columns, UTF-16 (this
    /// Revit reads the Russian names only in that encoding).
    /// </summary>
    private void Case10LoadClassifierTable()
    {
        try
        {
            var path = Path.Combine(
                Path.GetDirectoryName(_case10ClassifierPath!)!, Case10TableFileName);
            File.WriteAllBytes(
                path,
                AgentClassificationFile.EncodeUtf16(AgentClassificationFile.Format(_case10ClassifierRows)));

            AgentClassificationLoader.Result? result = null;
            _transactions!.RunInTransaction(Doc, "Seed case 10 (classification table)", doc =>
            {
                result = AgentClassificationLoader.Load(doc, path);
            });

            var table = AgentClassificationReader.Read(Doc);
            _case10TablePath = path;
            _case10TableEntries = table.Entries.Count;
            if (result is null
                || result.Status is not (AgentClassificationLoader.Status.Loaded
                                          or AgentClassificationLoader.Status.AlreadyCurrent)
                || table.Entries.Count != _case10ClassifierCodes.Count)
            {
                _case10SeedError = "Таблица классификации не загрузилась в документ сида (" +
                                   (result is null ? "нет результата" : result.Status.ToString()) + ": " +
                                   string.Join("; ", result?.Problems ?? Array.Empty<string>()) +
                                   "; записей " + table.Entries.Count + " из " + _case10ClassifierCodes.Count + ")";
            }
        }
        catch (Exception ex)
        {
            _case10SeedError = "Не удалось загрузить таблицу классификации: " +
                               ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static FamilySymbol? BenchFirstSymbolOf(RevitFamily family, Document doc)
    {
        var symbolId = family.GetFamilySymbolIds().FirstOrDefault();
        return symbolId is null ? null : doc.GetElement(symbolId) as FamilySymbol;
    }

    // ── Case10: чтение состояния для вердикта/дампа ─────────────────────

    /// <summary>Текущий Assembly Code типоразмера из сида (null = пустой).</summary>
    private string? Case10CodeOf(Case10TypeExpectation item)
    {
        if (Doc.GetElement(ElementIdCompat.Create(item.TypeId)) is not ElementType type)
            return null;

        var raw = type.get_Parameter(BuiltInParameter.UNIFORMAT_CODE)?.AsString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>Число позиций классификатора в результате read_classifier (data.total).</summary>
    private static int Case10ItemsOf(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty("data", out var data) &&
                   data.TryGetProperty("total", out var total) &&
                   total.ValueKind == JsonValueKind.Number
                ? total.GetInt32()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>net48-безопасный подсчёт вхождений подстроки.</summary>
    private static int Case10CountOccurrences(string text, string fragment)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(fragment, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += fragment.Length;
        }

        return count;
    }

    private string DumpCase10State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Типоразмер | Код сейчас | Ожидание |");
        builder.AppendLine("|---|---|---|");
        foreach (var item in _case10Types)
            builder.AppendLine("| " + item.Label + " | " + (Case10CodeOf(item) ?? "(пусто)") +
                              " | " + item.ExpectedCode + " |");

        return builder.ToString();
    }
}
