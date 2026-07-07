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
/// Logiqx XML writers, ported line-for-line from the Python suite so output is
/// byte-identical: tab indentation, LF newlines, UTF-8 without BOM, fixed
/// attribute order (name, size, crc, sha1, sha256, md5, blake3, date).
///
/// Structure options ("opt" keys match the suite's internal names):
///   opt1 — Dirs (README "Structure 4", legacy)
///   opt2 — Archives as Games (README "Structure 1", the default)
///   opt3 — First Level Dirs as Games (README "Structure 2")
///   opt4 — First Level Dirs as Games + Merge Dirs in Games (README "Structure 3")
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

    /// <summary>Game-level tag name based on settings.</summary>
    private static string Gtag(DatSettings s)
    {
        if (s.DatFormat == "legacy")
            return "dir";
        return s.UseMachine ? "machine" : "game";
    }

    /// <summary>Write opening game/dir tag + optional description. Returns tag used.</summary>
    private static string WriteGameOpen(TextWriter f, string name, DatSettings s, int depth)
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        string tag = Gtag(s);
        f.Write($"{t}<{tag} name=\"{XmlText.Xa(name)}\">\n");
        if (tag != "dir" && s.InclGameDesc)
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

    /// <summary>Recursively flatten a Mixed subtree into path-prefixed rom entries.</summary>
    private static void MMerge(TextWriter f, FolderNode node, DatData data, DatSettings s,
                               string prefix, string indent)
    {
        foreach (string item in node.Items)
            MRom(f, item, prefix, data, s, indent);
        foreach (var sub in node.Subdirs)
            MMerge(f, sub, data, s, prefix.Length > 0 ? prefix + "/" + sub.Name : sub.Name, indent);
    }

    // ── Zipped atom helpers ──────────────────────────────────────────────

    /// <summary>Write one zip as a game/dir block containing its internal rom entries.</summary>
    private static void ZBlock(TextWriter f, string zipPath, DatData data, DatSettings s,
                               int depth, bool asGame, string nameOverride = "")
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        string stem = Path.GetFileNameWithoutExtension(zipPath);
        string name = nameOverride.Length > 0 ? nameOverride : stem;
        string tag = asGame ? Gtag(s) : "dir";
        f.Write($"{t}<{tag} name=\"{XmlText.Xa(name)}\">\n");
        if (tag != "dir" && s.InclGameDesc)
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

    /// <summary>
    /// Recursively flatten a Zipped subtree into path-prefixed rom entries.
    /// Each zip's internal files appear as prefix/stem/internal_path.
    /// </summary>
    private static void ZMerge(TextWriter f, FolderNode node, DatData data, DatSettings s,
                               string prefix, string indent)
    {
        foreach (string zp in node.Items)
        {
            string stem = Path.GetFileNameWithoutExtension(zp);
            string p = prefix.Length > 0 ? prefix + "/" + stem : stem;
            if (data.Zipped.TryGetValue(zp, out var entries))
            {
                foreach (var r in entries)
                    f.Write(indent + RomLine(p + "/" + r.Name, r.Size, r.Crc, r.Sha1,
                                             r.Md5, r.Sha256, r.DateStr,
                                             s.IncludeMd5, s.IncludeSha256, s.InclFileDate,
                                             r.Blake3, s.IncludeBlake3) + "\n");
            }
        }
        foreach (var sub in node.Subdirs)
            ZMerge(f, sub, data, s, prefix.Length > 0 ? prefix + "/" + sub.Name : sub.Name, indent);
    }

    // ── Option 1 — Dirs ──────────────────────────────────────────────────

    private static void WriteMixedOpt1(TextWriter f, FolderNode node, DatData data,
                                       DatSettings s, int depth = 1)
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        // Root-level items wrapped in a game tag named after the folder
        if (depth == 1 && node.Items.Count > 0)
        {
            string tag = WriteGameOpen(f, node.Name, s, depth);
            foreach (string item in node.Items)
                MRom(f, item, "", data, s, ti);
            f.Write($"{t}</{tag}>\n");
        }
        else
        {
            foreach (string item in node.Items)
                MRom(f, item, "", data, s, t);
        }
        foreach (var sub in node.Subdirs)
        {
            f.Write($"{t}<dir name=\"{XmlText.Xa(sub.Name)}\">\n");
            WriteMixedOpt1(f, sub, data, s, depth + 1);
            f.Write($"{t}</dir>\n");
        }
    }

    private static void WriteZippedOpt1(TextWriter f, FolderNode node, DatData data,
                                        DatSettings s, int depth = 1)
    {
        string t = Tabs(depth);
        foreach (string zp in node.Items)
            ZBlock(f, zp, data, s, depth, asGame: false);
        foreach (var sub in node.Subdirs)
        {
            f.Write($"{t}<dir name=\"{XmlText.Xa(sub.Name)}\">\n");
            WriteZippedOpt1(f, sub, data, s, depth + 1);
            f.Write($"{t}</dir>\n");
        }
    }

    // ── Option 2 — Archives as Games ─────────────────────────────────────

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
        string ti = Tabs(depth + 1);
        if (depth == 1 && node.Items.Count > 0)
        {
            string tag = WriteGameOpen(f, node.Name, s, depth);
            foreach (string item in node.Items)
                MRom(f, item, "", data, s, ti);
            f.Write($"{t}</{tag}>\n");
        }
        else
        {
            foreach (string item in node.Items)
                MRom(f, item, "", data, s, t);
        }
        foreach (var sub in node.Subdirs)
            WriteMixedOpt2Node(f, sub, data, s, depth);
    }

    private static void WriteZippedOpt2(TextWriter f, FolderNode node, DatData data,
                                        DatSettings s, int depth = 1)
    {
        // Archives as Games — fully recursive. Every zip → game; every
        // physical dir → dir. Internal zip paths flow through unchanged.
        string t = Tabs(depth);
        foreach (string zp in node.Items)
            ZBlock(f, zp, data, s, depth, asGame: true);
        foreach (var sub in node.Subdirs)
        {
            f.Write($"{t}<dir name=\"{XmlText.Xa(sub.Name)}\">\n");
            WriteZippedOpt2(f, sub, data, s, depth + 1);
            f.Write($"{t}</dir>\n");
        }
    }

    // ── Option 3 — First Level Dirs as Games ─────────────────────────────

    private static void WriteMixedOpt3(TextWriter f, FolderNode node, DatData data,
                                       DatSettings s, int depth = 1)
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        if (depth == 1 && node.Items.Count > 0)
        {
            string tag = WriteGameOpen(f, node.Name, s, depth);
            foreach (string item in node.Items)
                MRom(f, item, "", data, s, ti);
            f.Write($"{t}</{tag}>\n");
        }
        else
        {
            foreach (string item in node.Items)
                MRom(f, item, "", data, s, t);
        }
        foreach (var sub in node.Subdirs)
        {
            // First-level: always game
            string tag = WriteGameOpen(f, sub.Name, s, depth);
            if (sub.Items.Count > 0)
            {
                // Has files: files as rom, subdirs merged
                foreach (string item in sub.Items)
                    MRom(f, item, "", data, s, ti);
                foreach (var ssub in sub.Subdirs)
                    MMerge(f, ssub, data, s, ssub.Name, ti);
            }
            else
            {
                // Container: children as dir (NOT game)
                foreach (var ssub in sub.Subdirs)
                {
                    f.Write($"{ti}<dir name=\"{XmlText.Xa(ssub.Name)}\">\n");
                    WriteMixedOpt1(f, ssub, data, s, depth + 2);
                    f.Write($"{ti}</dir>\n");
                }
            }
            f.Write($"{t}</{tag}>\n");
        }
    }

    private static void WriteZippedOpt3(TextWriter f, FolderNode node, DatData data,
                                        DatSettings s, int depth = 1)
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        foreach (string zp in node.Items)
            ZBlock(f, zp, data, s, depth, asGame: true);
        foreach (var sub in node.Subdirs)
        {
            string tag = WriteGameOpen(f, sub.Name, s, depth);
            foreach (string zp in sub.Items)
                ZBlock(f, zp, data, s, depth + 1, asGame: true);
            foreach (var ssub in sub.Subdirs)
            {
                f.Write($"{ti}<dir name=\"{XmlText.Xa(ssub.Name)}\">\n");
                WriteZippedOpt2(f, ssub, data, s, depth + 2);
                f.Write($"{ti}</dir>\n");
            }
            f.Write($"{t}</{tag}>\n");
        }
    }

    // ── Option 4 — First Level Dirs as Games + Merge Dirs in Games ───────

    private static void WriteMixedOpt4(TextWriter f, FolderNode node, DatData data,
                                       DatSettings s, int depth = 1)
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        if (depth == 1 && node.Items.Count > 0)
        {
            string tag = WriteGameOpen(f, node.Name, s, depth);
            foreach (string item in node.Items)
                MRom(f, item, "", data, s, ti);
            f.Write($"{t}</{tag}>\n");
        }
        else
        {
            foreach (string item in node.Items)
                MRom(f, item, "", data, s, t);
        }
        foreach (var sub in node.Subdirs)
        {
            // First-level: always game
            string tag = WriteGameOpen(f, sub.Name, s, depth);
            foreach (string item in sub.Items)
                MRom(f, item, "", data, s, ti);
            // All subdirs merged: empty dir marker + path-prefixed roms
            foreach (var ssub in sub.Subdirs)
            {
                f.Write($"{ti}<rom name=\"{XmlText.Xa(ssub.Name)}/\" size=\"0\" crc=\"00000000\"/>\n");
                MMerge(f, ssub, data, s, ssub.Name, ti);
            }
            f.Write($"{t}</{tag}>\n");
        }
    }

    private static void WriteZippedOpt4(TextWriter f, FolderNode node, DatData data,
                                        DatSettings s, int depth = 1)
    {
        string t = Tabs(depth);
        string ti = Tabs(depth + 1);
        foreach (string zp in node.Items)
            ZBlock(f, zp, data, s, depth, asGame: true);
        foreach (var sub in node.Subdirs)
        {
            string tag = WriteGameOpen(f, sub.Name, s, depth);
            foreach (string zp in sub.Items)
                ZBlock(f, zp, data, s, depth + 1, asGame: true);
            foreach (var ssub in sub.Subdirs)
            {
                f.Write($"{ti}<rom name=\"{XmlText.Xa(ssub.Name)}/\" size=\"0\" crc=\"00000000\"/>\n");
                ZMerge(f, ssub, data, s, ssub.Name, ti);
            }
            f.Write($"{t}</{tag}>\n");
        }
    }

    // ── Dispatch ─────────────────────────────────────────────────────────

    public static void WriteBody(TextWriter f, FolderNode node, DatData data, DatSettings s)
    {
        bool mixed = s.DatType == "mixed";
        string structure = s.Structure is "opt1" or "opt2" or "opt3" or "opt4" ? s.Structure : "opt2";
        switch (structure, mixed)
        {
            case ("opt1", true): WriteMixedOpt1(f, node, data, s); break;
            case ("opt1", false): WriteZippedOpt1(f, node, data, s); break;
            case ("opt2", true): WriteMixedOpt2(f, node, data, s); break;
            case ("opt2", false): WriteZippedOpt2(f, node, data, s); break;
            case ("opt3", true): WriteMixedOpt3(f, node, data, s); break;
            case ("opt3", false): WriteZippedOpt3(f, node, data, s); break;
            case ("opt4", true): WriteMixedOpt4(f, node, data, s); break;
            case ("opt4", false): WriteZippedOpt4(f, node, data, s); break;
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
