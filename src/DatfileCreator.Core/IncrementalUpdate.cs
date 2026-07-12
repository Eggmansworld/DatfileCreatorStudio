using System.Xml;
using System.Xml.Linq;

namespace DatfileCreator.Core;

/// <summary>One rom entry read from an existing dat (attribute values as-is; null when absent).</summary>
public sealed record DatRomRecord(
    string Name, string? Size, string? Crc, string? Sha1,
    string? Sha256, string? Md5, string? Blake3, string? Date);

/// <summary>
/// Parsed game index of an existing dat: game/machine/dir/set entries with
/// their DIRECT rom/disk/file children, in document order (later duplicates
/// overwrite in place, like a Python dict).
/// </summary>
public sealed class DatGameIndex
{
    public List<string> Order { get; } = [];
    public Dictionary<string, List<DatRomRecord>> Games { get; } = new(StringComparer.Ordinal);

    public void Add(string name, List<DatRomRecord> roms)
    {
        if (!Games.ContainsKey(name))
            Order.Add(name);
        Games[name] = roms;
    }
}

/// <summary>Result of the shallow dat-vs-folder validation (no hashing).</summary>
public sealed class DatValidationResult
{
    public int TotalInDat { get; init; }
    public int FoundInFolder { get; init; }
    /// <summary>Whole games absent from the folder.</summary>
    public List<string> Missing { get; init; } = [];
    /// <summary>Individual files absent within present games (folder-based Mixed).</summary>
    public List<string> FileMissing { get; init; } = [];
    /// <summary>Items in the folder that are not referenced in the dat.</summary>
    public List<string> Extra { get; init; } = [];
    public double MatchPct { get; init; }
}

/// <summary>
/// Incremental update engine, ported from the suite: dat index reading,
/// dat-vs-folder validation, carry-forward data building, and .old retirement.
/// </summary>
public static class IncrementalUpdate
{
    // ── Dat XML reader ───────────────────────────────────────────────────

