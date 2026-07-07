using System.Buffers.Binary;
using System.Text;

namespace DatfileCreator.Core;

/// <summary>
/// One entry from a zip central directory. Field semantics match Python's
/// zipfile.ZipInfo (the suite's source of truth): the CRC comes from the
/// central directory, timestamps are the raw DOS values (TorrentZip's
/// "1980/00/00" stays month 0 / day 0 — never coerced into a real DateTime),
/// and names are decoded as UTF-8 or CP437 per the entry's flag bit 11.
/// </summary>
public sealed class ZipEntryInfo
{
    public required string FileName { get; init; }
    public required ushort Method { get; init; }
    public required uint Crc32 { get; init; }
    public required long CompressedSize { get; init; }
    public required long UncompressedSize { get; init; }
    public required long HeaderOffset { get; init; }
    public required int Year { get; init; }
    public required int Month { get; init; }
    public required int Day { get; init; }
    public required int Hour { get; init; }
    public required int Minute { get; init; }
    public required int Second { get; init; }

    /// <summary>Python zipfile's is_dir(): name ends with '/'.</summary>
    public bool IsDirectory => FileName.EndsWith('/');
}

/// <summary>
/// Minimal zip central-directory parser (with zip64 support). The suite never
/// uses BCL ZipArchive because it needs raw header offsets for direct
/// compressed-data reads and must not reject method 93 (Zstandard) entries.
/// </summary>
public static class ZipCentralDirectory
{
    private const uint EocdSignature = 0x06054b50;
    private const uint Eocd64LocatorSignature = 0x07064b50;
    private const uint Eocd64Signature = 0x06064b50;
    private const uint CentralHeaderSignature = 0x02014b50;

    private static readonly Encoding Cp437;

    static ZipCentralDirectory()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp437 = Encoding.GetEncoding(437);
    }

    /// <summary>Parse all central directory entries from a seekable stream.</summary>
    public static List<ZipEntryInfo> Read(Stream stream)
    {
        long length = stream.Length;
        if (length < 22)
            throw new InvalidDataException("File is not a zip file (too small)");

        // ── Locate the End Of Central Directory record ──────────────────
        int tailLen = (int)Math.Min(length, 22 + 65535);
        stream.Seek(length - tailLen, SeekOrigin.Begin);
        byte[] tail = new byte[tailLen];
        stream.ReadExactly(tail);

        int eocdInTail = -1;
        for (int i = tailLen - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EocdSignature)
            {
                eocdInTail = i;
                break;
            }
        }
        if (eocdInTail < 0)
            throw new InvalidDataException("File is not a zip file (no end-of-central-directory record)");

        long eocdPos = length - tailLen + eocdInTail;
        var eocd = tail.AsSpan(eocdInTail);
        long totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);

        // ── Zip64: any saturated field means the real values live in the
        //    zip64 EOCD record, found via the locator just before the EOCD ──
        bool needZip64 = totalEntries == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF;
        bool isZip64 = false;
        if (needZip64 && eocdPos >= 20)
        {
            stream.Seek(eocdPos - 20, SeekOrigin.Begin);
            byte[] locator = new byte[20];
            stream.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == Eocd64LocatorSignature)
            {
                long eocd64Pos = BinaryPrimitives.ReadInt64LittleEndian(locator.AsSpan(8));
                stream.Seek(eocd64Pos, SeekOrigin.Begin);
                byte[] rec = new byte[56];
                stream.ReadExactly(rec);
                if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != Eocd64Signature)
                    throw new InvalidDataException("Corrupt zip64 end-of-central-directory record");
                totalEntries = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(32));
                cdSize = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(40));
                cdOffset = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(48));
                isZip64 = true;
            }
        }

        // Self-extractor / prepended-data adjustment, exactly as Python's
        // zipfile computes "concat": data before the archive shifts every
        // stored offset forward.
        long concat = isZip64 ? 0 : eocdPos - cdSize - cdOffset;
        if (concat < 0)
            concat = 0;

        // ── Read the whole central directory in one pass ─────────────────
        if (cdSize > int.MaxValue)
            throw new InvalidDataException("Central directory too large");
        stream.Seek(cdOffset + concat, SeekOrigin.Begin);
        byte[] cd = new byte[(int)cdSize];
        stream.ReadExactly(cd);

        var entries = new List<ZipEntryInfo>((int)Math.Min(totalEntries, 1_000_000));
        int pos = 0;
        for (long n = 0; n < totalEntries && pos + 46 <= cd.Length; n++)
        {
            var h = cd.AsSpan(pos);
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != CentralHeaderSignature)
                throw new InvalidDataException("Corrupt central directory header");

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]);
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
            ushort dosTime = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
            ushort dosDate = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
            long csize = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            long usize = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
            long headerOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]);

            byte[] nameBytes = cd.AsSpan(pos + 46, nameLen).ToArray();
            var extra = cd.AsSpan(pos + 46 + nameLen, extraLen);

            // Zip64 extended information extra field (0x0001): values appear
            // in a fixed order, present only for saturated 32-bit fields
            int ep = 0;
            while (ep + 4 <= extra.Length)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra[ep..]);
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(extra[(ep + 2)..]);
                if (id == 0x0001)
                {
                    var z = extra.Slice(ep + 4, Math.Min(len, extra.Length - ep - 4));
                    int zp = 0;
                    if (usize == 0xFFFFFFFF && zp + 8 <= z.Length)
                    {
                        usize = BinaryPrimitives.ReadInt64LittleEndian(z[zp..]);
                        zp += 8;
                    }
                    if (csize == 0xFFFFFFFF && zp + 8 <= z.Length)
                    {
                        csize = BinaryPrimitives.ReadInt64LittleEndian(z[zp..]);
                        zp += 8;
                    }
                    if (headerOffset == 0xFFFFFFFF && zp + 8 <= z.Length)
                        headerOffset = BinaryPrimitives.ReadInt64LittleEndian(z[zp..]);
                    break;
                }
                ep += 4 + len;
            }

            // Bit 11 = UTF-8 name; otherwise CP437 (Python zipfile behaviour)
            string name = (flags & 0x800) != 0
                ? Encoding.UTF8.GetString(nameBytes)
                : Cp437.GetString(nameBytes);

            entries.Add(new ZipEntryInfo
            {
                FileName = name,
                Method = method,
                Crc32 = crc,
                CompressedSize = csize,
                UncompressedSize = usize,
                HeaderOffset = headerOffset + concat,
                Year = (dosDate >> 9) + 1980,
                Month = (dosDate >> 5) & 0xF,
                Day = dosDate & 0x1F,
                Hour = dosTime >> 11,
                Minute = (dosTime >> 5) & 0x3F,
                Second = (dosTime & 0x1F) * 2,
            });

            pos += 46 + nameLen + extraLen + commentLen;
        }

        return entries;
    }

    /// <summary>
    /// Byte offset where an entry's compressed data begins. The local
    /// header's name/extra lengths CAN differ from the central directory's,
    /// so the local header is always read (same as the Python suite).
    /// </summary>
    public static long GetDataOffset(Stream stream, ZipEntryInfo entry)
    {
        try
        {
            stream.Seek(entry.HeaderOffset + 26, SeekOrigin.Begin);
            Span<byte> lens = stackalloc byte[4];
            stream.ReadExactly(lens);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(lens);
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(lens[2..]);
            return entry.HeaderOffset + 30 + nameLen + extraLen;
        }
        catch
        {
            // Fallback using central directory values (usually correct)
            return entry.HeaderOffset + 30 + Encoding.UTF8.GetByteCount(entry.FileName) + 0;
        }
    }
}
