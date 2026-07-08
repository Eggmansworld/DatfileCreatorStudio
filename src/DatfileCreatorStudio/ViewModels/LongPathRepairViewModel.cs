using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>One flagged path row in the Long Path Repair tool.</summary>
public sealed partial class RepairItem : ObservableObject
{
    private static readonly IBrush CritBrush = new ImmutableSolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush WarnBrush = new ImmutableSolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IBrush PendingBrush = new ImmutableSolidColorBrush(Color.Parse("#4A9EDA"));
    private static readonly IBrush AppliedBrush = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));

    public required string OrigPath { get; init; }
    public required string Severity { get; init; } // "crit" | "warn"
    public int OrigLength { get; init; }

    public string CurrentPath { get; set; } = "";
    public string DirPart { get; set; } = "";
    public string Stem { get; set; } = "";
    public string Ext { get; set; } = "";
    public string? PendingStem { get; set; }
    public string Status { get; set; } = "ok"; // ok | pending | applied | error
    public string ErrorMsg { get; set; } = "";

    public string? PendingPath =>
        PendingStem is null ? null : Path.Combine(DirPart, PendingStem + Ext);

    // ── Row display bindings ─────────────────────────────────────────────

    public string StatusText => Status switch
    {
        "pending" => "● Pending",
        "applied" => "✔ Applied",
        "error" => "✘ " + (ErrorMsg.Length > 0 ? ErrorMsg : "Error"),
        _ => "",
    };

    public string LengthText => "[" + (PendingPath ?? CurrentPath).Length + "]";

    public string DisplayStem => PendingStem ?? Stem;

    public IBrush RowBrush => Status switch
    {
        "error" => CritBrush,
        "applied" => AppliedBrush,
        "pending" => PendingBrush,
        _ => Severity == "crit" ? CritBrush : WarnBrush,
    };

    /// <summary>Re-evaluate every computed binding after state changes.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// State for the Long Path Length Repair tool: flagged paths, filtering,
/// sorting, the rename/preview/apply/undo flow, and the rename log.
/// </summary>
public partial class LongPathRepairViewModel : ViewModelBase
{
    public const int DefaultTarget = 190;

    private sealed record UndoRecord(
        bool IsFolder, string OldPath, string NewPath,
        RepairItem? Item, List<RepairItem>? Affected);

    private readonly List<RepairItem> _all = [];
    private readonly List<UndoRecord> _undoStack = [];
    private readonly List<(string Timestamp, string Old, string New)> _renameLog = [];

    public ObservableCollection<RepairItem> Visible { get; } = [];

    public LongPathRepairViewModel(PathLengthStats? stats = null)
    {
        if (stats is not null)
            LoadFromPathStats(stats);
    }

    // ── Filters / sorting / stats ────────────────────────────────────────

    [ObservableProperty] private bool _filterAll = true;
    [ObservableProperty] private bool _filterWarn;
    [ObservableProperty] private bool _filterCrit;
    [ObservableProperty] private bool _filterPending;

    partial void OnFilterAllChanged(bool value) { if (value) Repopulate(); }
    partial void OnFilterWarnChanged(bool value) { if (value) Repopulate(); }
    partial void OnFilterCritChanged(bool value) { if (value) Repopulate(); }
    partial void OnFilterPendingChanged(bool value) { if (value) Repopulate(); }

    private string _sortCol = "length";
    private bool _sortRev;

    [ObservableProperty] private string _statsText = "No paths loaded.";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _targetText = DefaultTarget.ToString();
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _isScanning;

    // ── Edit panel state ─────────────────────────────────────────────────

    private RepairItem? _selected;
    private bool _inhibitStemEvents;

    [ObservableProperty] private string _editDirText = "(select a row to edit)";
    [ObservableProperty] private string _editExtText = "";
    [ObservableProperty] private string _editStem = "";
    [ObservableProperty] private string _currentLenText = "";
    [ObservableProperty] private string _newLenText = "New: —";
    [ObservableProperty] private IBrush _newLenBrush = new ImmutableSolidColorBrush(Color.Parse("#8A8F98"));
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _canApplyThis;

