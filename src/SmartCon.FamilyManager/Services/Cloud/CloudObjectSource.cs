using System.IO;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>
/// Источник CAS-объектов для применения манифеста: открывает объект по
/// SHA-256 (hex, lowercase). Реализация среза — скачивание из кэша/HTTP
/// (GET /v1/files/{sha256}); тесты — словарь в памяти.
/// </summary>
public interface ICloudObjectSource
{
    Task<Stream> OpenReadAsync(string sha256, CancellationToken ct = default);
}
