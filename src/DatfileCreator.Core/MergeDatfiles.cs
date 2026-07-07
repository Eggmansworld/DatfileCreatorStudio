using System.Text;

namespace DatfileCreator.Core;

/// <summary>One classified immediate subfolder of a merge category root.</summary>
public sealed class MergeJob
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    /// <summary>"merge" | "skip_direct" | "skip_empty" | "skip_mixed"</summary>
    public required string Action { get; init; }
    public required string Reason { get; init; }
    /// <summary>Deeper dat paths (populated only when Action == "merge").</summary>
    public List<string> Deeper { get; init; } = [];
}

/// <summary>
/// Merge Datfiles core, ported from the suite's _dm_* helpers. Merges
/// per-subfolder dats upward into one dat placed at the first-level subfolder
/// of a category root. The writer is a pure passthrough of source rom
/// attributes (base XML escaping only), so merged output is byte-identical.
/// </summary>
public static class MergeDatfiles
{
    /// <summary>True if the folder contains at least one .xml/.dat file directly.</summary>
    public static bool HasDirectDat(string folder)
    {
        try
        {
            foreach (string full in Directory.EnumerateFiles(folder))
            {
                string ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext is ".xml" or ".dat")
                    return true;
            }
        }
        catch
        {
            // unreadable — treat as no direct dat
        }
        return false;
    }

    /// <summary>
    /// Sorted list of dat paths found strictly BELOW subPath (in its
    /// descendants, not directly inside subPath). Hidden/system dirs skipped.
    /// </summary>
    public static List<string> FindDeeperDats(string subPath)
    {
        var results = new List<string>();
        string subNorm = subPath.ToLowerInvariant();

        void Walk(string dir)
        {
            string[] entries;
            string[] subdirs;
            try
            {
                entries = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                return;
            }

            if (dir.ToLowerInvariant() != subNorm)
            {
                foreach (string full in entries.OrderBy(Path.GetFileName, StringComparer.Ordinal))
                {
                    string ext = Path.GetExtension(full).ToLowerInvariant();
                    if (ext is ".xml" or ".dat")
                        results.Add(full);
                }
            }

            foreach (string sub in subdirs
                         .Where(d => !FolderScanner.IsHiddenOrSystem(new DirectoryInfo(d)))
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                Walk(sub);
        }

        Walk(subPath);
        return results;
    }

    /// <summary>Classify each immediate subfolder of the category root for merging.</summary>
    public static List<MergeJob> ScanForMerge(string rootPath)
    {
        var jobs = new List<MergeJob>();
        List<DirectoryInfo> entries;
        try
        {
            entries = new DirectoryInfo(rootPath).GetDirectories()
                .OrderBy(d => d.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return jobs;
        }

        foreach (var entry in entries)
        {
            if (FolderScanner.IsHiddenOrSystem(entry))
                continue;
            bool hasDirect = HasDirectDat(entry.FullName);
            var deeper = FindDeeperDats(entry.FullName);

            if (hasDirect && deeper.Count > 0)
                jobs.Add(new MergeJob
                {
                    Name = entry.Name, Path = entry.FullName, Action = "skip_mixed",
                    Reason = "dats at both direct and deeper levels — skipped",
                });
            else if (hasDirect)
                jobs.Add(new MergeJob
                {
                    Name = entry.Name, Path = entry.FullName, Action = "skip_direct",
                    Reason = "dat already present — no merge needed",
                });
            else if (deeper.Count > 0)
                jobs.Add(new MergeJob
                {
                    Name = entry.Name, Path = entry.FullName, Action = "merge",
                    Reason = deeper.Count + " source dat(s)", Deeper = deeper,
                });
            else
                jobs.Add(new MergeJob
                {
                    Name = entry.Name, Path = entry.FullName, Action = "skip_empty",
                    Reason = "no datfiles found",
                });
        }
        return jobs;
    }

    /// <summary>
    /// Parse all source dats under subPath into merged game data.
    /// merged key = the immediate subfolder of subPath (depth-2 name); rom
    /// names are path-prefixed for dats at depth 3+. header is taken from the
    /// first successfully parsed dat.
    /// </summary>
    public static (List<KeyValuePair<string, List<DatRomRecord>>> Merged,
                   Dictionary<string, string> Header, string Error)
        CollectGames(string subPath, List<string> deeperDats)
    {
        // Preserve first-seen key order (Python dict insertion order)
        var order = new List<string>();
        var merged = new Dictionary<string, List<DatRomRecord>>(StringComparer.Ordinal);
        Dictionary<string, string> firstHeader = [];
        bool haveHeader = false;

        foreach (string datPath in deeperDats)
        {
            var (index, header, err) = IncrementalUpdate.ReadDatIndex(datPath);
            if (err.Length > 0)
                continue;
            if (!haveHeader)
            {
                firstHeader = header;
                haveHeader = true;
            }

            string datDir = Path.GetDirectoryName(datPath) ?? "";
            string rel = Path.GetRelativePath(subPath, datDir).Replace('\\', '/');
            string[] parts = rel.Split('/');
            string depth2Name = parts[0];
            string romPrefix = parts.Length > 1 ? string.Join("/", parts[1..]) + "/" : "";

            if (!merged.TryGetValue(depth2Name, out var list))
            {
                merged[depth2Name] = list = [];
                order.Add(depth2Name);
            }

            foreach (string gname in index.Order)
            {
                foreach (var rom in index.Games[gname])
                {
                    list.Add(romPrefix.Length > 0
                        ? rom with { Name = romPrefix + rom.Name }
                        : rom);
                }
            }
        }

        if (order.Count == 0)
            return ([], firstHeader, "All source dats failed to parse or were empty");

        var ordered = order.Select(k => new KeyValuePair<string, List<DatRomRecord>>(k, merged[k])).ToList();
        return (ordered, firstHeader, "");
    }

    /// <summary>
    /// Write a merged Logiqx XML dat. Games are sorted by name; roms are
    /// written in source order with only their present attributes, base-XML-
    /// escaped (no quote escaping / CP437 fixing) — a faithful passthrough.
    /// </summary>
    public static void WriteMergedDat(string outPath, string datName,
        IReadOnlyList<KeyValuePair<string, List<DatRomRecord>>> mergedGames,
        IReadOnlyDictionary<string, string> header, string today)
    {
        static string E(string v) => XmlText.EscapeXml(v);
        string Get(string key) => header.GetValueOrDefault(key, "");

        string rvLine = header.GetValueOrDefault("forcepacking", "") == "fileonly"
            ? "\t\t<romvault forcepacking=\"fileonly\"/>\n"
            : "\t\t<romvault/>\n";

        using var f = new StreamWriter(outPath, append: false, new UTF8Encoding(false));
        f.Write("<?xml version=\"1.0\"?>\n");
        f.Write("<datafile>\n");
        f.Write("\t<header>\n");
        f.Write("\t\t<name>" + E(datName) + "</name>\n");
        f.Write("\t\t<description>" + E(Get("description")) + "</description>\n");
        f.Write("\t\t<category>" + E(Get("category")) + "</category>\n");
        f.Write("\t\t<version>" + E(Get("version")) + "</version>\n");
        f.Write("\t\t<date>" + E(today) + "</date>\n");
        f.Write("\t\t<author>" + E(Get("author")) + "</author>\n");
        f.Write("\t\t<url>" + E(Get("url")) + "</url>\n");
        f.Write("\t\t<homepage>" + E(Get("homepage")) + "</homepage>\n");
        f.Write("\t\t<comment>" + E(Get("comment")) + "</comment>\n");
        f.Write(rvLine);
        f.Write("\t</header>\n");

        foreach (var (gameName, roms) in mergedGames.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            f.Write("\t<game name=\"" + E(gameName) + "\">\n");
            foreach (var rom in roms)
            {
                var sb = new StringBuilder(" name=\"").Append(E(rom.Name)).Append('"');
                if (rom.Size is not null) sb.Append(" size=\"").Append(rom.Size).Append('"');
                if (rom.Crc is not null) sb.Append(" crc=\"").Append(rom.Crc).Append('"');
                if (rom.Sha1 is not null) sb.Append(" sha1=\"").Append(rom.Sha1).Append('"');
                if (rom.Sha256 is not null) sb.Append(" sha256=\"").Append(rom.Sha256).Append('"');
                if (rom.Md5 is not null) sb.Append(" md5=\"").Append(rom.Md5).Append('"');
                if (rom.Blake3 is not null) sb.Append(" blake3=\"").Append(rom.Blake3).Append('"');
                if (rom.Date is not null) sb.Append(" date=\"").Append(rom.Date).Append('"');
                f.Write("\t\t<rom" + sb + "/>\n");
            }
            f.Write("\t</game>\n");
        }

        f.Write("</datafile>\n");
    }
}