    public RepairItem? Selected => _selected;

    /// <summary>Called by the view when the list selection changes.</summary>
    public void SetSelected(RepairItem? item)
    {
        AutoSaveIfDirty();
        _selected = item;
        UpdateEditPanel();
    }

    private void UpdateEditPanel()
    {
        if (_selected is not { } item)
        {
            EditDirText = "(select a row to edit)";
            EditExtText = "";
            CurrentLenText = "";
            NewLenText = "New: —";
            HasSelection = false;
            CanApplyThis = false;
            _inhibitStemEvents = true;
            EditStem = "";
            _inhibitStemEvents = false;
            return;
        }

        string dirDisplay = item.DirPart;
        if (!dirDisplay.EndsWith('\\') && !dirDisplay.EndsWith('/'))
            dirDisplay += Path.DirectorySeparatorChar;
        EditDirText = dirDisplay;
        EditExtText = item.Ext.Length > 0 ? item.Ext + "  — locked" : "(no extension)";
        CurrentLenText = "Current: [" + item.CurrentPath.Length + "]";
        HasSelection = true;
        CanApplyThis = item.Status == "pending";

        _inhibitStemEvents = true;
        EditStem = item.DisplayStem;
        _inhibitStemEvents = false;
        ComputeNewLength(item, item.DisplayStem);
    }

    partial void OnEditStemChanged(string value)
    {
        if (_inhibitStemEvents || _selected is not { } item)
            return;
        ComputeNewLength(item, value);
    }

