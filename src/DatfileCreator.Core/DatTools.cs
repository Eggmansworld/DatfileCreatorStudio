using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DatfileCreator.Core;

/// <summary>Result of one bulk header update pass over a single dat file.</summary>
public sealed class HeaderUpdateDetails
{
    public string PathBefore { get; set; } = "";
    public string PathAfter { get; set; } = "";
    public string? FnDateBefore { get; set; }
    public string? FnDateAfter { get; set; }
    public string? HdrDateBefore { get; set; }
    public string? HdrDateAfter { get; set; }
    public List<string> FieldsAdded { get; } = [];
    public List<string> FieldsUpdated { get; } = [];
    public List<string> FieldsCleared { get; } = [];
    public bool Renamed { get; set; }
    public bool ContentUpdated { get; set; }
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Bulk Datfile Header Updater core, ported from the suite's _bhu_* helpers.
/// Operates on the raw text with regexes so each dat's existing formatting,
/// indentation and encoding are preserved exactly.
/// </summary>
public static partial class BulkHeaderUpdater
{
    public static readonly string[] OptionalFields =
        ["description", "category", "version", "author", "url", "homepage", "comment"];

    public static readonly Dictionary<string, string> FieldLabels = new()
    {
        ["description"] = "Description", ["category"] = "Category",
        ["version"] = "Version", ["author"] = "Author", ["url"] = "URL",
        ["homepage"] = "Homepage", ["comment"] = "Comment",
    };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateStrictRegex();

    [GeneratedRegex(@"\(\d{4}-\d{2}-\d{2}_RomVault\)")]
    private static partial Regex FilenameTokenRegex();

    [GeneratedRegex(@"(<date>\s*)(\d{4}-\d{2}-\d{2})(\s*</date>)", RegexOptions.IgnoreCase)]
    private static partial Regex DateTagRegex();

    [GeneratedRegex(@"<romvault\s+forcepacking\s*=\s*[""']fileonly[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ForcePackingRegex();

    [GeneratedRegex(@"(<header\b[^>]*>)(.*?)(</header>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeaderBlockRegex();

    [GeneratedRegex(@"(</header>)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderCloseRegex();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex BareDateRegex();

    private static readonly Dictionary<string, Regex> FieldRegexes =
        OptionalFields.ToDictionary(
            f => f,
            f => new Regex($"(<{f}>)[^<]*(</{f}>)", RegexOptions.IgnoreCase | RegexOptions.Compiled));

    public static bool ValidateDate(string s)
    {
        s = s.Trim();
        return DateStrictRegex().IsMatch(s)
            && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out _);
    }

    /// <summary>
    /// Read a dat file preserving its encoding: BOM-marked UTF-8/UTF-16,
    /// strict UTF-8, then cp1252 fallback (never fails). The returned
    /// encoding is used to write the file back unchanged.
    /// </summary>
    public static (string Text, Encoding Enc) ReadText(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return (new UTF8Encoding(true).GetString(data, 3, data.Length - 3), new UTF8Encoding(true));
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return (Encoding.Unicode.GetString(data, 2, data.Length - 2), new UnicodeEncoding(false, true));
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2), new UnicodeEncoding(true, true));
        try
        {
            var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            // Suite quirk preserved: Python decodes with "utf-8-sig" first and
            // writes back with that codec, so BOM-less UTF-8 dats gain a BOM
            // after an update. Byte-parity requires the same behaviour here.
            return (strictUtf8.GetString(data), new UTF8Encoding(true));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp1252 = Encoding.GetEncoding(1252);
            return (cp1252.GetString(data), cp1252);
        }
    }

    [GeneratedRegex(@"<header[^>]*>[ \t]*\r?\n([ \t]+)<", RegexOptions.IgnoreCase)]
    private static partial Regex IndentRegex();

    public static string DetectIndent(string text)
    {
        var m = IndentRegex().Match(text);
        return m.Success ? m.Groups[1].Value : "\t\t";
    }

    /// <summary>All .dat/.xml files under root (or root itself when it's a file), sorted.</summary>
    public static List<string> IterDatFiles(string root)
    {
        if (File.Exists(root))
            return [root];
        var result = new List<string>();
        void Walk(string dir)
        {
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                return;
            }
            foreach (string f in files.OrderBy(x => x, StringComparer.Ordinal))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".dat" or ".xml")
                    result.Add(f);
            }
            foreach (string sub in subdirs.OrderBy(x => x, StringComparer.Ordinal))
                Walk(sub);
        }
        Walk(root);
        return result;
    }

