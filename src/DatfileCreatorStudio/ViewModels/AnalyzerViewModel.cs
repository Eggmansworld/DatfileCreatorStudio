using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>
/// State for the Folder Structure Analyzer window: runs the structure and
/// path-length scans on a background task and renders the colour-coded
/// findings report, recommendation, and Apply support.
/// </summary>
public partial class AnalyzerViewModel : ViewModelBase
{
    private static readonly IBrush HeadBrush = new ImmutableSolidColorBrush(Color.Parse("#4A9EDA"));
    private static readonly IBrush GoodBrush = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush WarnBrush = new ImmutableSolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IBrush CritBrush = new ImmutableSolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush StatBrush = new ImmutableSolidColorBrush(Color.Parse("#8A8F98"));
    private static readonly IBrush RecHighBrush = new ImmutableSolidColorBrush(Color.Parse("#2EC96A"));

    private readonly MainWindowViewModel _main;
    private CancellationTokenSource? _cancel;

    public ObservableCollection<LogLine> ReportLines { get; } = [];

    /// <summary>The last completed analysis, or null.</summary>
    public AnalyzerFindings? Result { get; private set; }

    public AnalyzerViewModel(MainWindowViewModel main)
    {
        _main = main;
        _folderPath = main.InputRoot;
        _typeMixed = main.IsMixed;
        _typeZipped = !main.IsMixed;
    }

    [ObservableProperty] private string _folderPath;
    [ObservableProperty] private bool _typeMixed;
    [ObservableProperty] private bool _typeZipped;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _canApply;
    [ObservableProperty] private bool _canSaveLog;
    [ObservableProperty] private bool _canOpenRepair;
    [ObservableProperty] private string _recommendationText = "Run an analysis to see a recommendation.";

    private void AddLine(string text, IBrush brush) =>
        Dispatcher.UIThread.Post(() => ReportLines.Add(new LogLine(text, brush)));

    public void CancelScan() => _cancel?.Cancel();

    public async Task RunAnalyzeAsync()
    {
        string path = FolderPath.Trim();
        if (path.Length == 0 || !Directory.Exists(path))
        {
            ReportLines.Clear();
            ReportLines.Add(new LogLine("Please select a valid folder to analyze.", CritBrush));
            return;
        }

        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        string datType = TypeMixed ? "mixed" : "zipped";

        ReportLines.Clear();
        Result = null;
        CanApply = false;
        CanSaveLog = false;
        CanOpenRepair = false;
        IsScanning = true;
        AddLine("Scanning folder structure — please wait...", StatBrush);
        AddLine("(Folder names will appear below as each is completed.)", StatBrush);
        AddLine("", StatBrush);

        try
        {
            var result = await Task.Run(() =>
            {
                var findings = FolderAnalysis.Analyze(path, datType,
                    progress: (name, count) =>
                        AddLine($"  Scanned: {name} ({count} items)", StatBrush),
                    cancel: token);
                if (!token.IsCancellationRequested)
                {
                    AddLine("", StatBrush);
                    AddLine("Scanning path lengths — please wait...", StatBrush);
                    findings.PathStats = FolderAnalysis.CollectPathLengths(path, token);
                }
                return findings;
            });

            Result = result;
            var ps = result.PathStats;
            CanSaveLog = ps is not null;
            CanOpenRepair = ps is not null && (ps.WarnCount > 0 || ps.CritCount > 0);
            AddLine("", StatBrush);
            AddLine("Scan complete.", HeadBrush);
            AddLine("", StatBrush);
            Display(result);
        }
        catch (Exception ex)
        {
            ReportLines.Add(new LogLine("ERROR during analysis: " + ex.Message, CritBrush));
            RecommendationText = "Analysis failed — see error above.";
        }
        finally
        {
            IsScanning = false;
            _cancel = null;
        }
    }

    // ── Findings report (port of AnalyzerWindow._display) ────────────────

