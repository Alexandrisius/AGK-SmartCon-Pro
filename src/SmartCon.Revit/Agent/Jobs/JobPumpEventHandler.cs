using Autodesk.Revit.UI;
using SmartCon.Core.Logging;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>
/// Dedicated external event for the job engine. Deliberately separate from
/// <c>AgentAwaitableEvent</c>: a long batch must never delay tool RPCs, and tool
/// RPCs must never be counted as job slices.
/// </summary>
public sealed class JobPumpEventHandler : IExternalEventHandler, IDisposable
{
    private readonly JobRunner _runner;
    private readonly TimeSpan _sliceBudget;
    private readonly Timer _keepAlive;
    private ExternalEvent? _event;
    private int _executing;

    public JobPumpEventHandler(JobRunner runner, int sliceBudgetMs = 1500)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        var budget = Math.Min(Math.Max(sliceBudgetMs, 200), 2000);
        _sliceBudget = TimeSpan.FromMilliseconds(budget);

        // Safety net for the "external event raised but Revit is idle" case: the
        // timer only raises when the runner still has work and the event is not
        // already pending, so it never floods the Revit queue.
        _keepAlive = new Timer(OnKeepAlive, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Attach(ExternalEvent externalEvent)
    {
        _event = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
    }

    public void Execute(UIApplication app)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJobPump", ("Method", "Execute"));

        if (Interlocked.Exchange(ref _executing, 1) == 1)
        {
            return;
        }

        try
        {
            var document = app?.ActiveUIDocument?.Document;
            if (document is null)
            {
                return;
            }

            var result = _runner.RunSlice(document, _sliceBudget);
            if (result.HasMoreWork)
            {
                RaiseAgain();
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                "Execute failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: run get_job_status; the job is stopped rather than left in a silent partial state]");
        }
        finally
        {
            Interlocked.Exchange(ref _executing, 0);
        }
    }

    public string GetName() => "SmartCon.Agent.JobPump";

    public void Dispose() => _keepAlive.Dispose();

    private void RaiseAgain()
    {
        using var _scope = SmartConLogger.BeginScope("AgentJobPump", ("Method", "RaiseAgain"));

        var externalEvent = _event;
        if (externalEvent is null)
        {
            return;
        }

        try
        {
            if (!externalEvent.IsPending)
            {
                externalEvent.Raise();
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Re-raise failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: call get_job_status; the keep-alive timer retries every 2 seconds]");
        }
    }

    private void OnKeepAlive(object? state)
    {
        if (_runner.HasWork)
        {
            RaiseAgain();
        }
    }
}