    /// <summary>
    /// Update one dat file's header fields and filename date token.
    /// fieldValues: null = leave untouched, "" = clear, other = overwrite.
    /// </summary>
    public static HeaderUpdateDetails UpdateFile(string path, string newDate,
        IReadOnlyDictionary<string, string?> fieldValues, bool addForcePacking)
    {
        var d = new HeaderUpdateDetails { PathBefore = path, PathAfter = path };
        var (text, enc) = ReadText(path);
        string newText = text;

        var hm = HeaderBlockRegex().Match(newText);
        if (!hm.Success)
        {
            d.Warnings.Add("No <header> block found — skipped.");
            return d;
        }

        string hOpen = hm.Groups[1].Value;
        string working = hm.Groups[2].Value + hm.Groups[3].Value;
        string indent = DetectIndent(newText);

        // Insert missing optional tags just before </header>
        var missing = new StringBuilder();
        foreach (string fname in OptionalFields)
        {
            if (!FieldRegexes[fname].IsMatch(working))
            {
                missing.Append($"{indent}<{fname}></{fname}>\n");
                d.FieldsAdded.Add(fname);
            }
        }
        if (missing.Length > 0)
        {
            string insert = missing.ToString();
            working = HeaderCloseRegex().Replace(working, m => insert + m.Groups[1].Value, 1);
        }

        // Update date
        var dateMatch = DateTagRegex().Match(working);
        d.HdrDateBefore = dateMatch.Success ? dateMatch.Groups[2].Value : null;
        int n = DateTagRegex().Matches(working).Count;
        working = DateTagRegex().Replace(working,
            m => m.Groups[1].Value + newDate + m.Groups[3].Value);
        if (n > 0)
        {
            d.HdrDateAfter = newDate;
            d.FieldsUpdated.Add("date");
        }
        else
        {
            d.Warnings.Add("No <date> tag found.");
        }

        // Optional fields
        foreach (string fname in OptionalFields)
        {
            if (!fieldValues.TryGetValue(fname, out string? val) || val is null)
                continue;
            var pat = FieldRegexes[fname];
            int count = pat.Matches(working).Count;
            working = pat.Replace(working, m => m.Groups[1].Value + val + m.Groups[2].Value);
            if (count > 0)
                (val.Length == 0 ? d.FieldsCleared : d.FieldsUpdated).Add(fname);
            else
                d.Warnings.Add($"No <{fname}> tag found.");
        }

        // forcepacking
        if (addForcePacking)
        {
            if (ForcePackingRegex().IsMatch(working))
            {
                d.Warnings.Add("<romvault forcepacking> already present.");
            }
            else
            {
                working = HeaderCloseRegex().Replace(working,
                    m => $"{indent}<romvault forcepacking=\"fileonly\"/>\n" + m.Groups[1].Value, 1);
                d.FieldsUpdated.Add("forcepacking");
            }
        }

        newText = newText[..hm.Index] + hOpen + working + newText[(hm.Index + hm.Length)..];
        if (newText != text)
        {
            File.WriteAllText(path, newText, enc);
            d.ContentUpdated = true;
        }

        // Rename the (YYYY-MM-DD_RomVault) filename token
        string oldName = Path.GetFileName(path);
        var tokenMatch = FilenameTokenRegex().Match(oldName);
        d.FnDateBefore = tokenMatch.Success ? BareDateRegex().Match(tokenMatch.Value).Value : null;
        if (tokenMatch.Success)
        {
            string newName = FilenameTokenRegex().Replace(oldName, $"({newDate}_RomVault)", 1);
            if (newName != oldName)
            {
                string target = Path.Combine(Path.GetDirectoryName(path) ?? "", newName);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    d.Warnings.Add($"Rename skipped (exists): {newName}");
                }
                else
                {
                    File.Move(path, target);
                    d.Renamed = true;
                    d.PathAfter = target;
                    d.FnDateAfter = newDate;
                }
            }
        }
        else
        {
            d.Warnings.Add("No filename date token found to rename.");
        }
        if (!d.Renamed)
            d.FnDateAfter = d.FnDateBefore;
        return d;
    }
}

/// <summary>Game and ROM Counter core (the suite's _fmt_size / _scan_dat_counts).</summary>
public static class DatCounter
{
    /// <summary>Human-readable decimal size (MB/GB/TB) — never bytes.</summary>
    public static string FmtSize(long bytes)
    {
        return bytes switch
        {
            < 1_000_000 => (bytes / 1_000_000.0).ToString("F2", CultureInfo.InvariantCulture) + " MB",
            < 1_000_000_000 => (bytes / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + " MB",
            < 1_000_000_000_000 => (bytes / 1_000_000_000.0).ToString("F2", CultureInfo.InvariantCulture) + " GB",
            _ => (bytes / 1_000_000_000_000.0).ToString("F2", CultureInfo.InvariantCulture) + " TB",
        };
    }

    /// <summary>Per-dat counts: games, roms, uncompressed bytes, internal dir elements.</summary>
    public sealed record DatCounts(
        int Games, int Roms, long TotalBytes, int DirCount, string DatName, string Error);

    public static DatCounts ScanDatCounts(string datPath)
    {
        try
        {
            var (index, header, err) = IncrementalUpdate.ReadDatIndex(datPath);
            if (err.Length > 0)
                return new DatCounts(0, 0, 0, 0, Path.GetFileName(datPath), err);

            string datName = header.GetValueOrDefault("name", "");
            if (datName.Length == 0)
                datName = Path.GetFileNameWithoutExtension(datPath);

            int games = index.Order.Count;
            int roms = 0;
            long totalBytes = 0;
            foreach (string gname in index.Order)
            {
                foreach (var rom in index.Games[gname])
                {
                    roms++;
                    if (long.TryParse(rom.Size, out long sz))
                        totalBytes += sz;
                }
            }

            // Count <dir> elements (internal subfolder groupings)
            int dirCount = 0;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
                using var reader = XmlReader.Create(datPath, settings);
                var doc = XDocument.Load(reader);
                dirCount = doc.Root?.Descendants("dir").Count() ?? 0;
            }
            catch
            {
                dirCount = 0;
            }

            return new DatCounts(games, roms, totalBytes, dirCount, datName, "");
        }
        catch (Exception exc)
        {
            return new DatCounts(0, 0, 0, 0, Path.GetFileName(datPath), exc.Message);
        }
    }
}

