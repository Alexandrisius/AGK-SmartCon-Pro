using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using Xunit;

namespace SmartCon.Tests.UI;

/// <summary>
/// Generic.xaml is parsed at RUNTIME via XamlReader.Load (ADR-046,
/// SingletonResources) — the build never validates it, so a XAML error
/// there crashes every view on open. This test parses the embedded
/// resource exactly like production and asserts the implicit thin
/// ScrollBar style is registered.
/// </summary>
public sealed class GenericXamlLoadTests
{
    [Fact]
    public void GenericXaml_ParsesViaXamlReader_LikeProduction()
    {
        var assembly = typeof(SmartCon.UI.SingletonResources).Assembly;
        using var stream = assembly.GetManifestResourceStream("SmartCon.UI.Generic.xaml");
        Assert.NotNull(stream);

        var dictionary = (ResourceDictionary)XamlReader.Load(stream!);

        Assert.NotNull(dictionary[typeof(ScrollBar)]);
    }
}