    private void Display(AnalyzerFindings r)
    {
        var rec = r.Recommendation;
        string cw = r.DatType == "zipped" ? "zip archives" : "files";
        void W(string text, IBrush brush) => ReportLines.Add(new LogLine(text, brush));

        W("FOLDER STRUCTURE ANALYSIS", HeadBrush);
        W("Path : " + r.RootPath, StatBrush);
        W("Type : " + (r.DatType == "zipped" ? "Zipped" : "Mixed (Archive as File)"), StatBrush);
        W("", StatBrush);

        W("STATISTICS", HeadBrush);
        W("  Top-level folders : " + r.TopFolders, StatBrush);
        W("  Total " + cw.PadRight(16) + "  : " + r.TotalItems, StatBrush);
        W("  Max folder depth  : " + r.MaxDepth, StatBrush);

        if (r.DepthHistogram.Count > 0)
        {
            W("  Depth distribution :", StatBrush);
            var labelMap = new Dictionary<int, string>
            {
                [1] = "flat (items only)", [2] = "one subdir level",
                [3] = "two levels deep", [4] = "three levels deep",
            };
            foreach (var (d, count) in r.DepthHistogram)
            {
                string bar = new('█', Math.Min(count, 30));
                string lbl = labelMap.GetValueOrDefault(d, d + " levels deep");
                W($"    depth {d}  {lbl,-22}{count,4} folder(s)  {bar}", StatBrush);
            }
        }

        W("", StatBrush);
        W("PATTERN BREAKDOWN", HeadBrush);
        foreach (var (label, count, desc, brush) in (ReadOnlySpan<(string, int, string, IBrush)>)
        [
            ("Flat game folders", r.FoldersFlatGames, "items directly, no subdirs", GoodBrush),
            ("Games with subdirs", r.FoldersWithNestedSubdirs, "items directly + physical subdirs", GoodBrush),
            ("Container folders", r.FoldersAsContainers, "no direct items, subdirs only", WarnBrush),
            ("Empty folders", r.FoldersEmpty, "no items found", WarnBrush),
        ])
        {
            if (count > 0)
                W($"  {label,-24}{count,4}   ({desc})", brush);
        }

        if (r.Nodes.Count > 0)
        {
            W("", StatBrush);
            W("SAMPLE FOLDERS (first 6)", HeadBrush);
            foreach (var node in r.Nodes.Take(6))
            {
                string name = Path.GetFileName(node.Path);
                name = name.Length > 50 ? name[..50] : name;
                W($"  {name,-52}items={node.DirectItems,-4}subdirs={node.DirectSubdirs,-4}depth={node.MaxDepth}",
                  StatBrush);
            }
        }

        if (r.Notes.Count > 0)
        {
            W("", StatBrush);
            W("NOTES", HeadBrush);
            foreach (string note in r.Notes)
            {
                W("  " + note, WarnBrush);
                W("", StatBrush);
            }
        }

        // ── Path Length Analysis ─────────────────────────────────────────
        if (r.PathStats is { } ps)
        {
            W("", StatBrush);
            W("PATH LENGTH ANALYSIS", HeadBrush);
            W("  Thresholds  : >=200 chars = [Warning]   >=260 chars = [Critical]", StatBrush);
            W("  Total paths : " + ps.TotalPaths, StatBrush);
            W("  Max length  : " + ps.MaxPathLen + " chars", StatBrush);

            if (ps.CritCount > 0)
                W($"  >= 260 chars: {ps.CritCount,5}  [CRITICAL] — exceed Windows MAX_PATH", CritBrush);
            else
                W($"  >= 260 chars: {0,5}  (none — below critical threshold)", GoodBrush);

            if (ps.WarnCount > 0)
                W($"  >= 200 chars: {ps.WarnCount,5}  [Warning]  — review for dat compatibility", WarnBrush);
            else
                W($"  >= 200 chars: {0,5}  (none — all paths within safe range)", GoodBrush);

            var top = ps.CritPaths.Concat(ps.WarnPaths)
                        .OrderByDescending(x => x.Length).Take(10).ToList();
            if (top.Count > 0)
            {
                W("", StatBrush);
                W("  Top longest paths:", StatBrush);
                foreach (var (plen, pstr) in top)
                {
                    if (plen >= FolderAnalysis.CritLimit)
                        W($"    [Critical] ({plen} chars)  {pstr}", CritBrush);
                    else
                        W($"    [Warning]  ({plen} chars)  {pstr}", WarnBrush);
                }
            }

            int totalIssues = ps.WarnCount + ps.CritCount;
            if (totalIssues > 0)
            {
                W("", StatBrush);
                if (totalIssues > 10)
                    W($"  ({totalIssues - 10} more — use Save Analysis Log to see all)", WarnBrush);
                else
                    W("  Use \"Save Analysis Log\" to export the full path list.", WarnBrush);
            }
            else
            {
                W("", StatBrush);
                W("  No path length issues found.", GoodBrush);
            }
        }

        if (rec.Confidence != "none")
        {
            W("", StatBrush);
            W("RECOMMENDATION", HeadBrush);
            W("  " + rec.Summary, rec.Confidence == "high" ? RecHighBrush : WarnBrush);
            W("", StatBrush);
            foreach (string line in rec.Detail)
            {
                W("  " + line, StatBrush);
                W("", StatBrush);
            }
            RecommendationText = rec.Summary;
            CanApply = true;
        }
        else
        {
            RecommendationText = rec.Summary.Length > 0 ? rec.Summary
                : "Could not determine recommendation.";
            CanApply = false;
        }
    }

    // ── Apply recommended settings to the main window ────────────────────

