using System.Text.RegularExpressions;

namespace DatfileCreator.Core;

/// <summary>One characterised top-level directory node.</summary>
public sealed class AnalyzerNode
{
    public required string Path { get; init; }
    public int Depth { get; init; }
    public int DirectItems { get; set; }
    public int DirectSubdirs { get; set; }
    public int MaxDepth { get; set; }
    public int TotalItems { get; set; }
}

public sealed class AnalyzerRecommendation
{
    public string GenMode { get; set; } = "per_root";
    public string Structure { get; set; } = "opt2";
    public string DatFormat { get; set; } = "modern";
    public bool InclDesc { get; set; } = true;
    public string Confidence { get; set; } = "high";
    public string Summary { get; set; } = "";
    public List<string> Detail { get; } = [];
}

public sealed class PathLengthStats
{
    public int TotalPaths { get; set; }
    public int MaxPathLen { get; set; }
    public string LongestPath { get; set; } = "";
    public int WarnCount => WarnPaths.Count;
    public int CritCount => CritPaths.Count;
    /// <summary>Paths 200–259 chars, sorted by length descending.</summary>
    public List<(int Length, string Path)> WarnPaths { get; } = [];
    /// <summary>Paths &gt;= 260 chars, sorted by length descending.</summary>
    public List<(int Length, string Path)> CritPaths { get; } = [];
}

public sealed class AnalyzerFindings
{
    public required string RootPath { get; init; }
    public required string DatType { get; init; }
    public int TopFolders { get; set; }
    public int TotalItems { get; set; }
    public int MaxDepth { get; set; }
    public int FoldersWithDirectItems { get; set; }
    public int FoldersAsContainers { get; set; }
    public int FoldersWithNestedSubdirs { get; set; }
    public int FoldersEmpty { get; set; }
    public int FoldersFlatGames { get; set; }
    public SortedDictionary<int, int> DepthHistogram { get; } = [];
    public List<string> Notes { get; } = [];
    public List<AnalyzerNode> Nodes { get; } = [];
    public AnalyzerRecommendation Recommendation { get; set; } = new();
    public PathLengthStats? PathStats { get; set; }
}

/// <summary>
/// Fast folder structure analyzer + recommendation engine + path length
/// collector, ported from the suite. No hashing — completes in seconds even
/// for very large collections.
/// </summary>
public static partial class FolderAnalysis
{
    public const int WarnLimit = 200;
    public const int CritLimit = 260;

    // ── Structure analysis ───────────────────────────────────────────────

