using System.Windows.Data;
using System.Windows.Markup;

namespace SmartCon.UI.Localization;

public sealed class LocExtension : MarkupExtension
{
    private readonly string _key;

    public LocExtension(string key)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(_key))
            return string.Empty;

        return new Binding($"Item[{_key}]")
        {
            Source = TranslationSource.Instance,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
    }
}

public sealed class LocBinding : MarkupExtension
{
    private readonly string _key;

    public LocBinding(string key)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public override object? ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(_key)) return null;

        return new Binding($"Item[{_key}]")
        {
            Source = TranslationSource.Instance,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
    }
}
