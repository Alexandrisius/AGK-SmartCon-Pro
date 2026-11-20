using Xunit;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentTurnContextBuilderTests
{
    [Fact]
    public void Build_FullState_RendersStableSectionOrder()
    {
        var state = new WorkingState
        {
            Goal = "rename views",
            Constraints = { "do not delete", "only plans" },
            PlanId = "digest-1",
            CompletedSteps = { "list_views: 10 changed" },
            PendingSteps = { "rename_views: failed" },
            ActiveViewId = 42,
            ActiveViewName = "Level 1",
            SelectedUniqueIds = { "uid-1", "uid-2" },
            TouchedElementIds = { "101", "102" },
            ChangeSetIds = { "cs-1" },
            OpenQuestions = { "user rejected rename_views" }
        };

        var text = AgentTurnContextBuilder.Build(state, Context());

        Assert.StartsWith("<turn_context>\n", text);
        Assert.EndsWith("</turn_context>", text);
        Assert.Contains("goal: rename views", text);
        Assert.Contains("constraints:\n- do not delete\n- only plans", text);
        Assert.Contains(
            "plan: id=digest-1; completed: list_views: 10 changed; " +
            "pending: rename_views: failed",
            text);
        Assert.Contains("current_view: Level 1 (id 42)", text);
        Assert.Contains("selection: 2 selected (uid-1, uid-2)", text);
        Assert.Contains("change_sets: cs-1", text);
        Assert.Contains("touched_elements: 101, 102", text);
        Assert.Contains("open_questions: user rejected rename_views", text);
        Assert.Contains(
            "document: Test &amp; Project; workshared no; units mm; " +
            "levels 3/views 10/sheets 2",
            text);

        var order = new[]
        {
            "goal:", "constraints:", "plan:", "current_view:", "selection:",
            "change_sets:", "touched_elements:", "open_questions:", "document:"
        };
        var lastIndex = -1;
        foreach (var section in order)
        {
            var index = text.IndexOf(section, StringComparison.Ordinal);
            Assert.True(index > lastIndex, "Section '" + section + "' is out of order:" + text);
            lastIndex = index;
        }
    }

    [Fact]
    public void Build_XmlSensitiveValues_AreEscaped()
    {
        var state = new WorkingState
        {
            Goal = "<script>alert('x') & \"y\"</script>"
        };

        var text = AgentTurnContextBuilder.Build(state, Context());

        Assert.DoesNotContain("<script>", text);
        Assert.Contains(
            "goal: &lt;script&gt;alert(&apos;x&apos;) &amp; &quot;y&quot;&lt;/script&gt;",
            text);
    }

    [Fact]
    public void Build_EmptyState_RendersOnlyDateModeAndWrapper()
    {
        var context = new AgentProjectContext
        {
            HasDocument = false,
            DocumentId = "doc-1",
            Title = "Test",
            RevitVersion = "2025"
        };

        var text = AgentTurnContextBuilder.Build(
            new WorkingState(),
            context,
            AgentApprovalMode.ReadOnly,
            new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            "<turn_context>\ndate: 2026-11-20\nmode: ReadOnly\n</turn_context>",
            text);
    }

    [Fact]
    public void Build_CapsSectionItemCounts()
    {
        var state = new WorkingState
        {
            Constraints = Enumerable.Range(0, 15).Select(i => "c" + i).ToList(),
            SelectedUniqueIds = Enumerable.Range(0, 12).Select(i => "uid-" + i).ToList(),
            ChangeSetIds = Enumerable.Range(0, 15).Select(i => "cs-" + i).ToList(),
            TouchedElementIds = Enumerable.Range(0, 25).Select(i => "t" + i).ToList(),
            OpenQuestions = Enumerable.Range(0, 12).Select(i => "q" + i).ToList()
        };

        var text = AgentTurnContextBuilder.Build(state, Context());

        Assert.Contains("- c9", text);
        Assert.DoesNotContain("- c10", text);
        Assert.Contains("selection: 12 selected (uid-0, ", text);
        Assert.Contains("uid-9", text);
        Assert.DoesNotContain("uid-10", text);
        Assert.Contains("cs-9", text);
        Assert.DoesNotContain("cs-10", text);
        Assert.Contains("t19", text);
        Assert.DoesNotContain("t20", text);
        Assert.Contains("q9", text);
        Assert.DoesNotContain("q10", text);
    }

    [Fact]
    public void Build_TruncatesAtLineBoundaryWithinLimit()
    {
        var state = new WorkingState
        {
            Goal = new string('g', 1200),
            Constraints = Enumerable.Range(0, 10)
                .Select(i => new string((char)('a' + i), 100))
                .ToList()
        };

        var text = AgentTurnContextBuilder.Build(state, Context());

        Assert.True(text.Length <= AgentTurnContextBuilder.MaxLength);
        Assert.Contains("…", text);
        Assert.EndsWith("</turn_context>", text);
        Assert.DoesNotContain("document:", text);
        Assert.All(text.Split('\n'), line =>
            Assert.True(line.Length <= AgentTurnContextBuilder.MaxLength));
    }

    [Fact]
    public void Build_MultiLineGoal_StaysOnOneLine()
    {
        var state = new WorkingState { Goal = "first line\nsecond line" };

        var text = AgentTurnContextBuilder.Build(state, Context());

        Assert.Contains("goal: first line second line", text);
        Assert.DoesNotContain("\nsecond line", text);
    }

    [Fact]
    public void Build_ControlCharacters_AreSanitized()
    {
        var state = new WorkingState { Goal = "abc\u202Edef" };

        var text = AgentTurnContextBuilder.Build(state, Context());

        Assert.Contains("abc\\u202Edef", text);
        Assert.DoesNotContain(text, character => character == '\u202E');
    }

    [Fact]
    public void CoreAssembly_DoesNotReferenceWpf()
    {
        var references = typeof(AgentTurnContextBuilder).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToList();

        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("PresentationCore", references);
        Assert.DoesNotContain("System.Windows.Forms", references);
    }

    [Fact]
    public void Build_PlanLine_CapsCompletedStepCountAndLength()
    {
        var longStep = new string('x', 200);
        var state = new WorkingState
        {
            CompletedSteps = Enumerable.Range(0, 12)
                .Select(index => "c" + index + "-" + longStep)
                .ToList()
        };

        var text = AgentTurnContextBuilder.Build(state, Context());
        var planLine = text.Split('\n')
            .Single(line => line.StartsWith("plan: ", StringComparison.Ordinal));

        Assert.Contains("c7-", planLine, StringComparison.Ordinal);
        Assert.DoesNotContain("c8-", planLine, StringComparison.Ordinal);
        Assert.DoesNotContain("c9-", planLine, StringComparison.Ordinal);
        Assert.DoesNotContain(longStep, planLine, StringComparison.Ordinal);

        var completed = planLine
            .Split(new[] { "completed: " }, StringSplitOptions.None)[1]
            .Split(new[] { ", " }, StringSplitOptions.None);
        Assert.Equal(AgentTurnContextBuilder.MaxPlanStepsPerSection, completed.Length);
        Assert.All(completed, item =>
            Assert.True(item.Length <= AgentTurnContextBuilder.MaxPlanStepLength));
    }

    [Fact]
    public void Build_PlanLine_CapsPendingStepCountAndLength()
    {
        var longStep = new string('y', 200);
        var state = new WorkingState
        {
            PendingSteps = Enumerable.Range(0, 12)
                .Select(index => "p" + index + "-" + longStep)
                .ToList()
        };

        var text = AgentTurnContextBuilder.Build(state, Context());
        var planLine = text.Split('\n')
            .Single(line => line.StartsWith("plan: ", StringComparison.Ordinal));

        Assert.Contains("p7-", planLine, StringComparison.Ordinal);
        Assert.DoesNotContain("p8-", planLine, StringComparison.Ordinal);
        Assert.DoesNotContain("p9-", planLine, StringComparison.Ordinal);
        Assert.DoesNotContain(longStep, planLine, StringComparison.Ordinal);

        var pending = planLine
            .Split(new[] { "pending: " }, StringSplitOptions.None)[1]
            .Split(new[] { ", " }, StringSplitOptions.None);
        Assert.Equal(AgentTurnContextBuilder.MaxPlanStepsPerSection, pending.Length);
        Assert.All(pending, item =>
            Assert.True(item.Length <= AgentTurnContextBuilder.MaxPlanStepLength));
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        var context = Context();

        Assert.Throws<ArgumentNullException>(
            () => AgentTurnContextBuilder.Build(null!, context));
        Assert.Throws<ArgumentNullException>(
            () => AgentTurnContextBuilder.Build(new WorkingState(), null!));
    }

    private static AgentProjectContext Context() => new()
    {
        DocumentId = "doc-1",
        Title = "Test & Project",
        PathName = @"C:\models\test.rvt",
        RevitVersion = "2025",
        Units = "mm",
        IsWorkshared = false,
        ActiveViewId = 5,
        ActiveViewName = "Level 1",
        LevelCount = 3,
        ViewCount = 10,
        SheetCount = 2
    };
}
