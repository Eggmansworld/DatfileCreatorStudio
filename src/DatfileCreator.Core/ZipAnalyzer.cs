using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using ZstdSharp;

namespace DatfileCreator.Core;

/// <summary>One rom entry produced by analysing a zip archive.</summary>
public sealed record ZipRomEntry(
    string Name, long Size, string Crc, string Sha1,
    string? Md5, string? Sha256, string? Blake3, string? DateStr);

/// <summary>Raised when a zip cannot be read; message mirrors the Python suite.</summary>
public sealed class ZipAnalyzeException(string message) : Exception(message);

/// <summary>
/// Zipped-mode archive analysis, ported from the suite's analyze_zip().
/// Supports Stored (0), Deflate (8) and Zstandard (93, RVZSTD) entries.
/// CRC comes from the central directory; SHA1/MD5/SHA-256/BLAKE3 are computed
/// from decompressed content. Zero-byte files and directory entries get the
/// canonical empty-content hashes.
///
/// Note: this tool only READS RVZSTD archives. The "RVZSTD-" zip comment is
/// exclusively RomVault's marker — nothing here (or anywhere in Studio) may
/// ever write that comment into an archive.
/// </summary>
public static class ZipAnalyzer
{
    /// <summary>Zips at or below this size are read fully into RAM in one pass.</summary>
    public const long BytesIoThreshold = 500L * 1024 * 1024;

    /// <summary>Max compressed bytes buffered per entry on the stream path.</summary>
    public const long StreamEntryMem = 64L * 1024 * 1024;

    private const int Chunk = 4 * 1024 * 1024;      // matches RomVault's Buffersize
    private const double SlowThreshMbs = 5.0;       // [SLOW] tag threshold
    private const double EntrySlowSeconds = 3.0;    // per-entry slow report threshold

    /// <summary>Only one zip larger than the threshold reads from disk/network at a time.</summary>
    private static readonly SemaphoreSlim LargeZipLock = new(1, 1);

