using System.Globalization;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Tools.Semantic;

public sealed partial class SemanticMatchTool
{
    private sealed record MatchItem(string Id, string Text, int Index);

    private sealed class SemanticMatchRequest
    {
        public IReadOnlyList<MatchItem> Items = Array.Empty<MatchItem>();

        public string Query = string.Empty;

        public string? Instructions;

        public int TopK = DefaultTopK;

        public double MinScore = DefaultMinScore;

        public List<string> Warnings = new();
    }

    private async Task<ToolResult> MatchAsync(
        SemanticMatchRequest request,
        CancellationToken ct)
    {
        var state = ComposeState(request.Query, request.Instructions);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var batchCount = (request.Items.Count + MaxChoiceOptions - 1) / MaxChoiceOptions;

        for (var start = 0; start < request.Items.Count; start += MaxChoiceOptions)
        {
            var batch = request.Items.Skip(start).Take(MaxChoiceOptions).ToList();

            IReadOnlyDictionary<string, AgentDecisionAnswer> answers;
            try
            {
                answers = await AskBatchAsync(state, batch, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transport failure of the decision client: HttpDecisionClient is
                // fail-closed and throws, so this is the "model unavailable" path
                // the case must survive without the small model.
                SmartConLogger.Warn(
                    "The decision model call failed: " + ex.Message +
                    " [Action: match the candidates yourself from the source lines or ask the user]");
                return Unavailable("The decision model call failed: " + ex.Message, request);
            }

            if (!answers.TryGetValue("match", out var answer)
                || answer.Error is not null
                || (answer.Probabilities is null && answer.Choice is null))
            {
                // A batch that the model refused or answered outside the protocol
                // would silently drop up to 255 candidates from the merge - the
                // surviving top-K could then be wrong. Correct results matter more
                // than context economy, so the whole call degrades to fail-soft.
                var reason = answer?.Error is not null
                    ? "The decision model refused or failed the choice question"
                    : "The decision model returned no usable answer";
                SmartConLogger.Warn(
                    reason + " (batch " + (start / MaxChoiceOptions + 1) + " of " + batchCount +
                    ", " + request.Items.Count + " candidates) [Action: match the candidates " +
                    "yourself from the source lines or ask the user]");
                return Unavailable(reason, request);
            }

            MergeBatch(answer, batch, scores);
        }

        return BuildResult(request, scores, batchCount);
    }

    private async Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskBatchAsync(
        string state,
        IReadOnlyList<MatchItem> batch,
        CancellationToken ct)
    {
        var criteria = new Dictionary<string, string?>(batch.Count, StringComparer.Ordinal);
        foreach (var item in batch)
            criteria[item.Id] = item.Text;

        var questions = new Dictionary<string, AgentDecisionQuestion>(StringComparer.Ordinal)
        {
            ["match"] = AgentDecisionQuestion.Choice(
                "Which item is the best match for the request?",
                criteria)
        };

        return await _decisionClient
            .AskAsync(state, questions, ct)
            .ConfigureAwait(false);
    }

    private static void MergeBatch(
        AgentDecisionAnswer answer,
        IReadOnlyList<MatchItem> batch,
        Dictionary<string, double> scores)
    {
        var batchIds = new HashSet<string>(
            batch.Select(item => item.Id), StringComparer.Ordinal);

        if (answer.Probabilities is not null)
        {
            foreach (var pair in answer.Probabilities)
            {
                if (!batchIds.Contains(pair.Key))
                    continue;

                if (!scores.TryGetValue(pair.Key, out var existing) || pair.Value > existing)
                    scores[pair.Key] = pair.Value;
            }
        }

        // A choice without probabilities is still a strong signal: the model
        // picked it outright. Confidence (when present) becomes its score; a
        // plain pick lands above the default 0.08 cutoff.
        if (!string.IsNullOrEmpty(answer.Choice)
            && batchIds.Contains(answer.Choice!)
            && !scores.ContainsKey(answer.Choice!))
        {
            scores[answer.Choice!] = answer.Confidence ?? 0.5;
        }
    }

    private ToolResult BuildResult(
        SemanticMatchRequest request,
        Dictionary<string, double> scores,
        int batchCount)
    {
        var ranked = request.Items
            .Select(item => (Item: item, Score: scores.TryGetValue(item.Id, out var s) ? s : double.NaN))
            .Where(entry => !double.IsNaN(entry.Score) && entry.Score >= request.MinScore)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Item.Index)
            .Take(request.TopK)
            .ToList();

        var warnings = new List<string>(request.Warnings);
        if (ranked.Count == 0)
        {
            warnings.Add(
                "No candidate reached minScore=" +
                request.MinScore.ToString("0.##", CultureInfo.InvariantCulture) +
                ". [Action: lower minScore, rephrase the query, or match the lines manually]");
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            matches = ranked.Select((entry, index) => new
            {
                id = entry.Item.Id,
                score = Math.Round(entry.Score, 4),
                rank = index + 1
            }).ToList(),
            decisionUsed = true,
            model = _decisionClient.Model,
            provider = _decisionClient.ProviderName,
            batchesProcessed = batchCount,
            candidatesTotal = request.Items.Count
        });

