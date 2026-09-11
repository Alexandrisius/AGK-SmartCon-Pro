using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Moq;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.ViewModels.Cloud;
using SmartCon.FamilyManager.Views;
using SmartCon.Tests.FamilyManager.Services.Cloud;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels.Cloud;

/// <summary>
/// STA-смок-тест реального окна логина: ввод через НАСТОЯЩИЕ биндинги XAML
/// (TextBox.Text / PasswordBox behavior), проверка CanExecute. Ловит сломанные
/// биндинги, которые невидимы VM-тестам (серая кнопка при заполненных полях).
/// </summary>
public sealed class CloudLoginViewSmokeTests
{
    private static Task RunOnStaAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static CloudLoginViewModel MakeVm()
    {
        var http = new FakeHttpMessageHandler();
        http.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"accessToken\":\"a\",\"refreshToken\":\"r\",\"expiresInMinutes\":30,\"user\":{\"id\":\"u\",\"displayName\":\"X\",\"email\":\"e\"}}")
        });
        var auth = new CloudAuthService(new HttpClient(http), new Mock<ICloudCredentialStore>().Object,
            new Mock<IClock>().Object,
            Path.Combine(Path.GetTempPath(), $"cloud-account-{Guid.NewGuid():N}.json"));
        return new CloudLoginViewModel(auth);
    }

    private static IReadOnlyList<TextBox> FindTextBoxes(DependencyObject root)
    {
        var result = new List<TextBox>();
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is TextBox tb) result.Add(tb);
            var children = LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>();
            foreach (var child in children) queue.Enqueue(child);
        }
        return result;
    }

    private static PasswordBox? FindPasswordBox(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is PasswordBox pb) return pb;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                queue.Enqueue(child);
        }
        return null;
    }

    [Fact]
    public async Task PasswordBoxBehavior_IsolatedRoundTrip()
    {
        Exception? captured = null;
        await RunOnStaAsync(() =>
        {
            try
            {
                var box = new PasswordBox();
                box.SetValue(SmartCon.UI.Behaviors.PasswordBoxBehaviors.BindPasswordProperty, "start");
                Assert.Equal("start", box.Password); // подписка+применение source→target

                box.Password = "typed"; // пользовательский ввод
                var attached = (string)box.GetValue(SmartCon.UI.Behaviors.PasswordBoxBehaviors.BindPasswordProperty);
                Assert.Equal("typed", attached); // target→handler→SetBindPassword
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        Assert.Null(captured);
    }

    [Fact]
    public async Task TypedInput_EnablesSubmit_InRegisterMode()
    {
        Exception? captured = null;
        await RunOnStaAsync(() =>
        {
            try
            {
                var vm = MakeVm();
                var view = new CloudLoginView(vm);

                var boxes = FindTextBoxes(view);
                Assert.True(boxes.Count >= 3, $"expected >=3 TextBoxes, got {boxes.Count}");
                var password = FindPasswordBox(view);
                Assert.NotNull(password);

                // Активация биндингов в непоказанном окне асинхронна (DataBind priority):
                // сначала прогоняем очередь, иначе ввод затрётся source→target.
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.Equal(CloudLoginViewModel.DefaultEndpoint, boxes[0].Text); // биндинг endpoint жив

                var earlyExpr = System.Windows.Data.BindingOperations.GetBindingExpression(
                    password!, SmartCon.UI.Behaviors.PasswordBoxBehaviors.BindPasswordProperty);
                Assert.NotNull(earlyExpr); // биндинг пароля установлен XAML'ем
                password!.Password = "probe";
                var probeAttached = (string)password.GetValue(SmartCon.UI.Behaviors.PasswordBoxBehaviors.BindPasswordProperty);
                Assert.Equal("probe", probeAttached); // подписка PasswordChanged жива после активации

                vm.IsRegisterMode = true;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

                // Симулируем ввод пользователя через реальные биндинги.
                boxes[0].Text = "http://127.0.0.1:8787";   // Endpoint
                boxes[1].Text = "user@example.com";         // Email
                boxes[2].Text = "Иван";                     // DisplayName
                password!.Password = "secret-1";
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

                var attached = (string)password.GetValue(SmartCon.UI.Behaviors.PasswordBoxBehaviors.BindPasswordProperty);
                var pwExpr = System.Windows.Data.BindingOperations.GetBindingExpression(
                    password, SmartCon.UI.Behaviors.PasswordBoxBehaviors.BindPasswordProperty);
                var pwDiag = $"attached='{attached}' vm.len={vm.Password.Length} " +
                             $"expr={(pwExpr is null ? "NULL" : pwExpr.Status.ToString())}";

                Assert.True(vm.SubmitCommand.CanExecute(null),
                    $"CanExecute=false: endpoint='{vm.Endpoint}' email='{vm.Email}' " +
                    $"password.len={vm.Password.Length} name='{vm.DisplayName}' register={vm.IsRegisterMode}; {pwDiag}");
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        Assert.Null(captured);
    }
}