    public static (List<ZipRomEntry> Results, string Diag) Analyze(
        string zipPath, bool includeMd5, bool includeSha256, bool inclDate,
        bool includeBlake3, CancellationToken cancel,
        BandwidthThrottle? throttle = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new List<ZipRomEntry>();
        var slowEntries = new List<string>();

        long zipSize;
        try
        {
            zipSize = new FileInfo(zipPath).Length;
        }
        catch
        {
            zipSize = 0;
        }

        bool useBytesIo = zipSize > 0 && zipSize <= BytesIoThreshold;
        bool lockAcquired = false;

        try
        {
            if (useBytesIo)
            {
                cancel.ThrowIfCancellationRequested();
                byte[] raw = File.ReadAllBytes(zipPath);
                // Post-consume throttle: rate-limit AFTER the read so the
                // actual read runs at full speed; the sleep falls between zips
                if (throttle is not null && raw.Length > 0)
                    throttle.Consume(raw.Length, cancel);
                cancel.ThrowIfCancellationRequested();
                using var ms = new MemoryStream(raw, writable: false);
                HashEntries(ms, raw, results, slowEntries,
                            includeMd5, includeSha256, inclDate, includeBlake3, cancel, null);
            }
            else
            {
                while (!LargeZipLock.Wait(500, CancellationToken.None))
                    cancel.ThrowIfCancellationRequested();
                lockAcquired = true;
                cancel.ThrowIfCancellationRequested();
                try
                {
                    using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read, bufferSize: 1 * 1024 * 1024);
                    HashEntries(fs, memBuffer: null, results, slowEntries,
                                includeMd5, includeSha256, inclDate, includeBlake3, cancel, throttle);
                }
                finally
                {
                    LargeZipLock.Release();
                    lockAcquired = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exc)
        {
            throw new ZipAnalyzeException(
                "Failed to read " + Path.GetFileName(zipPath) + ": " + exc.Message);
        }
        finally
        {
            if (lockAcquired)
                LargeZipLock.Release();
        }

        // Sort results by rom name for deterministic dat output
        results.Sort((a, b) => string.CompareOrdinal(
            a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant()));

        // Diagnostic string
        double elapsed = stopwatch.Elapsed.TotalSeconds;
        double mbRead = zipSize / (1024.0 * 1024.0);
        string diag = "";
        if (elapsed > 0)
        {
            double rate = mbRead / elapsed;
            string pathTag = useBytesIo ? "mem" : "stream";
            diag = string.Create(CultureInfo.InvariantCulture,
                $"{mbRead:F1} MB in {elapsed:F1}s = {rate:F1} MB/s ({results.Count} entries, {pathTag})");
            if (rate < SlowThreshMbs && mbRead > 1.0)
                diag = "[SLOW] " + diag;
            if (slowEntries.Count > 0)
            {
                diag += "  SLOW ENTRIES: " + string.Join("; ", slowEntries.Take(5));
                if (slowEntries.Count > 5)
                    diag += " ...+" + (slowEntries.Count - 5) + " more";
            }
        }

        return (results, diag);
    }

    private static void HashEntries(
        Stream stream, byte[]? memBuffer,
        List<ZipRomEntry> results, List<string> slowEntries,
        bool includeMd5, bool includeSha256, bool inclDate, bool includeBlake3,
        CancellationToken cancel, BandwidthThrottle? streamThrottle)
    {
        var allEntries = ZipCentralDirectory.Read(stream);
        if (allEntries.Count == 0)
            return;

        // Canonical hashes for zero-byte content (empty file or folder entry)
        const string EmptySha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
        const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        const string EmptyMd5 = "d41d8cd98f00b204e9800998ecf8427e";
        const string EmptyBlake3 = "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262";

        foreach (var entry in allEntries.Where(e => e.IsDirectory))
        {
            cancel.ThrowIfCancellationRequested();
            string romName = entry.FileName.Replace('\\', '/');
            if (!romName.EndsWith('/'))
                romName += "/";
            results.Add(new ZipRomEntry(
                romName, 0, "00000000", EmptySha1,
                includeMd5 ? EmptyMd5 : null,
                includeSha256 ? EmptySha256 : null,
                includeBlake3 ? EmptyBlake3 : null,
                null));
        }

        // Files sorted by data position so stream reads are sequential forward
        var readOrder = allEntries.Where(e => !e.IsDirectory)
                                  .OrderBy(e => e.HeaderOffset)
                                  .ToList();

        foreach (var entry in readOrder)
        {
            cancel.ThrowIfCancellationRequested();

            using var hasher = new MultiHasher(withCrc: false, includeMd5, includeSha256, includeBlake3);
            long tEntry = Stopwatch.GetTimestamp();

            if (entry.UncompressedSize > 0)
                HashOneEntry(stream, memBuffer, entry, hasher, cancel);

            // Post-consume throttle only for the network (stream) path —
            // compressed bytes are the actual network traffic
            if (streamThrottle is not null && entry.CompressedSize > 0)
                streamThrottle.Consume(entry.CompressedSize, cancel);

            double entryElapsed = Stopwatch.GetElapsedTime(tEntry).TotalSeconds;
            if (entryElapsed >= EntrySlowSeconds)
            {
                double uncMb = entry.UncompressedSize / (1024.0 * 1024.0);
                slowEntries.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{entry.FileName} ({uncMb:F0} MB uncomp, {entryElapsed:F1}s)"));
            }

            string? dateStr = null;
            if (inclDate)
            {
                dateStr = string.Create(CultureInfo.InvariantCulture,
                    $"{entry.Year}/{entry.Month:00}/{entry.Day:00} " +
                    $"{entry.Hour:00}-{entry.Minute:00}-{entry.Second:00}");
            }

            results.Add(new ZipRomEntry(
                entry.FileName.Replace('\\', '/'),
                entry.UncompressedSize,
                entry.Crc32.ToString("x8"),
                hasher.Sha1Hex,
                hasher.Md5Hex,
                hasher.Sha256Hex,
                hasher.Blake3Hex,
                dateStr));
        }
    }

    private static void HashOneEntry(
        Stream stream, byte[]? memBuffer, ZipEntryInfo entry,
        MultiHasher hasher, CancellationToken cancel)
    {
        long dataOffset = ZipCentralDirectory.GetDataOffset(stream, entry);

        // Source stream over exactly this entry's compressed bytes
        Stream source;
        bool ownsSource = true;
        if (memBuffer is not null)
        {
            source = new MemoryStream(memBuffer, (int)dataOffset, (int)entry.CompressedSize,
                                      writable: false);
        }
        else if (entry.CompressedSize <= StreamEntryMem)
        {
            // One large sequential read per entry
            stream.Seek(dataOffset, SeekOrigin.Begin);
            cancel.ThrowIfCancellationRequested();
            byte[] compressed = new byte[entry.CompressedSize];
            stream.ReadExactly(compressed);
            source = new MemoryStream(compressed, writable: false);
        }
        else
        {
            // Entry too large to buffer — bounded view over the file stream
            stream.Seek(dataOffset, SeekOrigin.Begin);
            source = new BoundedReadStream(stream, entry.CompressedSize);
            ownsSource = false; // BoundedReadStream must not close the zip stream
        }

        try
        {
            switch (entry.Method)
            {
                case 0: // Stored
                    PumpToHasher(source, hasher, cancel);
                    break;
                case 8: // Deflate (raw)
                    using (var deflate = new DeflateStream(source, CompressionMode.Decompress,
                                                           leaveOpen: true))
                        PumpToHasher(deflate, hasher, cancel);
                    break;
                case 93: // Zstandard (RVZSTD)
                    using (var zstd = new DecompressionStream(source, leaveOpen: true))
                        PumpToHasher(zstd, hasher, cancel);
                    break;
                default:
                    throw new InvalidDataException(
                        $"compress type {entry.Method} is not supported ({entry.FileName})");
            }
        }
        finally
        {
            if (ownsSource)
                source.Dispose();
        }
    }

    private static void PumpToHasher(Stream source, MultiHasher hasher, CancellationToken cancel)
    {
        byte[] buffer = new byte[Chunk];
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            hasher.Update(buffer.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Read-only view over the next N bytes of an underlying stream — feeds a
    /// decompressor exactly one entry's compressed data so it can never read
    /// past the entry boundary (the suite's _LimitedReader).
    /// </summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _remaining = limit;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
                return 0;
            if (buffer.Length > _remaining)
                buffer = buffer[..(int)_remaining];
            int read = inner.Read(buffer);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