        return ToolResult.Ok(
            payload,
            "Ranked " + ranked.Count + " of " + request.Items.Count +
            " candidate(s) across " + batchCount + " decision batch(es).",
            affectedCount: ranked.Count,
            warnings: warnings.Count == 0 ? null : warnings);
    }

    private static string ComposeState(string query, string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
            return query;

        var trimmed = instructions!.Trim();
        const int maxInstructions = 1500;
        if (trimmed.Length > maxInstructions)
            trimmed = trimmed.Substring(0, maxInstructions);

        return query + "\n\nSelection context:\n" + trimmed;
    }

    private static SemanticMatchRequest? ParseRequest(JsonElement args, out string? error)
    {
        error = null;

        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("items", out var itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array
            || itemsElement.GetArrayLength() == 0)
        {
            error = "items must be a non-empty array of {id, text} objects.";
            return null;
        }

        if (itemsElement.GetArrayLength() > MaxItems)
        {
            error = "items must contain at most " + MaxItems + " entries, got " +
                    itemsElement.GetArrayLength() + ".";
            return null;
        }

        var request = new SemanticMatchRequest();
        var items = new List<MatchItem>(itemsElement.GetArrayLength());
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in itemsElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryGetString(entry, "id", out var id)
                || !TryGetString(entry, "text", out var text))
            {
                error = "Every item must be an object with non-empty string id and text.";
                return null;
            }

            if (!seenIds.Add(id))
            {
                request.Warnings.Add(
                    "Duplicate item id '" + id + "' kept only its first occurrence.");
                continue;
            }

            items.Add(new MatchItem(id, Truncate(text), items.Count));
        }

        if (items.Count == 0)
        {
            error = "items must contain at least one entry with a unique id.";
            return null;
        }

        if (!TryGetString(args, "query", out var query))
        {
            error = "query is required and must be a non-empty string.";
            return null;
        }

        request.Items = items;
        request.Query = Truncate(query);
        request.Instructions = TryGetString(args, "instructions", out var instructions)
            ? Truncate(instructions)
            : null;

        if (args.TryGetProperty("topK", out var topK))
        {
            if (topK.ValueKind != JsonValueKind.Number || !topK.TryGetInt32(out var parsedTopK)
                || parsedTopK < 1 || parsedTopK > 10)
            {
                error = "topK must be an integer between 1 and 10.";
                return null;
            }

            request.TopK = parsedTopK;
        }

        if (args.TryGetProperty("minScore", out var minScore))
        {
            if (minScore.ValueKind != JsonValueKind.Number || !minScore.TryGetDouble(out var parsedScore)
                || parsedScore < 0 || parsedScore > 1)
            {
                error = "minScore must be a number between 0 and 1.";
                return null;
            }

            request.MinScore = parsedScore;
        }

        return request;
    }

    private static bool TryGetString(JsonElement args, string property, out string value)
    {
        value = string.Empty;
        if (!args.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        value = text!;
        return true;
    }

    private static string Truncate(string value) =>
        value.Length <= MaxTextLength ? value : value.Substring(0, MaxTextLength - 3) + "...";
}
