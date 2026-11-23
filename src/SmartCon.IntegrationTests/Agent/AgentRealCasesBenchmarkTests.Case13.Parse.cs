using System.Text.RegularExpressions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Answer-text parsing of case 13 (ORCH-4, P2-1/P3-2 of review r1): the
/// strict segment summary parse with per-pair qualifiers («N из M» per
/// parameter, «заполнено K из M, пустых N из M», «не заполнено N из M») and
/// the exact culprit-set check. All truth numbers and family names are
/// derived from the seed truth helpers of the Case13 core partial.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    /// <summary>
    /// Стемы-псевдонимы параметров сида для атрибуции строк сводки
    /// (словарные основы русских названий; канонические имена — константы
    /// сида Case13*Parameter).
    /// </summary>
    private static readonly (Guid Guid, string[] Stems)[] Case13SummaryStems =
    {
        (Case13NameGuid, new[] { "наименован" }),
        (Case13MarkGuid, new[] { "марк", "марок" }),
        (Case13UnitGuid, new[] { "единиц", "ед.изм", "ед. изм" }),
        (Case13CountGuid, new[] { "количеств" })
    };

    /// <summary>
    /// Сводка в ответе модели совпадает с истиной сида (ORCH-4: ручная сводка
    /// без audit_fill_rate). Строгий разбор: текст делится на строки, строки —
    /// на сегменты (, ;), сегмент засчитывается, если называет РОВНО ОДИН
    /// параметр и несёт пару «N из M» / «N/M» с M = размеру истины; N — число
    /// пустых. «12 из 12» — это 12, а не «2 из 12» подстрокой. Обе формы
    /// сводки (P2-1 рецензии r1): «N из M» по параметру и «заполнено K из M,
    /// пустых N из M» — квалификатор при паре см. <see cref="Case13SegmentEmptyCount"/>.
    /// По истины сида: Name = имя дырок, Mark = марки дырок, Unit = единицы
    /// дырок (или 0 после безопасного заполнения), Count = 0.
    /// </summary>
    internal static bool Case13AnswerSummaryMatchesTruth(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var (total, nameEmpty, markEmpty, unitEmpty) = Case13TruthShape();
        var counts = new Dictionary<Guid, int>();
        foreach (var rawLine in text.Split('\n'))
        {
            foreach (var rawSegment in rawLine.Split(',', ';'))
            {
                var segment = rawSegment.Trim();
                if (segment.Length == 0)
                    continue;

                var parameter = Case13SegmentParameter(segment);
                if (parameter is null)
                    continue;

                var empty = Case13SegmentEmptyCount(segment, total);
                if (empty is not null)
                    counts[parameter.Value] = empty.Value;
            }
        }

        return counts.TryGetValue(Case13NameGuid, out var name) && name == nameEmpty &&
               counts.TryGetValue(Case13MarkGuid, out var mark) && mark == markEmpty &&
               counts.TryGetValue(Case13UnitGuid, out var unit) &&
               (unit == unitEmpty || unit == 0) &&
               counts.TryGetValue(Case13CountGuid, out var count) && count == 0;
    }

    /// <summary>
    /// Параметр сида, названный в сегменте; null — не назван или названо
    /// несколько (неоднозначный сегмент не засчитывается).
    /// </summary>
    private static Guid? Case13SegmentParameter(string segment)
    {
        Guid? found = null;
        foreach (var (guid, stems) in Case13SummaryStems)
        {
            if (!stems.Any(stem => ContainsText(segment, stem)))
                continue;

            if (found is not null && found != guid)
                return null;

            found = guid;
        }

        return found;
    }

    /// <summary>
    /// Пара «N из M» / «N/M» (лат. of тоже принимается) сегмента, где
    /// M = размеру истины; возвращается число ПУСТЫХ. Числа берутся целыми
    /// токенами: «12 из 12» даёт N=12, а не 2. Квалификатор пары (P2-1
    /// рецензии r1) привязывается ПОСЛОВНО — словом «пуст…» (или «не
    /// заполнено…») при паре: пустых N; словом «заполн…» (или «не пустых»):
    /// пустых M−N («заполнено 10 из 12» = 2 пустых). Пара без
    /// квалификатора — N (буква кейса: «сколько пустых из скольких»), поэтому
    /// неотбрившийся квалификатор («1 из 12 (заполнено 11)» — число при
    /// слове относится к другой паре) оставляет корректную голую пару.
    /// Явное «пустых» приоритетнее; противоречие «заполнено»/«пустых»
    /// (заполнено ≠ M − пустых) и два разных «пустых N» бракуют сегмент.
    /// </summary>
    private static int? Case13SegmentEmptyCount(string segment, int total)
    {
        var matches = Regex.Matches(
            segment,
            @"(\d+)\s*(?:из|/|of)\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int? emptyExplicit = null;
        int? filledCount = null;
        int? bare = null;
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (!int.TryParse(match.Groups[1].Value, out var number) ||
                !int.TryParse(match.Groups[2].Value, out var of) ||
                of != total)
                continue;

            // Контекст пары — текст между соседними парами: квалификатор
            // соседней пары не затекает в эту.
            var beforeStart = index == 0
                ? 0
                : matches[index - 1].Index + matches[index - 1].Length;
            var before = segment.Substring(beforeStart, match.Index - beforeStart);
            var afterEnd = index + 1 < matches.Count
                ? matches[index + 1].Index
                : segment.Length;
            var after = segment.Substring(
                match.Index + match.Length, afterEnd - match.Index - match.Length);

            var sign = Case13PairQualifierSign(before, after);
            if (sign < 0)
            {
                if (emptyExplicit is not null && emptyExplicit != number)
                    return null;

                emptyExplicit = number;
            }
            else if (sign > 0)
            {
                if (filledCount is not null && filledCount != number)
                    return null;

                filledCount = number;
            }
            else if (bare is null)
            {
                bare = number;
            }
        }

        if (emptyExplicit is not null)
            return filledCount is not null && filledCount != total - emptyExplicit
                ? null
                : emptyExplicit;

        if (filledCount is not null)
            return total - filledCount;

        return bare;
    }

    /// <summary>
    /// Знак квалификатора пары: слово «пуст…» (или отрицанное «не заполнено»)
    /// при паре = −1 (число называет пустых), слово «заполн…» (или «не
    /// пустых») = +1 (число называет заполненных), 0 — квалификатора нет.
    /// До пары берутся два последних слова контекста, после пары — два
    /// первых; цифра в этих словах отвязывает квалификатор (число при слове
    /// принадлежит другой паре: «1 из 12 (заполнено 11)»).
    /// </summary>
    private static int Case13PairQualifierSign(string before, string after)
    {
        var beforeWords = Case13WordsOf(before);
        if (beforeWords.Count > 0 &&
            !Case13LastWordsHaveDigits(beforeWords))
        {
            for (var offset = 1; offset <= 2 && offset <= beforeWords.Count; offset++)
            {
                var word = beforeWords[^offset];
                var previous = beforeWords.Count >= offset + 1
                    ? beforeWords[^(offset + 1)]
                    : null;
                var sign = Case13WordQualifierSign(word, previous);
                if (sign != 0)
                    return sign;
            }
        }

        var afterWords = Case13WordsOf(after);
        if (afterWords.Count > 0 && afterWords[0].IndexOfAny(Case13Digits) < 0)
        {
            var sign = Case13WordQualifierSign(afterWords[0], null);
            if (sign != 0 &&
                (afterWords.Count == 1 || afterWords[1].IndexOfAny(Case13Digits) < 0))
                return sign;
        }

        return 0;
    }

    private static readonly char[] Case13Digits =
        { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };

    private static bool Case13LastWordsHaveDigits(List<string> words) =>
        words[^1].IndexOfAny(Case13Digits) >= 0 ||
        (words.Count > 1 && words[^2].IndexOfAny(Case13Digits) >= 0);

    private static List<string> Case13WordsOf(string context) =>
        context.Split(
                new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    /// <summary>Слово-квалификатор: «заполн…» = +1, «пуст…» = −1, «не…» инвертирует.</summary>
    private static int Case13WordQualifierSign(string word, string? previousWord)
    {
        var sign = 0;
        if (ContainsText(word, "заполн"))
            sign = 1;
        else if (ContainsText(word, "пуст"))
            sign = -1;

        if (sign == 0)
            return 0;

        var negated = (word.Length > 2 &&
                       word.StartsWith("не", StringComparison.OrdinalIgnoreCase)) ||
                      (previousWord is not null &&
                       previousWord.Length <= 3 && ContainsText(previousWord, "не"));
        return negated ? -sign : sign;
    }

    /// <summary>
    /// Ответ называет виновников дырок истины сида ТОЧНЫМ множеством (ORCH-4;
    /// P3-2 рецензии r1): каждое семейство дырок названо И ни одно неповинное
    /// семейство истины не названо — дамп всех семейств сида как «виновников»
    /// не проходит. Семейства выводятся из таблицы истины, не из литералов
    /// вердиктора; имена вне таблицы истины не выводимы из кейса и не
    /// проверяются.
    /// </summary>
    internal static bool Case13AnswerNamesExactCulprits(string text)
    {
        var culprits = Case13TruthCulpritFamilies();
        foreach (var family in culprits)
        {
            if (!ContainsText(text, family))
                return false;
        }

        var innocents = Case13Truth()
            .Select(row => row.Family)
            .Distinct(StringComparer.Ordinal)
            .Where(family => !culprits.Contains(family));
        foreach (var family in innocents)
        {
            if (ContainsText(text, family))
                return false;
        }

        return true;
    }

}
