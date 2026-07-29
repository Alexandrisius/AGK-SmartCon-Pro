using Autodesk.Revit.DB;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Минимальная реализация IRevitContext поверх документа, созданного тестом.
/// Тест-хост Nice3point.TUnit.Revit не имеет UI-сессии, поэтому production
/// RevitContext (обёртка над UIApplication) здесь неприменим.
/// </summary>
internal sealed class StubRevitContext : IRevitContext
{
    private readonly Document _document;

    public StubRevitContext(Document document)
    {
        _document = document;
    }

    public Document GetDocument() => _document;

    public string GetRevitVersion() => _document.Application.VersionNumber;

    public string GetUsername() => _document.Application.Username;
}
