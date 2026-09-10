using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

public interface IFamilyImportPreparationService
{
    Task CloseAllPreparedDocumentsAsync(CancellationToken ct = default);

    Document? GetOpenedDocument(string sourcePath);

    void CloseAndRelease(string sourcePath);
}
