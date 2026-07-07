using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>One scanned dat's counts plus its location.</summary>
public sealed record DatCountResult(
    string Path, string Rel, string RelDir, string Name,
    int Games, int Roms, long Bytes, int Dirs, string Error);

/// <summary>One display row: a folder header or a dat entry.</summary>
public sealed class CounterRow
{
    private static readonly IBrush FolderBrush = new SolidColorBrush(Color.Parse("#4A9EDA"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush DatBrush = new SolidColorBrush(Color.Parse("#8A8F98"));

    public required bool IsFolder { get; init; }
    public DatCountResult? Result { get; init; }
    public required string DisplayName { get; init; }
    public int Depth { get; init; }

    public Avalonia.Thickness Indent => new(Depth * 18, 0, 0, 0);
    public bool IsError => Result is { Error.Length: > 0 };
    public string GamesText => Result is { Error.Length: 0 } r ? r.Games.ToString("N0") : IsError ? "ERR" : "";
    public string RomsText => Result is { Error.Length: 0 } r ? r.Roms.ToString("N0")
        : IsError ? (Result!.Error.Length > 60 ? Result.Error[..60] : Result.Error) : "";
    public string SizeText => Result is { Error.Length: 0 } r ? DatCounter.FmtSize(r.Bytes) : "";
    public IBrush RowBrush => IsFolder ? FolderBrush : IsError ? ErrorBrush : DatBrush;
    public FontWeight NameWeight => IsFolder ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>
/// State for the Game and ROM Counter: recursive dat scan, tree/flat views,
/// sorting, selection subtotals, and the collection summary.
/// </summary>
public partial class GameRomCounterViewModel : ViewModelBase
{
    private CancellationTokenSource? _cancel;
    private List<DatCountResult> _results = [];
    private string _sortCol = "";
    private bool _sortAsc = true;

    public ObservableCollection<CounterRow> Rows { get; } = [];

    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _viewTree = true;
    [ObservableProperty] private bool _viewFlat;
    [ObservableProperty] private string _statusText = "Enter a folder path and click Scan.";
    [ObservableProperty] private string _selectionInfo =
        "Select one or more dats (Ctrl+click / Shift+click) to see a subtotal.";

    // Collection summary
    [ObservableProperty] private string _totalDats = "—";
    [ObservableProperty] private string _rootFolders = "—";
    [ObservableProperty] private string _subFolders = "—";
    [ObservableProperty] private string _totalGames = "—";
    [ObservableProperty] private string _totalRoms = "—";
    [ObservableProperty] private string _totalSize = "—";
    [ObservableProperty] private string _avgGames = "—";
    [ObservableProperty] private string _avgRoms = "—";
    [ObservableProperty] private string _maxGames = "—";
    [ObservableProperty] private string _maxRoms = "—";
    [ObservableProperty] private string _emptyDats = "—";
    [ObservableProperty] private string _parseErrors = "—";

    partial void OnViewTreeChanged(bool v) { if (v) PopulateRows(); }
    partial void OnViewFlatChanged(bool v) { if (v) PopulateRows(); }

    public bool HasResults => _results.Count > 0;

    public void Stop() => _cancel?.Cancel();

    public async Task ScanAsync()
    {
        string path = FolderPath.Trim();
        if (path.Length == 0 || !Directory.Exists(path))
        {
            StatusText = "Please select a valid folder.";
            return;
        }

        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        IsScanning = true;
        StatusText = "Scanning...";
        Rows.Clear();
        _results = [];
        ClearSummary();

        try
        {
            var results = await Task.Run(() =>
            {
                var datFiles = DatValidator.CollectFiles(path, singleMode: false);
                Dispatcher.UIThread.Post(() =>
                    StatusText = $"Found {datFiles.Count} dat file(s) — parsing...");

                var list = new List<DatCountResult>();
                int done = 0;
                foreach (string fp in datFiles)
                {
                    if (token.IsCancellationRequested)
                        break;
                    var c = DatCounter.ScanDatCounts(fp);
                    string rel = Path.GetRelativePath(path, fp);
                    list.Add(new DatCountResult(fp, rel, Path.GetDirectoryName(rel) ?? "",
                                                c.DatName, c.Games, c.Roms, c.TotalBytes,
                                                c.DirCount, c.Error));
                    done++;
                    if (done % 50 == 0 || done == datFiles.Count)
                    {
                        int d = done;
                        Dispatcher.UIThread.Post(() =>
                            StatusText = $"Parsed {d}/{datFiles.Count}...");
                    }
                }
                return list;
            });

            _results = results;
            PopulateRows();
            UpdateSummary();
            string modeHint = ViewTree ? "Tree" : "Flat list";
            StatusText = $"{modeHint} — {_results.Count} dat(s). "
                + "Ctrl+click / Shift+click to select for subtotals.";
        }
        finally
        {
            IsScanning = false;
            _cancel = null;
        }
    }

    private void PopulateRows()
    {
        Rows.Clear();
        if (ViewFlat)
        {
            foreach (var r in _results)
                Rows.Add(DatRow(r, 0));
            return;
        }

        // Tree: folder header rows created on first encounter, dats indented
        var seenFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in _results)
        {
            if (r.RelDir.Length > 0 && !seenFolders.Contains(r.RelDir))
            {
                string[] parts = r.RelDir.Replace('\\', '/').Split('/');
                string current = "";
                for (int i = 0; i < parts.Length; i++)
                {
                    current = current.Length > 0 ? current + "/" + parts[i] : parts[i];
                    if (seenFolders.Add(current))
                        Rows.Add(new CounterRow
                        {
                            IsFolder = true,
                            DisplayName = "📁 " + parts[i],
                            Depth = i,
                        });
                }
            }
            int depth = r.RelDir.Length == 0 ? 0 : r.RelDir.Replace('\\', '/').Split('/').Length;
            Rows.Add(DatRow(r, depth));
        }
    }

    private static CounterRow DatRow(DatCountResult r, int depth) => new()
    {
        IsFolder = false,
        Result = r,
        DisplayName = (r.Error.Length > 0 ? "⚠ " + Path.GetFileName(r.Path) : r.Name),
        Depth = depth,
    };

    public void SortBy(string col)
    {
        if (_results.Count == 0)
            return;
        if (_sortCol == col)
            _sortAsc = !_sortAsc;
        else
        {
            _sortCol = col;
            _sortAsc = true;
        }
        Comparison<DatCountResult> cmp = col switch
        {
            "games" => (a, b) => a.Games.CompareTo(b.Games),
            "roms" => (a, b) => a.Roms.CompareTo(b.Roms),
            "size" => (a, b) => a.Bytes.CompareTo(b.Bytes),
            _ => (a, b) => string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant()),
        };
        _results.Sort(cmp);
        if (!_sortAsc)
            _results.Reverse();
        PopulateRows();
    }

    public void UpdateSelectionInfo(IReadOnlyList<CounterRow> selected)
    {
        int games = 0, roms = 0, count = 0;
        long bytes = 0;
        foreach (var row in selected)
        {
            if (row.Result is { Error.Length: 0 } r)
            {
                games += r.Games;
                roms += r.Roms;
                bytes += r.Bytes;
                count++;
            }
        }
        SelectionInfo = count > 0
            ? $"{count} dat(s) selected  —  Games: {games:N0}  |  ROMs: {roms:N0}  |  "
              + $"Size: {DatCounter.FmtSize(bytes)}  |  Avg games/dat: {(double)games / count:N1}"
            : "Select one or more dats (Ctrl+click / Shift+click) to see a subtotal.";
    }

    private void UpdateSummary()
    {
        var data = _results.Where(r => r.Error.Length == 0).ToList();
        if (data.Count == 0)
            return;
        int totalG = data.Sum(r => r.Games);
        int totalR = data.Sum(r => r.Roms);
        long totalB = data.Sum(r => r.Bytes);
        int totalD = _results.Sum(r => r.Dirs);
        int n = data.Count;

        int rootFolders = _results.Where(r => r.RelDir.Length > 0)
            .Select(r => r.RelDir.Replace('\\', '/').Split('/')[0])
            .Distinct(StringComparer.Ordinal).Count();
        if (rootFolders == 0 && _results.Any(r => r.RelDir.Length == 0))
            rootFolders = 1;

        var maxG = data.MaxBy(r => r.Games)!;
        var maxR = data.MaxBy(r => r.Roms)!;

        TotalDats = n.ToString("N0");
        RootFolders = rootFolders.ToString("N0");
        SubFolders = totalD.ToString("N0");
        TotalGames = totalG.ToString("N0");
        TotalRoms = totalR.ToString("N0");
        TotalSize = DatCounter.FmtSize(totalB);
        AvgGames = ((double)totalG / n).ToString("N1");
        AvgRoms = ((double)totalR / n).ToString("N1");
        MaxGames = $"{maxG.Games:N0}  ({Truncate(maxG.Name, 40)})";
        MaxRoms = $"{maxR.Roms:N0}  ({Truncate(maxR.Name, 40)})";
        EmptyDats = data.Count(r => r.Games == 0).ToString("N0");
        ParseErrors = _results.Count(r => r.Error.Length > 0).ToString("N0");
    }

    private static string Truncate(string s, int n) => s.Length > n ? s[..n] : s;

    private void ClearSummary()
    {
        TotalDats = RootFolders = SubFolders = TotalGames = TotalRoms = TotalSize =
            AvgGames = AvgRoms = MaxGames = MaxRoms = EmptyDats = ParseErrors = "—";
    }

    // ── Exports ──────────────────────────────────────────────────────────

    public List<string> SummaryLines() =>
    [
        "COLLECTION SUMMARY",
        new string('=', 40),
        "  Total dat files:          " + TotalDats,
        "  Root folders:             " + RootFolders,
        "  Internal <dir> folders:   " + SubFolders,
        "  Total games:              " + TotalGames,
        "  Total ROMs:               " + TotalRoms,
        "  Total uncompressed size:  " + TotalSize,
        "  Avg games / dat:          " + AvgGames,
        "  Avg ROMs / dat:           " + AvgRoms,
        "  Largest (games):          " + MaxGames,
        "  Largest (ROMs):           " + MaxRoms,
        "  Empty dats (0 games):     " + EmptyDats,
        "  Parse errors:             " + ParseErrors,
    ];

    public List<string> BuildLogLines()
    {
        var lines = SummaryLines();
        lines.AddRange(["", "", "DAT FILE LISTING", new string('=', 40)]);
        const int colW = 54;

        string FmtRow(DatCountResult r)
        {
            string name = Truncate(r.Name, colW);
            if (r.Error.Length > 0)
                return "  [ERR] " + Truncate(Path.GetFileName(r.Path), colW) + "  " + Truncate(r.Error, 60);
            return "  " + name.PadRight(colW) + "  "
                + ("Games: " + r.Games).PadRight(14)
                + ("ROMs: " + r.Roms).PadRight(14)
                + "Size: " + DatCounter.FmtSize(r.Bytes);
        }

        var folderMap = new SortedDictionary<string, List<DatCountResult>>(StringComparer.Ordinal);
        var rootDats = new List<DatCountResult>();
        foreach (var r in _results)
        {
            if (r.RelDir.Length > 0)
            {
                if (!folderMap.TryGetValue(r.RelDir, out var list))
                    folderMap[r.RelDir] = list = [];
                list.Add(r);
            }
            else
            {
                rootDats.Add(r);
            }
        }

        if (rootDats.Count > 0)
        {
            lines.AddRange(["", "[ Root folder ]", new string('-', 80)]);
            lines.AddRange(rootDats.Select(FmtRow));
        }
        foreach (var (folder, list) in folderMap)
        {
            lines.AddRange(["", "[ " + folder + " ]", new string('-', 80)]);
            lines.AddRange(list.Select(FmtRow));
        }

        lines.AddRange(["", "Generated by Datfile Creator Studio  "
            + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")]);
        return lines;
    }

    public string BuildCsv()
    {
        static string Q(string s) =>
            s.Contains(',') || s.Contains('"') || s.Contains('\n')
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        var sb = new StringBuilder();
        sb.AppendLine("Dat Name,Games,ROMs,Uncompressed Size,Bytes,Relative Path,Error");
        foreach (var r in _results)
            sb.AppendLine(string.Join(",",
                Q(r.Name), r.Games, r.Roms, Q(DatCounter.FmtSize(r.Bytes)),
                r.Bytes, Q(r.Rel), Q(r.Error)));
        return sb.ToString();
    }
}
