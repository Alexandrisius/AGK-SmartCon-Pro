namespace SmartCon.Tests.UI;

/// <summary>
/// Запускает тест в STA-потоке. WPF FrameworkElement (TextBlock, Run, и т.д.)
/// требуют STA apartment для создания — xUnit по умолчанию использует MTA,
/// поэтому тесты, создающие WPF-объекты, нужно явно оборачивать.
/// </summary>
internal static class StaThreadRunner
{
    public static void Run(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(caught).Throw();
        }
    }

    public static T Run<T>(Func<T> factory)
    {
        T? result = default;
        Run(() => { result = factory(); });
        return result!;
    }
}