    /// <summary>
    /// Parse an existing Logiqx XML dat into an in-memory index.
    /// Returns (gameIndex, headerDict, errorString) — error is "" on success.
    /// For Mixed dats: game name == archive stem, rom name == filename.
    /// For Zipped dats: game name == zip stem, rom names == internal paths.
    /// </summary>
    public static (DatGameIndex Index, Dictionary<string, string> Header, string Error)
        ReadDatIndex(string datPath)
    {
        var index = new DatGameIndex();
        var header = new Dictionary<string, string>(StringComparer.Ordinal);

        XDocument doc;
        try
        {
            // DtdProcessing.Ignore: many community dats carry the Logiqx
            // DOCTYPE declaration, which Python's ElementTree skips silently
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(datPath, settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception e)
        {
            return (index, header, $"XML parse error: {e.Message}");
        }

        var root = doc.Root;
        if (root is null)
            return (index, header, "XML parse error: empty document");

        // Header fields — handle both <name> and the <n> shorthand
        var hdr = root.Element("header");
        if (hdr is not null)
        {
            foreach (string tag in (string[])["name", "n", "description", "category", "version",
                                              "date", "author", "url", "homepage", "comment"])
            {
                var el = hdr.Element(tag);
                if (el is not null && !string.IsNullOrEmpty(el.Value))
                {
                    string storeKey = tag == "n" ? "name" : tag;
                    header[storeKey] = el.Value.Trim();
                }
            }
            var rv = hdr.Element("romvault");
            if (rv is not null)
                header["forcepacking"] = rv.Attribute("forcepacking")?.Value ?? "";
        }

        // Game / machine / dir / set entries at any depth; DIRECT rom children only
        foreach (var gameEl in root.Descendants())
        {
            string tag = gameEl.Name.LocalName;
            if (tag is not ("game" or "machine" or "dir" or "set"))
                continue;
            string gname = gameEl.Attribute("name")?.Value ?? "";
            if (gname.Length == 0)
                continue;
            var roms = new List<DatRomRecord>();
            foreach (var romEl in gameEl.Elements())
            {
                if (romEl.Name.LocalName is not ("rom" or "disk" or "file"))
                    continue;
                roms.Add(new DatRomRecord(
                    romEl.Attribute("name")?.Value ?? "",
                    romEl.Attribute("size")?.Value,
                    romEl.Attribute("crc")?.Value,
                    romEl.Attribute("sha1")?.Value,
                    romEl.Attribute("sha256")?.Value,
                    romEl.Attribute("md5")?.Value,
                    romEl.Attribute("blake3")?.Value,
                    romEl.Attribute("date")?.Value));
            }
            index.Add(gname, roms);
        }

        // Loose <rom> entries directly under <datafile> (Mixed dats emit root
        // files unwrapped). Index each as a single-file entry keyed by its own
        // name so incremental carry-forward and validation still see them —
        // otherwise they'd be re-hashed on every run.
        foreach (var romEl in root.Elements())
        {
            if (romEl.Name.LocalName is not ("rom" or "disk" or "file"))
                continue;
            string rname = romEl.Attribute("name")?.Value ?? "";
            if (rname.Length == 0)
                continue;
            index.Add(rname, [new DatRomRecord(
                rname,
                romEl.Attribute("size")?.Value,
                romEl.Attribute("crc")?.Value,
                romEl.Attribute("sha1")?.Value,
                romEl.Attribute("sha256")?.Value,
                romEl.Attribute("md5")?.Value,
                romEl.Attribute("blake3")?.Value,
                romEl.Attribute("date")?.Value)]);
        }

        return (index, header, "");
    }

    // ── CRC fast-check (Zipped) ──────────────────────────────────────────

    /// <summary>
    /// Read total uncompressed size and XOR of all entry CRCs from a zip's
    /// central directory — no decompression, takes milliseconds.
    /// Returns (0, "") on failure or when the zip has no file entries.
    /// </summary>
    public static (long TotalSize, string XorCrcHex) ZipCrcFast(string zipPath)
    {
        try
        {
            long totalSize = 0;
            uint crcXor = 0;
            bool found = false;
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (var entry in ZipCentralDirectory.Read(fs))
            {
                if (entry.IsDirectory)
                    continue;
                totalSize += entry.UncompressedSize;
                crcXor ^= entry.Crc32;
                found = true;
            }
            return found ? (totalSize, crcXor.ToString("x8")) : (0, "");
        }
        catch
        {
            return (0, "");
        }
    }

    // ── Dat validation (name-only cross-check) ───────────────────────────

    /// <summary>
    /// Check how many game entries in the dat correspond to files/zips/folders
    /// actually present in folderPath (shallow check, no hashing).
    /// </summary>
    public static DatValidationResult ValidateDatVsFolder(
        DatGameIndex gameIndex, string folderPath, string datType)
    {
        bool isZipped = datType == "zipped";

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var seenDatFiles = new HashSet<string>(StringComparer.Ordinal);
        List<string> extra;

        if (isZipped)
        {
            // Full recursive walk — a per_root dat covers an entire folder
            // tree; zips and their subfolders can live at any depth
            var allZipNames = new HashSet<string>(StringComparer.Ordinal);
            var allDirNames = new HashSet<string>(StringComparer.Ordinal);
            Walk(folderPath);

            void Walk(string dir)
            {
                try
                {
                    foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                    {
                        if (info is FileInfo)
                        {
                            if (info.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                allZipNames.Add(info.Name);
                        }
                        else if (info is DirectoryInfo)
                        {
                            allDirNames.Add(info.Name);
                            Walk(info.FullName);
                        }
                    }
                }
                catch
                {
                    // unreadable subtree — same silent skip as os.walk
                }
            }

            foreach (string gname in gameIndex.Order)
            {
                var roms = gameIndex.Games[gname];
                if (roms.Count == 0)
                {
                    // <dir> container entry — verify the subfolder exists anywhere
                    seenDatFiles.Add(gname);
                    if (allDirNames.Contains(gname))
                        matched.Add(gname);
                    else
                        missing.Add(gname);
                }
                else
                {
                    // Leaf zip entry — verify the zip exists anywhere in the tree
                    string expected = gname + ".zip";
                    seenDatFiles.Add(expected);
                    if (allZipNames.Contains(expected))
                        matched.Add(expected);
                    else
                        missing.Add(gname);
                }
            }

            var datZipSet = new HashSet<string>(
                gameIndex.Order.Where(gn => gameIndex.Games[gn].Count > 0).Select(gn => gn + ".zip"),
                StringComparer.Ordinal);
            extra = allZipNames.Except(datZipSet).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        else
        {
            // Mixed — shallow scan: each game maps to a file or immediate subfolder
            var folderFiles = new HashSet<string>(StringComparer.Ordinal);
            var folderSubdirs = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var info in new DirectoryInfo(folderPath).EnumerateFileSystemInfos())
                {
                    if (info is FileInfo)
                        folderFiles.Add(info.Name);
                    else if (info is DirectoryInfo)
                        folderSubdirs.Add(info.Name);
                }
            }
            catch
            {
                // unreadable folder — empty sets, everything reports missing
            }

            foreach (string gname in gameIndex.Order)
            {
                var roms = gameIndex.Games[gname];
                bool foundThis = false;

                if (folderSubdirs.Contains(gname))
                {
                    // Folder-based Mixed (Structure 3/4): game = subfolder;
                    // fully matched only when every listed rom file is present
                    var subFiles = new HashSet<string>(StringComparer.Ordinal);
                    try
                    {
                        foreach (var f in new DirectoryInfo(Path.Combine(folderPath, gname)).EnumerateFiles())
                            subFiles.Add(f.Name);
                    }
                    catch
                    {
                        // unreadable subfolder — all roms report missing
                    }

                    bool allPresent = true;
                    foreach (var rom in roms)
                    {
                        string romName = Path.GetFileName(rom.Name);
                        if (romName.Length > 0 && !subFiles.Contains(romName))
                        {
                            allPresent = false;
                            missing.Add($"{gname}/{romName}");
                        }
                    }

                    seenDatFiles.Add(gname);
                    if (allPresent)
                        matched.Add(gname);
                    // Folder presence counts as game presence either way
                    foundThis = true;
                }
                else
                {
                    // Flat Mixed — rom name is a direct file in the folder
                    string firstRomName = roms.Count > 0 ? roms[0].Name : "";
                    string bareName = firstRomName.Length > 0 ? Path.GetFileName(firstRomName) : "";

                    if (firstRomName.Length > 0 && folderFiles.Contains(firstRomName))
                    {
                        seenDatFiles.Add(firstRomName);
                        matched.Add(firstRomName);
                        foundThis = true;
                    }
                    else if (bareName.Length > 0 && folderFiles.Contains(bareName))
                    {
                        seenDatFiles.Add(bareName);
                        matched.Add(bareName);
                        foundThis = true;
                    }
                    else if (folderFiles.Contains(gname))
                    {
                        seenDatFiles.Add(gname);
                        matched.Add(gname);
                        foundThis = true;
                    }
                    else
                    {
                        seenDatFiles.Add(firstRomName.Length > 0 ? firstRomName : gname);
                    }
                }

                if (!foundThis)
                    missing.Add(gname);
            }

            extra = folderFiles.Concat(folderSubdirs)
                               .Where(x => !seenDatFiles.Contains(x))
                               .OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        int total = gameIndex.Order.Count;
        int found = matched.Count;
        double pct = total > 0 ? found / (double)total * 100.0 : 0.0;

        // Separate whole-game misses from within-game file misses
        var gameMissing = missing.Where(m => !m.Contains('/') && !m.Contains('\\')).ToList();
        var fileMissing = missing.Where(m => m.Contains('/') || m.Contains('\\')).ToList();

        return new DatValidationResult
        {
            TotalInDat = total,
            FoundInFolder = found,
            Missing = gameMissing,
            FileMissing = fileMissing,
            Extra = extra,
            MatchPct = pct,
        };
    }

    // ── Carry-forward data builder ───────────────────────────────────────

    /// <summary>
    /// For each item: carry forward stored hashes when it matches an existing
    /// dat entry (filename + size + CRC for Zipped, filename + size for
    /// Mixed), otherwise hash it fresh. Runs single-threaded in item order,
    /// exactly like the suite. Returns updated done count plus carried /
    /// hashed counts and errors.
    /// </summary>
    public static (DatData Data, int Done, int Carried, int Hashed, List<string> Errors)
        BuildIncrementalData(List<string> items, DatGameIndex gameIndex, DatSettings s,
                             CancellationToken hardStop, EngineCallbacks cb, int doneSoFar,
                             BandwidthThrottle? throttle)
    {
        bool isZipped = s.DatType == "zipped";
        var data = new DatData();
        var errors = new List<string>();
        int doneCount = doneSoFar;
        int carriedCount = 0;
        int hashedCount = 0;

        // Flatten the game index into a quick lookup by expected filename.
        // Zipped: "GameName.zip" → internal rom name → record.
        // Mixed: every rom of every game indexed by basename (folder-based
        // Mixed games have many files per game).
        var zipLookup = new Dictionary<string, Dictionary<string, DatRomRecord>>(StringComparer.Ordinal);
        var mixedLookup = new Dictionary<string, DatRomRecord>(StringComparer.Ordinal);
        foreach (string gname in gameIndex.Order)
        {
            var roms = gameIndex.Games[gname];
            if (roms.Count == 0)
                continue;
            if (isZipped)
            {
                var map = new Dictionary<string, DatRomRecord>(StringComparer.Ordinal);
                foreach (var r in roms)
                    map[r.Name] = r;
                zipLookup[gname + ".zip"] = map;
            }
            else
            {
                foreach (var r in roms)
                {
                    string key = Path.GetFileName(r.Name);
                    if (key.Length > 0)
                        mixedLookup[key] = r;
                }
            }
        }

        static long ParseSize(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return 0;
            return long.TryParse(raw, out long v) ? v : 0;
        }

        List<ZipRomEntry>? TryCarryZipped(string zipPath)
        {
            string fname = Path.GetFileName(zipPath);
            if (!zipLookup.TryGetValue(fname, out var romMap))
                return null; // not in dat at all → new item

            // Quick CRC fingerprint from the zip central directory
            var (folderSize, folderCrc) = ZipCrcFast(zipPath);
            if (folderCrc.Length == 0)
                return null; // couldn't read zip → rehash to be safe

            // Compare against stored CRC (XOR of all entries — same calculation)
            uint storedCrcXor = 0;
            long storedSize = 0;
            foreach (var rd in romMap.Values)
            {
                string raw = rd.Crc ?? "";
                if (uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out uint crcVal))
                    storedCrcXor ^= crcVal;
                storedSize += ParseSize(rd.Size);
            }

            if (folderSize != storedSize || folderCrc != storedCrcXor.ToString("x8"))
                return null; // changed → rehash

            // Match — reconstruct rom entries from stored values.
            // NOTE: sorted by rom name CASE-SENSITIVELY (Python's sorted()),
            // unlike fresh analysis which sorts case-insensitively. Carried
            // zips keep the exact ordering the suite produces.
            var resultRoms = new List<ZipRomEntry>();
            foreach (var (romName, rd) in romMap.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                // Hash quality gate: any missing/malformed sha1 rejects the
                // whole zip carry and forces a full rehash
                string storedSha1 = rd.Sha1 ?? "";
                if (storedSha1.Length != 40)
                    return null;
                // BLAKE3 gate: dat predates BLAKE3 → rehash so it's populated
                string storedB3 = rd.Blake3 ?? "";
                if (s.IncludeBlake3 && storedB3.Length != 64)
                    return null;
                resultRoms.Add(new ZipRomEntry(
                    romName,
                    ParseSize(rd.Size),
                    string.IsNullOrEmpty(rd.Crc) ? "00000000" : rd.Crc,
                    storedSha1,
                    string.IsNullOrEmpty(rd.Md5) ? null : rd.Md5,
                    string.IsNullOrEmpty(rd.Sha256) ? null : rd.Sha256,
                    string.IsNullOrEmpty(rd.Blake3) ? null : rd.Blake3,
                    string.IsNullOrEmpty(rd.Date) ? null : rd.Date));
            }
            return resultRoms;
        }

        FileHashResult? TryCarryMixed(string filePath)
        {
            string fname = Path.GetFileName(filePath);
            if (!mixedLookup.TryGetValue(fname, out var rd))
                return null; // new item

            long storedSize = ParseSize(rd.Size);
            long folderSize;
            try
            {
                folderSize = new FileInfo(filePath).Length;
            }
            catch
            {
                return null;
            }

            if (folderSize != storedSize)
                return null; // size changed → rehash

            // Hash quality gate — reject carry when critical values are
            // missing or malformed (dats imported from external tools)
            string storedCrc = rd.Crc ?? "";
            string storedSha1 = rd.Sha1 ?? "";
            if (storedCrc.Length != 8 || storedSha1.Length != 40)
                return null;

            // BLAKE3 gate — same rationale as the zipped path
            string storedB3 = rd.Blake3 ?? "";
            if (s.IncludeBlake3 && storedB3.Length != 64)
                return null;

            return new FileHashResult(
                ParseSize(rd.Size), storedCrc, storedSha1,
                string.IsNullOrEmpty(rd.Md5) ? null : rd.Md5,
                string.IsNullOrEmpty(rd.Sha256) ? null : rd.Sha256,
                string.IsNullOrEmpty(rd.Blake3) ? null : rd.Blake3);
        }

        foreach (string item in items)
        {
            if (hardStop.IsCancellationRequested)
                break;

            string fname = Path.GetFileName(item);
            bool carried = false;
            string hashDiag = "";
            cb.ItemStarted?.Invoke(fname);
            try
            {
                if (isZipped)
                {
                    var carry = TryCarryZipped(item);
                    if (carry is not null)
                    {
                        data.Zipped[item] = carry;
                        carried = true;
                    }
                    else
                    {
                        var (res, diag) = ZipAnalyzer.Analyze(item, s.IncludeMd5, s.IncludeSha256,
                                                              s.InclFileDate, s.IncludeBlake3,
                                                              hardStop, throttle);
                        data.Zipped[item] = res;
                        hashDiag = diag;
                    }
                }
                else
                {
                    var carry = TryCarryMixed(item);
                    if (carry is not null)
                    {
                        data.Mixed[item] = carry;
                        carried = true;
                    }
                    else
                    {
                        data.Mixed[item] = FileHasher.HashFile(item, s.IncludeMd5, s.IncludeSha256,
                                                               s.IncludeBlake3, hardStop,
                                                               throttle: throttle);
                    }
                }

                if (carried)
                {
                    carriedCount++;
                    cb.ItemCarried?.Invoke(fname);
                }
                else
                {
                    hashedCount++;
                    cb.ItemHashed?.Invoke(fname, hashDiag.Length > 0 ? "  (" + hashDiag + ")" : "");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exc)
            {
                string errDetail = $"{exc.GetType().Name}: {exc.Message}";
                errors.Add("ERROR: " + item + " :: " + errDetail);
                cb.ItemError?.Invoke(fname, errDetail);
            }

            doneCount++;
            cb.Progress?.Invoke(doneCount);
        }

        return (data, doneCount, carriedCount, hashedCount, errors);
    }

    // ── .old rename helper ───────────────────────────────────────────────

    /// <summary>
    /// Rename datPath to datPath + ".old" (numeric suffix when taken).
    /// Returns (finalOldPath, errorString) — error is "" on success.
    /// </summary>
    public static (string OldPathFinal, string Error) RetireOldDat(string datPath)
    {
        string candidate = datPath + ".old";
        int counter = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = datPath + $"({counter}).old";
            counter++;
        }
        try
        {
            File.Move(datPath, candidate);
            return (candidate, "");
        }
        catch (Exception e)
        {
            return ("", $"Could not rename {datPath}: {e.Message}");
        }
    }
}
