// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date

namespace SmartCon.Cloud.Api.Cas;

/// <summary>
/// Объектное хранилище CAS. Вертикальный срез: <see cref="FileSystemObjectStorage"/> (dev).
/// Прод (C0/C1): реализация поверх Cloudflare R2 (presigned URL, ADR-075 §4) —
/// контракт намеренно не раскрывает способ доставки.
/// </summary>
public interface IObjectStorage
{
    Task<bool> ExistsAsync(string sha256, CancellationToken ct);

    /// <summary>Копирует поток в CAS, вычисляя SHA-256 на лету. Возвращает фактический хэш.</summary>
    Task<string> PutAsync(Stream content, CancellationToken ct);

    Task<Stream> OpenReadAsync(string sha256, CancellationToken ct);

    Task<long> SizeOfAsync(string sha256, CancellationToken ct);
}
