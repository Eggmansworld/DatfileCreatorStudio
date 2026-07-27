using System.Globalization;
using System.Text;

namespace DatfileCreator.Core;

/// <summary>
/// Hash data for one job: Mixed keys are file paths, Zipped keys are zip paths.
/// </summary>
public sealed class DatData
{
    public Dictionary<string, FileHashResult> Mixed { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<ZipRomEntry>> Zipped { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Logiqx XML writers: tab indentation, LF newlines, UTF-8 without BOM, fixed
/// attribute order (name, size, crc, sha1, sha256, md5, blake3, date).
///
/// Structure options ("opt" keys match the suite's internal names):
///   opt2 — "Standard"  (the default; the only shape valid for Zipped)
///   opt3 — "Grouped"   — Mixed only
///
/// Zipped dats only ever use opt2: there a game resolves to a physical archive,
/// so pointing one at a folder of separate zips would describe a collection that
/// does not exist. See the note in WriteBody.
///
/// Every shape here obeys RomVault's grammar, which its DAT parser enforces by
/// only ever looking for these children (see RVWorld/DATReader/DatXMLReader.cs):
///   &lt;datafile&gt;      → dir, game, machine, rom, disk
///   &lt;dir&gt;           → dir, game, machine        (NEVER rom — a dir holds sets)
///   &lt;game&gt;/&lt;machine&gt; → rom, disk + metadata   (NEVER dir or game — a set holds files)
/// A subfolder inside a set therefore has exactly one legal encoding: a '/' in
/// the rom's name, which RomVault re-expands into a tree on load. Anything
/// outside this grammar is silently DISCARDED by RomVault — it does not warn —
/// so the old "Dirs" structure and "legacy" format (both of which put rom
/// entries inside dir tags) produced dats that loaded as completely empty and
/// have been retired.
/// </summary>
public static class DatWriter
{
    // ── Header ───────────────────────────────────────────────────────────

    public static void WriteDatHeader(TextWriter f, string datName, DatSettings s, string headerDate)
    {
        string rvLine = s.DatType == "mixed" && s.ForcePacking
            ? "\t\t<romvault forcepacking=\"fileonly\"/>\n"
            : "\t\t<romvault/>\n";

        f.Write("<?xml version=\"1.0\"?>\n");
        f.Write("<datafile>\n");
        f.Write("\t<header>\n");
        f.Write($"\t\t<name>{XmlText.Xe(datName)}</name>\n");
        f.Write($"\t\t<description>{XmlText.Xe(s.Description)}</description>\n");
        f.Write($"\t\t<category>{XmlText.Xe(s.Category)}</category>\n");
        f.Write($"\t\t<version>{XmlText.Xe(s.Version)}</version>\n");
        f.Write($"\t\t<date>{XmlText.Xe(headerDate)}</date>\n");
        f.Write($"\t\t<author>{XmlText.Xe(s.Author)}</author>\n");
        f.Write($"\t\t<url>{XmlText.Xe(s.Url)}</url>\n");
        f.Write($"\t\t<homepage>{XmlText.Xe(s.Homepage)}</homepage>\n");
        f.Write($"\t\t<comment>{XmlText.Xe(s.Comment)}</comment>\n");
        f.Write(rvLine);
        f.Write("\t</header>\n");
    }

    // ── Rom line ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build a &lt;rom .../&gt; line. Attribute order matches RomVault's
    /// DatXMLWriter.cs with BLAKE3 appended before date. Invalid CRC/SHA1
    /// values throw rather than silently writing an unusable dat.
    /// </summary>
    public static string RomLine(string name, long size, string crc, string sha1,
        string? md5, string? sha256, string? dateStr,
        bool includeMd5, bool includeSha256, bool inclDate,
        string? blake3, bool includeBlake3)
    {
        name = XmlText.SanitizeRomName(name);
        if (string.IsNullOrEmpty(crc) || crc.Length != 8)
            throw new InvalidOperationException(
                "rom_line: invalid CRC for '" + name + "': '" + crc
                + "' — rehash this file and regenerate the dat.");
        if (string.IsNullOrEmpty(sha1) || sha1.Length != 40)
            throw new InvalidOperationException(
                "rom_line: invalid SHA1 for '" + name + "': '" + sha1
                + "' — rehash this file and regenerate the dat.");

        var sb = new StringBuilder(160);
        sb.Append("<rom name=\"").Append(XmlText.Xa(name))
          .Append("\" size=\"").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\" crc=\"").Append(crc)
          .Append("\" sha1=\"").Append(sha1).Append('"');
        if (includeSha256 && !string.IsNullOrEmpty(sha256))
            sb.Append(" sha256=\"").Append(sha256).Append('"');
        if (includeMd5 && !string.IsNullOrEmpty(md5))
            sb.Append(" md5=\"").Append(md5).Append('"');
        if (includeBlake3 && !string.IsNullOrEmpty(blake3))
            sb.Append(" blake3=\"").Append(blake3).Append('"');
        if (inclDate && !string.IsNullOrEmpty(dateStr))
            sb.Append(" date=\"").Append(dateStr).Append('"');
        sb.Append("/>");
        return sb.ToString();
    }

    // ── Shared atom helpers ──────────────────────────────────────────────

    private static string Tabs(int depth) => new('\t', depth);

    /// <summary>
    /// Game-level tag name. Only "game"/"machine" are valid here — RomVault
    /// reads rom entries from those two and nowhere else.
    /// </summary>
    private static string Gtag(DatSettings s) => s.UseMachine ? "machine" : "game";

    /// <summary>Write opening game tag + optional description. Returns tag used.</summary>
    private static string WriteGameOpen(TextWriter f, string name, DatSettings s, int depth)
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        string tag = Gtag(s);
        f.Write($"{t}<{tag} name=\"{XmlText.Xa(name)}\">\n");
        if (s.InclGameDesc)
            f.Write($"{ti}<description>{XmlText.Xe(name)}</description>\n");
        return tag;
    }

    // ── Mixed atom helpers ───────────────────────────────────────────────

    private static void MRom(TextWriter f, string itemPath, string prefix,
                             DatData data, DatSettings s, string indent)
    {
        if (!data.Mixed.TryGetValue(itemPath, out var e))
            return;
        string fname = Path.GetFileName(itemPath);
        string name = prefix.Length > 0 ? prefix + "/" + fname : fname;
        f.Write(indent + RomLine(name, e.Size, e.Crc, e.Sha1, e.Md5, e.Sha256, null,
                                 s.IncludeMd5, s.IncludeSha256, false,
                                 e.Blake3, s.IncludeBlake3) + "\n");
    }

    // Canonical hashes of zero-byte content — shared by empty files and by the
    // folder entries below, which RomVault treats as zero-byte members.
    private const string EmptySha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string EmptyMd5 = "d41d8cd98f00b204e9800998ecf8427e";
    private const string EmptyBlake3 = "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262";

    /// <summary>
    /// A folder entry (trailing '/'), the only way a dat can state that a
    /// folder exists. RomVault keeps these when nothing else in the set sits
    /// inside that folder and prunes them when it does
    /// (DatClean.RemoveUnNeededDirectoriesFromZip), so an empty folder is lost
    /// for good if no entry is written for it.
    /// </summary>
    private static void MDirEntry(TextWriter f, string relPath, DatSettings s, string indent)
    {
        f.Write(indent + RomLine(relPath + "/", 0, "00000000", EmptySha1,
                                 EmptyMd5, EmptySha256, null,
                                 s.IncludeMd5, s.IncludeSha256, false,
                                 EmptyBlake3, s.IncludeBlake3) + "\n");
    }

    /// <summary>
    /// Recursively flatten a Mixed subtree into path-prefixed rom entries.
    /// Only folders with nothing at all inside them get an entry of their own —
    /// every other folder is already implied by the paths of its files, and
    /// RomVault discards a redundant one anyway.
    /// </summary>
    private static void MMerge(TextWriter f, FolderNode node, DatData data, DatSettings s,
                               string prefix, string indent)
    {
        if (prefix.Length > 0 && node.Items.Count == 0 && node.Subdirs.Count == 0)
            MDirEntry(f, prefix, s, indent);
        foreach (string item in node.Items)
            MRom(f, item, prefix, data, s, indent);
        foreach (var sub in node.Subdirs)
            MMerge(f, sub, data, s, prefix.Length > 0 ? prefix + "/" + sub.Name : sub.Name, indent);
    }

    // ── Zipped atom helpers ──────────────────────────────────────────────

    /// <summary>Write one zip as a game block containing its internal rom entries.</summary>
    private static void ZBlock(TextWriter f, string zipPath, DatData data, DatSettings s,
                               int depth, string nameOverride = "")
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        string stem = Path.GetFileNameWithoutExtension(zipPath);
        string name = nameOverride.Length > 0 ? nameOverride : stem;
        string tag = Gtag(s);
        f.Write($"{t}<{tag} name=\"{XmlText.Xa(name)}\">\n");
        if (s.InclGameDesc)
            f.Write($"{ti}<description>{XmlText.Xe(name)}</description>\n");
        if (data.Zipped.TryGetValue(zipPath, out var entries))
        {
            foreach (var r in entries)
                f.Write(ti + RomLine(r.Name, r.Size, r.Crc, r.Sha1, r.Md5, r.Sha256, r.DateStr,
                                     s.IncludeMd5, s.IncludeSha256, s.InclFileDate,
                                     r.Blake3, s.IncludeBlake3) + "\n");
        }
        f.Write($"{t}</{tag}>\n");
    }