    public static AnalyzerFindings Analyze(
        string rootPath, string datType,
        Action<string, int>? progress = null, CancellationToken cancel = default)
    {
        bool isZipped = datType == "zipped";
        var findings = new AnalyzerFindings { RootPath = rootPath, DatType = datType };

        AnalyzerNode FastScanNode(string dirPath, int depth)
        {
            var node = new AnalyzerNode { Path = dirPath, Depth = depth, MaxDepth = depth };
            if (cancel.IsCancellationRequested)
                return node;

            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(dirPath).GetFileSystemInfos();
            }
            catch
            {
                return node;
            }

            var subdirs = new List<string>();
            foreach (var e in entries)
            {
                try
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (e is DirectoryInfo)
                    {
                        // Hidden/system check only on dirs (we recurse into them)
                        if (FolderScanner.IsHiddenOrSystem(e))
                            continue;
                        node.DirectSubdirs++;
                        subdirs.Add(e.FullName);
                    }
                    else if (e is FileInfo)
                    {
                        if (isZipped)
                        {
                            if (e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                node.DirectItems++;
                        }
                        else if (!e.Name.StartsWith('.'))
                        {
                            // Mixed: count all non-dot files — fast approximation
                            // sufficient for structural analysis
                            node.DirectItems++;
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            node.TotalItems = node.DirectItems;
            foreach (string sub in subdirs)
            {
                if (cancel.IsCancellationRequested)
                    break;
                var child = FastScanNode(sub, depth + 1);
                node.TotalItems += child.TotalItems;
                node.MaxDepth = Math.Max(node.MaxDepth, child.MaxDepth);
            }
            return node;
        }

        FileSystemInfo[] raw;
        try
        {
            raw = new DirectoryInfo(rootPath).GetFileSystemInfos();
        }
        catch (Exception ex)
        {
            findings.Notes.Add("Error scanning root: " + ex.Message);
            MakeRecommendation(findings);
            return findings;
        }

        var topEntries = raw
            .Where(e => (e.Attributes & FileAttributes.ReparsePoint) == 0
                        && e is DirectoryInfo
                        && !FolderScanner.IsHiddenOrSystem(e))
            .OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

        // Root-level items (fast count)
        int rootItems = raw.Count(e => e is FileInfo
            && (isZipped
                ? e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                : !e.Name.StartsWith('.')));
        if (rootItems > 0)
            findings.Notes.Add(rootItems + " item(s) found directly in the root folder — "
                + "these will be datted separately as a root-level dat.");

        foreach (var entry in topEntries)
        {
            if (cancel.IsCancellationRequested)
            {
                findings.Notes.Add("Scan cancelled by user.");
                break;
            }

            var node = FastScanNode(entry.FullName, 1);
            findings.Nodes.Add(node);
            findings.TopFolders++;
            findings.TotalItems += node.TotalItems;
            findings.MaxDepth = Math.Max(findings.MaxDepth, node.MaxDepth);
            findings.DepthHistogram[node.MaxDepth] =
                findings.DepthHistogram.GetValueOrDefault(node.MaxDepth) + 1;

            if (node.TotalItems == 0)
            {
                findings.FoldersEmpty++;
            }
            else if (node.DirectItems > 0 && node.DirectSubdirs > 0)
            {
                findings.FoldersWithDirectItems++;
                findings.FoldersWithNestedSubdirs++;
            }
            else if (node.DirectItems > 0)
            {
                findings.FoldersWithDirectItems++;
            }
            else if (node.DirectSubdirs > 0)
            {
                findings.FoldersAsContainers++;
            }

            progress?.Invoke(entry.Name, node.TotalItems);
        }

        findings.FoldersFlatGames = findings.TopFolders - findings.FoldersEmpty
            - findings.FoldersWithDirectItems - findings.FoldersAsContainers;

        MakeRecommendation(findings);
        return findings;
    }

    // ── Recommendation ───────────────────────────────────────────────────

    private static void MakeRecommendation(AnalyzerFindings f)
    {
        int n = f.TopFolders;
        int maxD = f.MaxDepth;
        int containers = f.FoldersAsContainers;
        int nested = f.FoldersWithNestedSubdirs;
        int flat = f.FoldersFlatGames;
        int empty = f.FoldersEmpty;
        string cw = f.DatType == "zipped" ? "zip archives" : "files";

        var rec = new AnalyzerRecommendation();
        f.Recommendation = rec;
        var detail = rec.Detail;

        if (n == 0)
        {
            rec.Confidence = "none";
            rec.Summary = "No subfolders found. Nothing to dat.";
            return;
        }
        if (f.TotalItems == 0)
        {
            rec.Confidence = "none";
            rec.Summary = "No content files found in any subfolder.";
            return;
        }

        // Generation mode
        if (n > 20 && maxD <= 2 && containers == 0)
        {
            rec.GenMode = "per_all";
            detail.Add(n + " shallow top-level folders (depth <= 2, no containers). "
                + "'1 dat per root folder & all subfolders (TOSEC style)' works well "
                + "here since each folder is independent.");
        }
        else
        {
            rec.GenMode = "per_root";
            detail.Add(n + " top-level folder(s) with content up to depth " + maxD + ". "
                + "'1 dat per root folder (simple collection)' recommended — each folder "
                + "becomes one self-contained dat.");
        }

        // Structure
        if (maxD <= 2 && containers == 0 && nested == 0)
        {
            rec.Structure = "opt2";
            detail.Add("Content is flat or has at most one level of subdirectories. "
                + "Structure 2 (Archives as Games) is the standard choice — "
                + cw + " become <game> entries, physical subdirs become <dir> entries.");
        }
        else if (maxD <= 2 && containers > 0)
        {
            rec.Structure = "opt2";
            rec.Confidence = "medium";
            detail.Add(containers + " of " + n + " top-level folder(s) act as containers "
                + "(no " + cw + " directly, only subfolders). "
                + "Structure 2 handles this correctly: game folders become <game> entries, "
                + "container folders become <dir> entries.");
        }
        else if (maxD >= 3 && nested > n / 2)
        {
            rec.Structure = "opt4";
            detail.Add("Deep structure detected (max depth " + maxD + "). "
                + nested + " folder(s) have both direct " + cw + " AND nested subdirectories. "
                + "Structure 4 (First Level Dirs as Games + Merge Dirs) captures this most cleanly.");
        }
        else if (maxD >= 3 && containers > 0)
        {
            rec.Structure = "opt3";
            detail.Add("Deep structure detected (max depth " + maxD + "). "
                + containers + " container folder(s) found. "
                + "Structure 3 (First Level Dirs as Games) maps each top-level "
                + "folder to a game entry regardless of direct content.");
        }
        else
        {
            rec.Structure = "opt2";
            detail.Add("Moderate depth (max " + maxD + " levels). "
                + "Structure 2 (Archives as Games) is the standard choice.");
        }

        // Confidence adjustments
        if (containers > 0 && flat > 0 && nested > 0)
        {
            rec.Confidence = "medium";
            f.Notes.Add("Mixed pattern: " + flat + " flat game folder(s), "
                + containers + " container folder(s), and "
                + nested + " folder(s) with both direct content and subdirectories. "
                + "Consider using the Preview window to compare structure options.");
        }

        if (empty > 0)
            f.Notes.Add(empty + " empty top-level folder(s) found — these will be skipped.");

        if (maxD >= 5)
            f.Notes.Add("Very deep nesting detected (max " + maxD + " levels). "
                + "Structure 4 is recommended. If your top-level subfolders each represent "
                + "independent sub-collections rather than a single game or title, consider "
                + "switching Generation to '1 dat per root folder & all subfolders "
                + "(TOSEC style)' so each "
                + "subfolder gets its own separate dat file.");

        var structLabels = new Dictionary<string, string>
        {
            ["opt1"] = "Structure 1 (Dirs)",
            ["opt2"] = "Structure 2 (Archives as Games)",
            ["opt3"] = "Structure 3 (First Level Dirs as Games)",
            ["opt4"] = "Structure 4 (First Level Dirs + Merge Dirs)",
        };
        string modeLabel = rec.GenMode switch
        {
            "per_top" => "1 dat per top-level folder (single dat)",
            "per_all" => "1 dat per root folder & all subfolders (TOSEC style)",
            _ => "1 dat per root folder (simple collection)",
        };
        rec.Summary = modeLabel + "  |  " + structLabels[rec.Structure]
            + "  |  Modern  |  Confidence: " + rec.Confidence.ToUpperInvariant();
    }

    // ── Path length collection ───────────────────────────────────────────

    /// <summary>
    /// Walk every path (dirs and files) under rootPath and collect
    /// path-length statistics. Hidden/dot directories are skipped from
    /// traversal, exactly like the suite.
    /// </summary>
    public static PathLengthStats CollectPathLengths(string rootPath, CancellationToken cancel = default)
    {
        var stats = new PathLengthStats();

        void Check(string path)
        {
            int len = path.Length;
            stats.TotalPaths++;
            if (len > stats.MaxPathLen)
            {
                stats.MaxPathLen = len;
                stats.LongestPath = path;
            }
            if (len >= CritLimit)
                stats.CritPaths.Add((len, path));
            else if (len >= WarnLimit)
                stats.WarnPaths.Add((len, path));
        }

        void Walk(string dir)
        {
            if (cancel.IsCancellationRequested)
                return;
            Check(dir);

            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                return;
            }
            foreach (string f in files)
                Check(f);
            foreach (string sub in subdirs)
            {
                if (Path.GetFileName(sub).StartsWith('.'))
                    continue;
                Walk(sub);
            }
        }

        Walk(rootPath);
        stats.WarnPaths.Sort((a, b) => b.Length.CompareTo(a.Length));
        stats.CritPaths.Sort((a, b) => b.Length.CompareTo(a.Length));
        return stats;
    }

    // ── Filename stem/extension helpers (path repair tool) ───────────────

    // Known multi-part extensions: Path.GetExtension only strips the last
    // component (.png from .p8.png), so these are checked first
    private static readonly string[] MultiExt =
        [".p8.png", ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst"];

    /// <summary>Split a filename into (stem, ext), handling known multi-part extensions.</summary>
    public static (string Stem, string Ext) SplitStemExt(string filename)
    {
        string fl = filename.ToLowerInvariant();
        foreach (string me in MultiExt)
        {
            if (fl.EndsWith(me, StringComparison.Ordinal))
                return (filename[..^me.Length], filename[^me.Length..]);
        }
        string ext = Path.GetExtension(filename);
        return (filename[..^ext.Length], ext);
    }

    [GeneratedRegex(@"  +")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]{1,80}[\)\]]\s*$")]
    private static partial Regex TrailingTokenRegex();

    /// <summary>
    /// Compute a shortened stem to bring the total path within
    /// <paramref name="target"/> chars. Strategy: strip whitespace, collapse
    /// spaces, remove trailing parenthetical/bracketed ROM tokens, then
    /// hard-truncate. Returns null when no shortening is possible or needed.
    /// </summary>
    public static string? ComputeSuggestion(string dirPart, string stem, string ext, int target)
    {
        int avail = target - (dirPart.Length + 1) - ext.Length;
        if (avail <= 0)
            return null;
        string s = MultiSpaceRegex().Replace(stem.Trim(), " ");
        while (s.Length > avail)
        {
            string trimmed = TrailingTokenRegex().Replace(s, "").TrimEnd();
            if (trimmed.Length == 0 || trimmed == s)
                break;
            s = trimmed;
        }
        if (s.Length > avail)
            s = s[..avail].TrimEnd();
        return s != stem ? s : null;
    }
}