    /// <summary>Returns a confirmation summary, or null when nothing to apply.</summary>
    public string? ApplyToMainWindow()
    {
        if (Result is not { } r || r.Recommendation.Confidence == "none")
            return null;
        var rec = r.Recommendation;

        _main.DatTypeMixed = r.DatType == "mixed";
        _main.DatTypeZipped = r.DatType == "zipped";
        _main.GenPerTop = rec.GenMode == "per_top";
        _main.GenPerRoot = rec.GenMode == "per_root";
        _main.GenPerAll = rec.GenMode == "per_all";
        _main.StructOpt1 = rec.Structure == "opt1";
        _main.StructOpt2 = rec.Structure == "opt2";
        _main.StructOpt3 = rec.Structure == "opt3";
        _main.StructOpt4 = rec.Structure == "opt4";
        _main.FormatModern = true;
        _main.FormatLegacy = false;
        _main.InclGameDesc = true;
        _main.InputRoot = r.RootPath;

        string summary = "Analyzer settings applied: "
            + (r.DatType == "mixed" ? "Mixed" : "Zipped")
            + " | " + rec.GenMode + " | " + rec.Structure + " | Modern. "
            + "Review the output folder and header fields before clicking Start.";
        _main.Drawer.ReportInfo(summary);
        return summary;
    }

    // ── Analysis log (port of _save_analysis_log) ────────────────────────

    public List<string> BuildAnalysisLog()
    {
        var lines = new List<string>();
        if (Result is not { } r)
            return lines;
        var ps = r.PathStats;
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string sep = new('=', 72);
        string cw = r.DatType == "zipped" ? "zip archives" : "files";

        lines.AddRange(
        [
            "Datfile Creator Studio — Folder Structure & Path Length Analysis",
            "Generated  : " + now,
            "Root path  : " + r.RootPath,
            "Type       : " + (r.DatType == "zipped" ? "Zipped (zip contents)" : "Mixed (Archive as File)"),
            "",
            sep,
            "FOLDER STRUCTURE STATISTICS",
            sep,
            "  Top-level folders   : " + r.TopFolders,
            "  Total " + cw.PadRight(14) + ": " + r.TotalItems,
            "  Max folder depth    : " + r.MaxDepth,
            "",
        ]);
        if (r.DepthHistogram.Count > 0)
        {
            lines.Add("  Depth distribution:");
            var labelMap = new Dictionary<int, string>
            {
                [1] = "flat (items only)", [2] = "one subdir level",
                [3] = "two levels deep", [4] = "three levels deep",
            };
            foreach (var (d, cnt) in r.DepthHistogram)
                lines.Add($"    depth {d}  {labelMap.GetValueOrDefault(d, d + " levels deep"),-22}{cnt,4} folder(s)");
            lines.Add("");
        }

        var rec = r.Recommendation;
        if (rec.Confidence != "none")
        {
            lines.Add("RECOMMENDATION : " + rec.Summary);
            foreach (string detail in rec.Detail)
                lines.Add("  " + detail);
            lines.Add("");
        }
        if (r.Notes.Count > 0)
        {
            lines.Add("NOTES:");
            foreach (string note in r.Notes)
                lines.Add("  " + note);
            lines.Add("");
        }

        if (ps is not null)
        {
            lines.AddRange(
            [
                sep,
                "PATH LENGTH ANALYSIS",
                sep,
                "  Thresholds  : >=200 chars = [Warning]   >=260 chars = [Critical]",
                "  Total paths : " + ps.TotalPaths,
                "  Max length  : " + ps.MaxPathLen + " chars",
                "  >= 200 chars: " + ps.WarnCount + "  [Warning]",
                "  >= 260 chars: " + ps.CritCount + "  [Critical]",
                "",
            ]);

            if (ps.WarnCount == 0 && ps.CritCount == 0)
            {
                lines.Add("  All paths are within the 200-character safe threshold. No issues found.");
                lines.Add("");
            }
            else
            {
                string dash = new('-', 72);
                if (ps.CritPaths.Count > 0)
                {
                    lines.AddRange([dash,
                        $"[Critical] — Paths >= 260 characters  ({ps.CritPaths.Count} found)", dash]);
                    foreach (var (plen, pstr) in ps.CritPaths)
                        lines.Add($"[Critical] ({plen} chars)  {pstr}");
                    lines.Add("");
                }
                if (ps.WarnPaths.Count > 0)
                {
                    lines.AddRange([dash,
                        $"[Warning]  — Paths 200–259 characters  ({ps.WarnPaths.Count} found)", dash]);
                    foreach (var (plen, pstr) in ps.WarnPaths)
                        lines.Add($"[Warning]  ({plen} chars)  {pstr}");
                    lines.Add("");
                }
            }
        }
        return lines;
    }
}
