using System.IO.Hashing;
using System.Security.Cryptography;

namespace DatfileCreator.Core;

/// <summary>Hash results for one Mixed-mode file.</summary>
public sealed record FileHashResult(
    long Size, string Crc, string Sha1, string? Md5, string? Sha256, string? Blake3);

/// <summary>
/// Bundles the per-run hash algorithms (CRC32 optional — Zipped mode takes the
/// CRC from the zip central directory instead of recomputing it).
/// </summary>
public sealed class MultiHasher : IDisposable
{
    private readonly Crc32? _crc;
    private readonly IncrementalHash _sha1;
    private readonly IncrementalHash? _md5;
    private readonly IncrementalHash? _sha256;
    private Blake3.Hasher _blake3;
    private readonly bool _hasBlake3;
    private bool _disposed;

    public MultiHasher(bool withCrc, bool includeMd5, bool includeSha256, bool includeBlake3)
    {
        _crc = withCrc ? new Crc32() : null;
        _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        _md5 = includeMd5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        _sha256 = includeSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        _hasBlake3 = includeBlake3;
        if (includeBlake3)
            _blake3 = Blake3.Hasher.New();
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        _crc?.Append(data);
        _sha1.AppendData(data);
        _md5?.AppendData(data);
        _sha256?.AppendData(data);
        if (_hasBlake3)
            _blake3.Update(data);
    }

    public string CrcHex => (_crc!.GetCurrentHashAsUInt32()).ToString("x8");

    public string Sha1Hex => Convert.ToHexStringLower(_sha1.GetCurrentHash());

    public string? Md5Hex => _md5 is null ? null : Convert.ToHexStringLower(_md5.GetCurrentHash());

    public string? Sha256Hex => _sha256 is null ? null : Convert.ToHexStringLower(_sha256.GetCurrentHash());

    public string? Blake3Hex => _hasBlake3 ? _blake3.Finalize().ToString() : null;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sha1.Dispose();
        _md5?.Dispose();
        _sha256?.Dispose();
        if (_hasBlake3)
            _blake3.Dispose();
    }
}

/// <summary>Mixed-mode whole-file hashing, ported from the suite's hash_file().</summary>
public static class FileHasher
{
    public const int DefaultChunk = 8 * 1024 * 1024;

    /// <summary>
    /// Hash a file with CRC32 + SHA1 (always) plus optional MD5 / SHA-256 / BLAKE3.
    /// Throws OperationCanceledException when the hard-stop token fires.
    /// </summary>
    public static FileHashResult HashFile(
        string path, bool includeMd5, bool includeSha256, bool includeBlake3,
        CancellationToken cancel, int chunk = DefaultChunk)
    {
        using var hasher = new MultiHasher(withCrc: true, includeMd5, includeSha256, includeBlake3);
        long size = 0;
        byte[] buffer = new byte[chunk];

        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                     bufferSize: 1, FileOptions.SequentialScan);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            int read = f.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            size += read;
            hasher.Update(buffer.AsSpan(0, read));
        }

        return new FileHashResult(size, hasher.CrcHex, hasher.Sha1Hex,
                                  hasher.Md5Hex, hasher.Sha256Hex, hasher.Blake3Hex);
    }
}
