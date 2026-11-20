namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentMainViewModel
{
    private void CollapseHistoryWork()
    {
        if (Messages.Count == 0)
            return;

        var source = Messages.ToList();
        var rebuilt = new List<AgentChatItemViewModel>(source.Count + 4);
        var segment = new List<AgentChatItemViewModel>();

        void Flush()
        {
            if (segment.Count == 0)
                return;

            var work = segment
                .Where(item => item.IsAssistant || item.IsTool)
                .ToList();
            var final = work.LastOrDefault(item =>
                item.IsAssistant && !string.IsNullOrWhiteSpace(item.Text));
            var hidden = work
                .Where(item => !ReferenceEquals(item, final))
                .ToList();
            if (hidden.Count == 0)
            {
                rebuilt.AddRange(segment);
                segment.Clear();
                return;
            }

            foreach (var item in hidden)
                item.IsVisible = false;

            var toolCalls = hidden.Count(item => item.IsTool);
            var summary = CreateWorkSummary(
                hidden,
                toolCalls,
                TimeSpan.Zero,
                stopped: false,
                reasoningOwner: null,
                history: true);
            var inserted = false;
            foreach (var item in segment)
            {
                if (!inserted && ReferenceEquals(item, final))
                {
                    rebuilt.Add(summary);
                    inserted = true;
                }

                rebuilt.Add(item);
            }

            if (!inserted)
                rebuilt.Add(summary);

            segment.Clear();
        }

        foreach (var item in source)
        {
            if (item.IsUser)
            {
                Flush();
                rebuilt.Add(item);
            }
            else
            {
                segment.Add(item);
            }
        }

        Flush();

        Messages.Clear();
        foreach (var item in rebuilt)
            Messages.Add(item);
    }
}
