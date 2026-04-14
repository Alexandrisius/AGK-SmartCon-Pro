using System.Windows;

namespace SmartCon.UI;

public sealed class SingletonResources : ResourceDictionary
{
    private static ResourceDictionary? _instance;

    public SingletonResources()
    {
        if (_instance is null)
        {
            var uri = new Uri("/SmartCon.UI;component/Generic.xaml", UriKind.Relative);
            _instance = (ResourceDictionary)Application.LoadComponent(uri);
        }
        MergedDictionaries.Add(_instance);
    }
}