    private void ComputeNewLength(RepairItem item, string stem)
    {
        int n = Path.Combine(item.DirPart, stem + item.Ext).Length;
        if (n >= FolderAnalysis.CritLimit)
        {
            NewLenText = $"New: [{n}] CRITICAL";
            NewLenBrush = new ImmutableSolidColorBrush(Color.Parse("#E5484D"));
        }
        else if (n >= FolderAnalysis.WarnLimit)
        {
            NewLenText = $"New: [{n}] Warning";
            NewLenBrush = new ImmutableSolidColorBrush(Color.Parse("#E8A33D"));
        }
        else
        {
            NewLenText = $"New: [{n}] OK ✓";
            NewLenBrush = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));
        }
    }

    // ── Data loading ─────────────────────────────────────────────────────

    public void LoadFromPathStats(PathLengthStats stats)
    {
        _all.Clear();
        foreach (var (plen, pstr) in stats.CritPaths)
            _all.Add(MakeItem("crit", plen, pstr));
        foreach (var (plen, pstr) in stats.WarnPaths)
            _all.Add(MakeItem("warn", plen, pstr));
        Repopulate();
        UpdateStats();
        StatusMessage = _all.Count + " flagged path(s) loaded.";
    }

    private static RepairItem MakeItem(string severity, int plen, string pstr)
    {
        string fname = Path.GetFileName(pstr);
        var (stem, ext) = FolderAnalysis.SplitStemExt(fname);
        return new RepairItem
        {
            OrigPath = pstr,
            Severity = severity,
            OrigLength = plen,
            CurrentPath = pstr,
            DirPart = Path.GetDirectoryName(pstr) ?? "",
            Stem = stem,
            Ext = ext,
        };
    }

    /// <summary>Scan a folder directly (superset of the suite, which only imports).</summary>
    public async Task ScanFolderAsync(string folderPath)
    {
        IsScanning = true;
        StatusMessage = "Scanning path lengths...";
        try
        {
            var stats = await Task.Run(() => FolderAnalysis.CollectPathLengths(folderPath));
            LoadFromPathStats(stats);
            if (stats.WarnCount == 0 && stats.CritCount == 0)
                StatusMessage = "No paths at or above 200 characters — nothing to repair.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private static readonly Regex LogLineRegex = new(
        @"^\[(Critical|Warning)\]\s+\((\d+)\s+chars\)\s+(.+)$", RegexOptions.Compiled);

    /// <summary>Parse a saved analysis log. Returns the number of paths loaded.</summary>
    public int ImportFromLogText(IEnumerable<string> lines)
    {
        var entries = new List<(string Sev, int Len, string Path)>();
        foreach (string line in lines)
        {
            var m = LogLineRegex.Match(line.Trim());
            if (m.Success)
                entries.Add((m.Groups[1].Value == "Critical" ? "crit" : "warn",
                             int.Parse(m.Groups[2].Value), m.Groups[3].Value.Trim()));
        }
        if (entries.Count == 0)
        {
            StatusMessage = "No [Critical] or [Warning] paths found in this log.";
            return 0;
        }
        _all.Clear();
        foreach (var (sev, plen, pstr) in entries)
            _all.Add(MakeItem(sev, plen, pstr));
        Repopulate();
        UpdateStats();
        StatusMessage = entries.Count + " path(s) loaded from log.";
        return entries.Count;
    }

    // ── List population / sorting ────────────────────────────────────────

    private void Repopulate()
    {
        var visible = _all.Where(it =>
            !(FilterWarn && it.Severity != "warn")
            && !(FilterCrit && it.Severity != "crit")
            && !(FilterPending && it.Status != "pending")).ToList();

        visible.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(SortKey(a), SortKey(b));
            if (_sortCol == "length" || _sortCol == "status")
                c = SortNum(a).CompareTo(SortNum(b));
            return _sortRev ? -c : c;
        });

        Visible.Clear();
        foreach (var it in visible)
            Visible.Add(it);
    }

    private string SortKey(RepairItem it) => _sortCol switch
    {
        "directory" => it.DirPart.ToLowerInvariant(),
        "filename" => it.DisplayStem.ToLowerInvariant(),
        "ext" => it.Ext.ToLowerInvariant(),
        _ => "",
    };

    private int SortNum(RepairItem it) => _sortCol switch
    {
        "length" => (it.PendingPath ?? it.CurrentPath).Length,
        "status" => it.Status switch { "error" => 0, "pending" => 1, "ok" => 2, _ => 3 },
        _ => 0,
    };

    public void SortBy(string col)
    {
        if (_sortCol == col)
            _sortRev = !_sortRev;
        else
        {
            _sortCol = col;
            _sortRev = false;
        }
        Repopulate();
    }

    // ── Edit actions ─────────────────────────────────────────────────────

    private bool ConflictExists(RepairItem self, string dirPart, string newFilename)
    {
        string nl = newFilename.ToLowerInvariant();
        string dl = dirPart.ToLowerInvariant();
        return _all.Any(it => !ReferenceEquals(it, self)
            && it.DirPart.ToLowerInvariant() == dl
            && it.Status == "pending" && it.PendingStem is not null
            && (it.PendingStem + it.Ext).ToLowerInvariant() == nl);
    }

    /// <summary>Silently mark the current row Pending when the stem was edited but not previewed.</summary>
    public void AutoSaveIfDirty()
    {
        if (_selected is not { } item)
            return;
        string entry = EditStem.Trim();
        if (entry.Length == 0 || entry == item.DisplayStem)
            return;
        if (ConflictExists(item, item.DirPart, entry + item.Ext))
            return;
        item.PendingStem = entry;
        item.Status = "pending";
        item.Refresh();
        UpdateStats();
    }

    public void PreviewEdit()
    {
        if (_selected is not { } item)
            return;
        string newStem = EditStem.Trim();
        if (newStem.Length == 0)
        {
            StatusMessage = "The filename stem cannot be empty.";
            return;
        }
        if (ConflictExists(item, item.DirPart, newStem + item.Ext))
        {
            StatusMessage = $"'{newStem}{item.Ext}' is already in use by another pending rename in the same directory.";
            return;
        }
        item.PendingStem = newStem;
        item.Status = "pending";
        item.Refresh();
        CanApplyThis = true;
        UpdateStats();
        StatusMessage = "";
    }

    public void ClearEdit(IReadOnlyList<RepairItem> targets)
    {
        IReadOnlyList<RepairItem> items = targets.Count > 0 ? targets
            : _selected is { } s ? new[] { s } : Array.Empty<RepairItem>();
        foreach (var item in items)
        {
            item.PendingStem = null;
            if (item.Status is "pending" or "error")
            {
                item.Status = "ok";
                item.ErrorMsg = "";
            }
            item.Refresh();
        }
        UpdateEditPanel();
        UpdateStats();
    }

    public void AutoSuggest(IReadOnlyList<RepairItem> targets)
    {
        IReadOnlyList<RepairItem> items = targets.Count > 0 ? targets
            : _selected is { } s ? new[] { s } : Array.Empty<RepairItem>();
        if (items.Count == 0)
            return;
        int target = int.TryParse(TargetText.Trim(), out int t) && t is >= 100 and <= 259
            ? t : DefaultTarget;

        int modified = 0, noChange = 0, dirTooLong = 0;
        foreach (var item in items)
        {
            if (item.Status == "applied")
                continue;
            string? result = FolderAnalysis.ComputeSuggestion(item.DirPart, item.Stem, item.Ext, target);
            if (result is null)
            {
                if (target - (item.DirPart.Length + 1 + item.Ext.Length) <= 0)
                    dirTooLong++;
                else
                    noChange++;
                continue;
            }
            item.PendingStem = result;
            item.Status = "pending";
            item.Refresh();
            modified++;
        }

        UpdateEditPanel();
        UpdateStats();

        var parts = new List<string> { modified + " filename(s) set to Pending." };
        if (noChange > 0)
            parts.Add(noChange + " already within target.");
        if (dirTooLong > 0)
            parts.Add(dirTooLong + " skipped — directory path alone exceeds target (folder rename needed).");
        StatusMessage = string.Join("  ", parts);
    }

    // ── Apply / undo ─────────────────────────────────────────────────────

    public void ApplyItems(IReadOnlyList<RepairItem> targets)
    {
        var pending = targets.Where(it => it.Status == "pending").ToList();
        if (pending.Count == 0)
        {
            StatusMessage = "No pending renames to apply.";
            return;
        }

        int applied = 0, errors = 0;
        foreach (var item in pending)
        {
            string oldPath = item.CurrentPath;
            string newPath = Path.Combine(item.DirPart, item.PendingStem + item.Ext);
            try
            {
                // File.Move handles files; Directory.Move handles flagged dirs
                if (Directory.Exists(oldPath))
                    Directory.Move(oldPath, newPath);
                else
                    File.Move(oldPath, newPath);
                _undoStack.Add(new UndoRecord(false, oldPath, newPath, item, null));
                _renameLog.Add((DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), oldPath, newPath));
                item.CurrentPath = newPath;
                item.Stem = item.PendingStem!;
                item.PendingStem = null;
                item.Status = "applied";
                applied++;
            }
            catch (Exception ex)
            {
                item.Status = "error";
                item.ErrorMsg = ex.Message;
                errors++;
            }
            item.Refresh();
        }
        UpdateEditPanel();
        UpdateStats();
        CanUndo = _undoStack.Count > 0;
        StatusMessage = applied + " rename(s) applied."
            + (errors > 0 ? $"  {errors} error(s) — see list for details." : "");
    }

    public IReadOnlyList<RepairItem> AllPending() =>
        _all.Where(it => it.Status == "pending").ToList();

    public void UndoLast()
    {
        if (_undoStack.Count == 0)
            return;
        var rec = _undoStack[^1];
        try
        {
            if (!rec.IsFolder)
            {
                if (Directory.Exists(rec.NewPath))
                    Directory.Move(rec.NewPath, rec.OldPath);
                else
                    File.Move(rec.NewPath, rec.OldPath);
                if (rec.Item is { } item)
                {
                    var (stem, _) = FolderAnalysis.SplitStemExt(Path.GetFileName(rec.OldPath));
                    item.CurrentPath = rec.OldPath;
                    item.Stem = stem;
                    item.PendingStem = null;
                    item.Status = "ok";
                    item.ErrorMsg = "";
                    item.Refresh();
                }
            }
            else
            {
                Directory.Move(rec.NewPath, rec.OldPath);
                RewriteAffected(rec.Affected ?? [], rec.NewPath, rec.OldPath, resetStatus: true);
            }
            _undoStack.RemoveAt(_undoStack.Count - 1);
            UpdateEditPanel();
            UpdateStats();
            CanUndo = _undoStack.Count > 0;
            StatusMessage = "Undid: " + Path.GetFileName(rec.NewPath) + " → " + Path.GetFileName(rec.OldPath);
        }
        catch (Exception ex)
        {
            StatusMessage = "Undo error: " + ex.Message;
        }
    }

    // ── Rename parent folder ─────────────────────────────────────────────

    /// <summary>Returns an error/status message after attempting the folder rename.</summary>
    public string RenameParentFolder(string newName)
    {
        if (_selected is not { } item)
            return "Select a row first.";
        string oldDir = item.DirPart;
        string folderName = Path.GetFileName(oldDir);
        newName = newName.Trim();
        if (newName.Length == 0 || newName == folderName)
            return "";
        string newDir = Path.Combine(Path.GetDirectoryName(oldDir) ?? "", newName);
        if (File.Exists(newDir) || Directory.Exists(newDir))
            return $"A folder named '{newName}' already exists at that location.";

        var affected = _all.Where(it => it.DirPart == oldDir
            || it.DirPart.StartsWith(oldDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();

        try
        {
            Directory.Move(oldDir, newDir);
        }
        catch (Exception ex)
        {
            return "Rename error: " + ex.Message;
        }

        _renameLog.Add((DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), oldDir, newDir));
        _undoStack.Add(new UndoRecord(true, oldDir, newDir, null, affected));
        RewriteAffected(affected, oldDir, newDir, resetStatus: false);
        UpdateEditPanel();
        UpdateStats();
        CanUndo = true;
        return $"Renamed to: {newDir}  —  {affected.Count} path(s) updated. "
            + "Tip: re-run the Analyzer for a fresh path length picture after folder renames.";
    }

    private static void RewriteAffected(List<RepairItem> affected, string oldDir, string newDir, bool resetStatus)
    {
        foreach (var it in affected)
        {
            if (it.CurrentPath.StartsWith(oldDir, StringComparison.Ordinal))
                it.CurrentPath = newDir + it.CurrentPath[oldDir.Length..];
            if (it.DirPart == oldDir)
                it.DirPart = newDir;
            else if (it.DirPart.StartsWith(oldDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                it.DirPart = newDir + it.DirPart[oldDir.Length..];
            if (resetStatus)
                it.Status = "ok";
            it.Refresh();
        }
    }

    // ── Rename log ───────────────────────────────────────────────────────

    public bool HasRenames => _renameLog.Count > 0;

    public List<string> BuildRenameLog()
    {
        var lines = new List<string>
        {
            "Datfile Creator Studio — Path Repair Rename Log",
            "Generated  : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "",
            new string('=', 72),
            _renameLog.Count + " rename(s) recorded:",
            new string('=', 72),
            "",
        };
        foreach (var (ts, oldP, newP) in _renameLog)
        {
            lines.Add("[" + ts + "]");
            lines.Add("  OLD: " + oldP);
            lines.Add("  NEW: " + newP);
            lines.Add("");
        }
        return lines;
    }

    // ── Stats ────────────────────────────────────────────────────────────

    private void UpdateStats()
    {
        int total = _all.Count;
        int warns = _all.Count(it => it.Severity == "warn");
        int crits = _all.Count(it => it.Severity == "crit");
        int pending = _all.Count(it => it.Status == "pending");
        int applied = _all.Count(it => it.Status == "applied");
        int errors = _all.Count(it => it.Status == "error");
        var parts = new List<string>
        {
            "Total: " + total, "Warning: " + warns, "Critical: " + crits,
        };
        if (pending > 0) parts.Add("Pending: " + pending);
        if (applied > 0) parts.Add("Applied: " + applied);
        if (errors > 0) parts.Add("Errors: " + errors);
        StatsText = string.Join("  —  ", parts);
        if (FilterPending)
            Repopulate();
    }
}