/// <summary>Validate Datfiles core (the suite's _dv_* helpers).</summary>
public static partial class DatValidator
{
    [GeneratedRegex(@"^[0-9a-fA-F]+$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"<rom\s+[^>]*\/?>", RegexOptions.IgnoreCase)]
    private static partial Regex RomLineRegex();

    [GeneratedRegex(@"(\w+)=""([^""]*)""")]
    private static partial Regex AttrRegex();

    public static bool ValidHex(string value, int length) =>
        value.Length == length && HexRegex().IsMatch(value);

    /// <summary>
    /// Validate one rom attribute set. Required: size (decimal), crc (8 hex),
    /// sha1 (40 hex). Optional md5/sha256/blake3 validated when present.
    /// Returns one (field, message) per anomaly.
    /// </summary>
    public static List<(string Field, string Message)> CheckRomAttrs(
        IReadOnlyDictionary<string, string> attrs)
    {
        var issues = new List<(string, string)>();

        foreach (string req in (string[])["size", "crc", "sha1"])
        {
            if (!attrs.ContainsKey(req))
                issues.Add((req, "Missing required attribute"));
        }

        if (attrs.TryGetValue("size", out string? sizeVal))
        {
            string val = sizeVal.Trim();
            if (val.Length == 0)
                issues.Add(("size", "Empty value"));
            else if (!val.All(char.IsAsciiDigit))
                issues.Add(("size", "Invalid decimal value: " + val));
        }

        void CheckHex(string field, int length, string label)
        {
            if (!attrs.TryGetValue(field, out string? raw))
                return;
            string val = raw.Trim();
            if (!ValidHex(val, length))
                issues.Add((field, $"Invalid {label} (expected {length} hex chars): '"
                    + (val.Length > 0 ? val : "(empty)") + "'"));
        }
        CheckHex("crc", 8, "CRC");
        CheckHex("sha1", 40, "SHA1");
        CheckHex("md5", 32, "MD5");
        CheckHex("sha256", 64, "SHA256");
        CheckHex("blake3", 64, "BLAKE3");

        return issues;
    }

    /// <summary>
    /// Scan one datfile line-by-line for rom attribute anomalies. postIssue is
    /// called once per anomaly with the formatted issue line (no newline).
    /// Returns (issueCount, romCount).
    /// </summary>
    public static (int Issues, int Roms) ValidateFile(
        string filePath, Action<string> postIssue, Func<bool> isStopped)
    {
        int issueCount = 0;
        int romCount = 0;

        using var reader = new StreamReader(filePath, Encoding.UTF8); // replacement fallback
        int lineNum = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNum++;
            if (isStopped())
                break;
            if (!line.Contains("<rom ", StringComparison.Ordinal))
                continue;
            var m = RomLineRegex().Match(line);
            if (!m.Success)
                continue;
            romCount++;
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match am in AttrRegex().Matches(m.Value))
                attrs[am.Groups[1].Value] = am.Groups[2].Value; // last wins, like dict()
            foreach (var (fld, msg) in CheckRomAttrs(attrs))
            {
                string romName = attrs.GetValueOrDefault("name", "(unknown)");
                postIssue($"  [ISSUE]  Line {lineNum}  |  {fld}  |  {msg}  |  rom=\"{romName}\"");
                issueCount++;
            }
        }

        return (issueCount, romCount);
    }

    /// <summary>Sorted .dat/.xml paths: the single file, or a recursive folder walk.</summary>
    public static List<string> CollectFiles(string path, bool singleMode)
    {
        bool IsDat(string p) =>
            p.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        if (singleMode)
            return File.Exists(path) && IsDat(path) ? [path] : [];

        var result = new List<string>();
        void Walk(string dir)
        {
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                return;
            }
            result.AddRange(files.Where(IsDat));
            foreach (string sub in subdirs)
                Walk(sub);
        }
        Walk(path);
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
