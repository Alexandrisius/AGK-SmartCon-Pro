// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date

namespace SmartCon.Cloud.Api.Cas;

using System.Security.Cryptography;

/// <summary>Dev-реализация CAS поверх локальной ФС: {root}/{sha256[..2]}/{sha256} (git-objects модель).</summary>
public sealed class FileSystemObjectStorage : IObjectStorage
{
    private readonly string _root;

    public FileSystemObjectStorage(IConfiguration config)
    {
        _root = Path.GetFullPath(config["Cas:Root"] ?? "cas-data");
        Directory.CreateDirectory(_root);
    }

    public Task<bool> ExistsAsync(string sha256, CancellationToken ct) =>
        Task.FromResult(File.Exists(PathOf(sha256)));

    public async Task<string> PutAsync(Stream content, CancellationToken ct)
    {
        var tmp = Path.Combine(_root, $"tmp-{Guid.NewGuid():N}");
        string sha256;
        var succeeded = false;
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var hasher = new SHA256Stream(fs))
            {
                await content.CopyToAsync(hasher, ct);
                await hasher.FlushFinalHashAsync(ct);
                sha256 = hasher.Sha256Hex;
            }

            var final = PathOf(sha256);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try
            {
                File.Move(tmp, final); // атомарный rename внутри тома
            }
            catch (IOException) when (File.Exists(final))
            {
                File.Delete(tmp); // гонка параллельных PUT одного контента: победил другой писатель (дедуп)
            }

            succeeded = true;
            return sha256;
        }
        finally
        {
            if (!succeeded && File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public Task<Stream> OpenReadAsync(string sha256, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(PathOf(sha256), FileMode.Open, FileAccess.Read, FileShare.Read));

    public Task<long> SizeOfAsync(string sha256, CancellationToken ct)
    {
        var info = new FileInfo(PathOf(sha256));
        return Task.FromResult(info.Exists ? info.Length : -1);
    }

    public Task DeleteAsync(string sha256, CancellationToken ct)
    {
        var path = PathOf(sha256);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathOf(string sha256)
    {
        // Path-traversal guard + канонизация: CAS принимает только hex-хэши, хранит в lowercase
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            throw new ArgumentException("sha256 должен быть 64 hex-символа", nameof(sha256));
        sha256 = sha256.ToLowerInvariant();
        return Path.Combine(_root, sha256[..2], sha256);
    }

    /// <summary>Stream-обёртка: пишет в нижележащий поток и одновременно считает SHA-256.</summary>
    private sealed class SHA256Stream(Stream inner) : Stream
    {
        private readonly SHA256 _sha = SHA256.Create();
        private byte[] _hash = [];

        public string Sha256Hex =>
            _hash.Length == 0 ? throw new InvalidOperationException("FlushFinalHashAsync not called")
            : Convert.ToHexStringLower(_hash);

        public async Task FlushFinalHashAsync(CancellationToken ct)
        {
            _sha.TransformFinalBlock([], 0, 0); // финализация перед чтением Hash
            _hash = _sha.Hash ?? throw new InvalidOperationException("SHA256 state is empty");
            await inner.FlushAsync(ct);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            _sha.TransformBlock(buffer.ToArray(), 0, buffer.Length, null, 0);
            await inner.WriteAsync(buffer, ct);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _sha.TransformBlock(buffer, offset, count, null, 0);
            inner.Write(buffer, offset, count);
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _sha.Dispose();
            base.Dispose(disposing);
        }
    }
}
