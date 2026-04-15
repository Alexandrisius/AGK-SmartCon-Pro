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