    // ── opt2 — "Standard" ────────────────────────────────────────────────

    private static void WriteMixedOpt2Node(TextWriter f, FolderNode node, DatData data,
                                           DatSettings s, int depth)
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        if (node.Items.Count > 0)
        {
            // Folder has direct files → game; merge all subdirs
            string tag = WriteGameOpen(f, node.Name, s, depth);
            foreach (string item in node.Items)
                MRom(f, item, "", data, s, ti);
            foreach (var sub in node.Subdirs)
                MMerge(f, sub, data, s, sub.Name, ti);
            f.Write($"{t}</{tag}>\n");
        }
        else
        {
            // Container (no direct files) → dir; children processed same rule
            f.Write($"{t}<dir name=\"{XmlText.Xa(node.Name)}\">\n");
            foreach (var sub in node.Subdirs)
                WriteMixedOpt2Node(f, sub, data, s, depth + 1);
            f.Write($"{t}</dir>\n");
        }
    }

    private static void WriteMixedOpt2(TextWriter f, FolderNode node, DatData data,
                                       DatSettings s, int depth = 1)
    {
        string t = Tabs(depth);
        // Loose files sitting at the dat root are bare <rom> entries — never
        // wrapped in a <game>. RomVault treats a <game> as a folder, so a
        // wrapper here makes it relocate the root files into a subfolder.
        foreach (string item in node.Items)
            MRom(f, item, "", data, s, t);
        foreach (var sub in node.Subdirs)
            WriteMixedOpt2Node(f, sub, data, s, depth);
    }

    private static void WriteZippedOpt2(TextWriter f, FolderNode node, DatData data,
                                        DatSettings s, int depth = 1)
    {
        // Standard — fully recursive. Every zip → game; every
        // physical dir → dir. Internal zip paths flow through unchanged.
        string t = Tabs(depth);
        foreach (string zp in node.Items)
            ZBlock(f, zp, data, s, depth);
        foreach (var sub in node.Subdirs)
        {
            f.Write($"{t}<dir name=\"{XmlText.Xa(sub.Name)}\">\n");
            WriteZippedOpt2(f, sub, data, s, depth + 1);
            f.Write($"{t}</dir>\n");
        }
    }

    // ── opt3 — "Grouped" ─────────────────────────────────────────────────

    private static void WriteMixedOpt3(TextWriter f, FolderNode node, DatData data,
                                       DatSettings s, int depth = 1)
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        // Loose files at the dat root stay as bare <rom> entries (see WriteMixedOpt2).
        foreach (string item in node.Items)
            MRom(f, item, "", data, s, t);
        foreach (var sub in node.Subdirs)
        {
            // First-level: always game. Everything below it is merged into
            // path-prefixed rom names — the only encoding RomVault understands
            // for a subfolder inside a set. This used to branch on whether the
            // folder had loose files at its top, emitting <dir> inside <game>
            // when it did not; RomVault silently discarded those entries, so
            // the set loaded empty and RV offered to relocate the real files.
            string tag = WriteGameOpen(f, sub.Name, s, depth);
            foreach (string item in sub.Items)
                MRom(f, item, "", data, s, ti);
            foreach (var ssub in sub.Subdirs)
                MMerge(f, ssub, data, s, ssub.Name, ti);
            f.Write($"{t}</{tag}>\n");
        }
    }

    // ── Dispatch ─────────────────────────────────────────────────────────

    public static void WriteBody(TextWriter f, FolderNode node, DatData data, DatSettings s)
    {
        bool mixed = s.DatType == "mixed";
        // Retired values (notably the old "opt1" Dirs structure) fall back to
        // the default rather than writing a dat RomVault cannot read.
        string structure = s.Structure is "opt2" or "opt3" ? s.Structure : "opt2";

        if (!mixed)
        {
            // Zipped dats have exactly one valid shape. A <game> is a physical
            // ARCHIVE here — RomVault resolves every game to a .zip via the
            // dat-wide forcepacking setting (GetCompressionMethod in
            // RomVaultCore/ReadDat/DatReader.cs), never per entry. The
            // "first level dirs as games" structures pointed a game at a FOLDER
            // holding several separate zips, which claims a single archive named
            // after that folder exists — so RomVault would offer to repack the
            // collection into one. There is no correct way to emit them here,
            // so Zipped always uses Standard.
            WriteZippedOpt2(f, node, data, s);
            return;
        }

        switch (structure)
        {
            case "opt3": WriteMixedOpt3(f, node, data, s); break;
            default: WriteMixedOpt2(f, node, data, s); break;
        }
    }

    /// <summary>Write one complete dat file (header + body + closing tag).</summary>
    public static void WriteDat(string datPath, FolderNode node, DatData data,
                                string datName, DatSettings s, string headerDate,
                                List<string> errors)
    {
        try
        {
            using var f = new StreamWriter(datPath, append: false, new UTF8Encoding(false));
            WriteDatHeader(f, datName, s, headerDate);
            WriteBody(f, node, data, s);
            f.Write("</datafile>\n");
        }
        catch (Exception e)
        {
            errors.Add($"ERROR writing dat: {datPath} :: {e.GetType().Name}: {e.Message}");
        }
    }

    // ── Dat naming ───────────────────────────────────────────────────────

    /// <summary>
    /// Build the &lt;name&gt; header value and dat filename stem. Always
    /// includes the top-level input folder name between parent and subfolder.
    /// </summary>
    public static string MakeDatName(string folderName, string inputRoot, DatSettings s)
    {
        string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(inputRoot));
        var parts = new List<string>(3);
        if (s.ParentName.Trim().Length > 0)
            parts.Add(s.ParentName.Trim());
        parts.Add(rootName);
        parts.Add(folderName);
        return string.Join(" - ", parts);
    }

    public static string MakeDatFilename(string datName, string headerDate, bool incomplete = false)
    {
        string prefix = incomplete ? "[INCOMPLETE] " : "";
        return $"{prefix}{XmlText.SafeFilename(datName)} ({headerDate}_RomVault).xml";
    }
}
