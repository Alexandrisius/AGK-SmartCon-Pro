using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using SmartCon.UI.Behaviors;
using Xunit;

namespace SmartCon.Tests.UI;

/// <summary>
/// Regression for #409: the API-key fields started empty on a fresh machine, so
/// WPF never raised the BoundPassword property-changed callback (the value
/// already equalled the metadata default). The PasswordChanged subscription
/// lived in that callback, typing never reached the view model and the agent
/// saved an empty key while reporting success.
/// </summary>
public sealed class PasswordBoxBehaviorTests
{
    [Fact]
    public void TypingIntoEmptyBoundPassword_UpdatesSource()
    {
        var vm = new KeyVm();
        var typed = StaThreadRunner.Run(() => TypeInto(vm, "sk-typed"));

        Assert.Equal("sk-typed", typed);
        Assert.Equal("sk-typed", vm.Key);
    }

    [Fact]
    public void ClearingBoundPassword_UpdatesSource()
    {
        var vm = new KeyVm { Key = "sk-stored" };
        var typed = StaThreadRunner.Run(() => TypeInto(vm, string.Empty));

        Assert.Empty(typed);
        Assert.Empty(vm.Key);
    }

    [Fact]
    public void SourceChange_PushesToPasswordBox()
    {
        var vm = new KeyVm();
        var seen = StaThreadRunner.Run(() =>
        {
            var box = CreateBoundBox(vm);
            vm.Key = "sk-from-source";
            return box.Password;
        });

        Assert.Equal("sk-from-source", seen);
    }

    [Fact]
    public void Typing_KeepsBindingAliveForLaterSourceUpdates()
    {
        var vm = new KeyVm();
        var seen = StaThreadRunner.Run(() =>
        {
            var box = CreateBoundBox(vm);
            box.Password = "sk-typed";
            vm.Key = "sk-rotated";
            return box.Password;
        });

        Assert.Equal("sk-rotated", seen);
    }

    private static string TypeInto(KeyVm vm, string value)
    {
        var box = CreateBoundBox(vm);
        box.Password = value;
        return vm.Key;
    }

    private static PasswordBox CreateBoundBox(KeyVm vm)
    {
        var box = new PasswordBox();
        PasswordBoxBehavior.SetBindPassword(box, true);

        BindingOperations.SetBinding(
            box,
            PasswordBoxBehavior.BoundPasswordProperty,
            new Binding(nameof(KeyVm.Key))
            {
                Source = vm,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });

        return box;
    }

    private sealed class KeyVm : INotifyPropertyChanged
    {
        private string _key = string.Empty;

        public string Key
        {
            get => _key;
            set
            {
                _key = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Key)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}