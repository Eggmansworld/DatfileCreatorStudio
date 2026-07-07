using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>One optional header field row (value box + Clear checkbox).</summary>
public partial class BhuFieldRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Label { get; init; }

    [ObservableProperty] private string _value = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValueEnabled))]
    private bool _clear;

    public bool IsValueEnabled => !Clear;
}

/// <summary>
/// State for the Bulk Datfile Header Updater: field grid, source path, and
/// the background run streaming colour-coded results.
/// </summary>
public partial class BulkHeaderUpdaterViewModel : ViewModelBase
{
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IBrush ErrBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#8A8F98"));

    private CancellationTokenSource? _cancel;
    private readonly List<string> _logLines = [];

    public ObservableCollection<BhuFieldRow> Fields { get; } = [];
    public ObservableCollection<LogLine> Lines { get; } = [];

    public BulkHeaderUpdaterViewModel()
    {
        foreach (string f in BulkHeaderUpdater.OptionalFields)
            Fields.Add(new BhuFieldRow { Name = f, Label = BulkHeaderUpdater.FieldLabels[f] });
        _newDate = DateTime.Now.ToString("yyyy-MM-dd");
    }

    [ObservableProperty] private string _newDate;
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private bool _addForcePacking;
    [ObservableProperty] private bool _isRunning;

    public IReadOnlyList<string> LogLinesSnapshot => _logLines;

    private void Post(string text, IBrush brush)
    {
        Dispatcher.UIThread.Post(() => Lines.Add(new LogLine(text, brush)));
        _logLines.Add(text);
    }

    public void Stop() => _cancel?.Cancel();

    public void ClearLog()
    {
        Lines.Clear();
        _logLines.Clear();
    }

    public async Task RunAsync()
    {
        string newDate = NewDate.Trim();
        string target = SourcePath.Trim().Trim('"');

        if (!BulkHeaderUpdater.ValidateDate(newDate))
        {
            Post("Enter a valid date in YYYY-MM-DD format.", ErrBrush);
            return;
        }
        if (target.Length == 0 || (!File.Exists(target) && !Directory.Exists(target)))
        {
            Post("Select an existing dat file or folder first.", ErrBrush);
            return;
        }

        var fieldValues = new Dictionary<string, string?>();
        foreach (var row in Fields)
        {
            string val = row.Value.Trim();
            fieldValues[row.Name] = row.Clear ? "" : val.Length > 0 ? val : null;
        }
        bool addFp = AddForcePacking;

        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        IsRunning = true;

        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Post("", DimBrush);
        Post($"=== RUN {stamp} ===", DimBrush);
        Post($"Target : {target}", DimBrush);
        Post($"Date   : {newDate}", DimBrush);
        Post("", DimBrush);

        try
        {
            await Task.Run(() =>
            {
                var files = BulkHeaderUpdater.IterDatFiles(target);
                Post($"Found {files.Count} file(s) to process.", DimBrush);
                Post("", DimBrush);
                int ok = 0, err = 0, warn = 0;

                foreach (string f in files)
                {
                    if (token.IsCancellationRequested)
                    {
                        Post("[STOPPED by user]", WarnBrush);
                        break;
                    }
                    try
                    {
                        var d = BulkHeaderUpdater.UpdateFile(f, newDate, fieldValues, addFp);
                        warn += d.Warnings.Count;

                        Post($"[OK] {d.PathAfter}", OkBrush);
                        Post($"     filename : {d.FnDateBefore ?? "None"} → {d.FnDateAfter ?? "None"}", OkBrush);
                        Post($"     header   : {d.HdrDateBefore ?? "None"} → {d.HdrDateAfter ?? "None"}", OkBrush);
                        if (d.FieldsAdded.Count > 0)
                            Post($"     added    : {string.Join(", ", d.FieldsAdded)}", OkBrush);
                        if (d.FieldsUpdated.Count > 0)
                            Post($"     updated  : {string.Join(", ", d.FieldsUpdated)}", OkBrush);
                        if (d.FieldsCleared.Count > 0)
                            Post($"     cleared  : {string.Join(", ", d.FieldsCleared)}", OkBrush);
                        if (d.Renamed)
                            Post("     renamed  : yes", OkBrush);
                        foreach (string w in d.Warnings)
                            Post($"     ⚠ {w}", WarnBrush);
                        Post("", DimBrush);
                        ok++;
                    }
                    catch (Exception e)
                    {
                        err++;
                        Post($"[ERROR] {f}", ErrBrush);
                        Post($"        {e.GetType().Name}: {e.Message}", ErrBrush);
                        Post("", DimBrush);
                    }
                }

                Post($"=== COMPLETE — Success: {ok}  Warnings: {warn}  Errors: {err} ===",
                     err == 0 ? OkBrush : WarnBrush);
            }, CancellationToken.None);
        }
        finally
        {
            IsRunning = false;
            _cancel = null;
        }
    }
}
