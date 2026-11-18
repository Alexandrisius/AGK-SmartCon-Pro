using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Pure unit tests for the rename planner that guards Revit's unique-name rule
/// (chains, swaps and cycles inside one batch). Runs in the test host, no document.
/// </summary>
public sealed class AgentRenamePlannerTests
{
    [Test]
    public async Task Build_Chain_OrdersHeadFirstWithoutTemporaryNames()
    {
        var plan = AgentRenamePlanner.Build(
            new[]
            {
                Entry(1, "A", "B"),
                Entry(2, "B", "C")
            },
            new[] { "A", "B" });

        await Assert.That(plan.TempFirst.Count).IsEqualTo(0);
        await Assert.That(plan.Order.Select(entry => entry.Id).ToArray())
            .IsEquivalentTo(new long[] { 2, 1 });
    }

    [Test]
    public async Task Build_Swap_UsesOneTemporaryName()
    {
        var plan = AgentRenamePlanner.Build(
            new[]
            {
                Entry(1, "A", "B"),
                Entry(2, "B", "A")
            },
            new[] { "A", "B" });

        await Assert.That(plan.TempFirst.Count).IsEqualTo(1);
        await Assert.That(plan.Order.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Build_ThreeWayCycle_TempsOneEntryAndRenamesAll()
    {
        var plan = AgentRenamePlanner.Build(
            new[]
            {
                Entry(1, "A", "B"),
                Entry(2, "B", "C"),
                Entry(3, "C", "A")
            },
            new[] { "A", "B", "C" });

        await Assert.That(plan.TempFirst.Count).IsEqualTo(1);
        await Assert.That(plan.Order.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Build_TargetTakenByOutsideElement_DoesNotScheduleCollision()
    {
        var plan = AgentRenamePlanner.Build(
            new[] { Entry(1, "A", "B") },
            new[] { "A", "B" });

        // "B" is held by an element outside the batch: the planner cannot free it,
        // so the entry is left for the caller's validation to reject.
        await Assert.That(plan.Order.Count).IsEqualTo(0);
        await Assert.That(plan.TempFirst.Count).IsEqualTo(0);    }

    [Test]
    public async Task Build_FreeTarget_RenamesDirectly()
    {
        var plan = AgentRenamePlanner.Build(
            new[] { Entry(1, "A", "New") },
            new[] { "A", "B" });

        await Assert.That(plan.TempFirst.Count).IsEqualTo(0);
        await Assert.That(plan.Order.Count).IsEqualTo(1);
        await Assert.That(plan.Order[0].Id).IsEqualTo(1L);
    }

    private static AgentRenamePlanner.Entry Entry(long id, string current, string target) => new()
    {
        Id = id,
        CurrentName = current,
        NewName = target
    };
}
