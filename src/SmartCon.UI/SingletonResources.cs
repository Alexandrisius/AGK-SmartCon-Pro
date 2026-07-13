using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace SmartCon.UI;

public sealed class SingletonResources : ResourceDictionary
{
    private static ResourceDictionary? _instance;

    public SingletonResources()
    {
        if (_instance is null)
        {
            var assembly = typeof(SingletonResources).Assembly;

            // ADR-046: Generic.xaml is loaded as an embedded resource and must not contain
            // external icon controls (e.g. PackIconMaterial) because XamlReader.Load cannot
            // resolve their pack://application styles without a WPF Application context in Revit.

            // Try embedded resource first (works on both net48 and net8.0)
            using var stream = assembly.GetManifestResourceStream("SmartCon.UI.Generic.xaml");
            if (stream is not null)
            {
                _instance = (ResourceDictionary)XamlReader.Load(stream);
            }
            else
            {
                // Fallback: WPF Pack URI (works when loaded normally, not via LoadFrom)
                var uri = new Uri("/SmartCon.UI;component/Generic.xaml", UriKind.Relative);
                _instance = (ResourceDictionary)Application.LoadComponent(uri);
            }
        }

        if (_instance is not null)
            MergedDictionaries.Add(_instance);
    }
}
