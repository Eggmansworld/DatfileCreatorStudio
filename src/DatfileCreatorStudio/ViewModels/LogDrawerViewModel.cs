using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>Colour category for one activity-log line (the suite's log tag colours).</summary>
public enum LogKind
{
    Info,      // plain text
    Phase,     // amber — phase and scan events
    Folder,    // blue — folder boundaries (">>")
    Subfolder, // tan — subfolder markers within a job
    Success,   // green — successfully hashed items
    Carried,   // grey — carried items (incremental, Session 3)
    Error,     // red — errors
    DatDone,   // bright green — completed dat files
}

public sealed record LogLine(string Text, IBrush Brush);

/// <summary>
/// State for the sliding log drawer: buffers engine output from worker
/// threads and flushes it on a timer, tracks run status, progress and
/// elapsed time.
/// </summary>
public partial class LogDrawerViewModel : ViewModelBase
{
    private static readonly IBrush IdleBrush = new ImmutableSolidColorBrush(Color.Parse("#8A8F98"));
    private static readonly IBrush RunningBrush = new ImmutableSolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IBrush SuccessBrush = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#E5484D"));

    private static readonly Dictionary<LogKind, IBrush> KindBrushes = new()
    {
        [LogKind.Info] = new ImmutableSolidColorBrush(Color.Parse("#8A8F98")),
        [LogKind.Phase] = new ImmutableSolidColorBrush(Color.Parse("#E8A33D")),
        [LogKind.Folder] = new ImmutableSolidColorBrush(Color.Parse("#4A9EDA")),
        [LogKind.Subfolder] = new ImmutableSolidColorBrush(Color.Parse("#B08050")),
        [LogKind.Success] = new ImmutableSolidColorBrush(Color.Parse("#3FB950")),
        [LogKind.Carried] = new ImmutableSolidColorBrush(Color.Parse("#8A8F98")),
        [LogKind.Error] = new ImmutableSolidColorBrush(Color.Parse("#E5484D")),
        [LogKind.DatDone] = new ImmutableSolidColorBrush(Color.Parse("#2EC96A")),
    };

    private readonly List<LogLine> _pending = [];
    private readonly Lock _pendingLock = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer;

    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>Raised on the UI thread after new lines are flushed, so the view can auto-scroll.</summary>
    public event Action? LinesFlushed;

    public LogDrawerViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) =>
        {
            FlushPending();
            if (_stopwatch.IsRunning)
                Elapsed = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        };
        _timer.Start();
    }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "Idle";

    [ObservableProperty]
    private string _lastLine = "";

    [ObservableProperty]
    private string _elapsed = "";

    [ObservableProperty]
    private IBrush _statusBrush = IdleBrush;

    // Progress bar state (Phase 1 = indeterminate spinner, Phase 2 = determinate)
    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMax = 1;

    [ObservableProperty]
    private string _itemsText = "";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Clear()
    {
        lock (_pendingLock)
            _pending.Clear();
        Lines.Clear();
        LastLine = "";
    }

    /// <summary>Thread-safe: queue a log line from any thread.</summary>
    public void Append(LogKind kind, string text)
    {
        var line = new LogLine(text, KindBrushes[kind]);
        lock (_pendingLock)
            _pending.Add(line);
    }

    /// <summary>Called on the UI thread when a run begins.</summary>
    public void OnRunStarted(string summary)
    {
        Append(LogKind.Info, "");
        Append(LogKind.Phase, "> " + summary);
        IsRunning = true;
        IsExpanded = true;
        StatusText = "Running";
        StatusBrush = RunningBrush;
        IsIndeterminate = true;
        ProgressValue = 0;
        ProgressMax = 1;
        ItemsText = "";
        _stopwatch.Restart();
        Elapsed = "00:00:00";
        FlushPending();
    }

    /// <summary>Called on the UI thread when totals are known (Phase 2 begins).</summary>
    public void OnTotals(int jobs, int items)
    {
        IsIndeterminate = false;
        ProgressMax = Math.Max(1, items);
        ProgressValue = 0;
        ItemsText = $"0 / {items}";
    }

    /// <summary>Called on the UI thread per completed item.</summary>
    public void OnProgress(int done)
    {
        ProgressValue = done;
        ItemsText = $"{done} / {(int)ProgressMax}";
    }

    /// <summary>Called on the UI thread when a run finishes.</summary>
    public void OnRunCompleted(bool ok, int errorCount, bool stopped)
    {
        _stopwatch.Stop();
        Elapsed = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        IsRunning = false;
        IsIndeterminate = false;

        (StatusText, StatusBrush) = (stopped, ok, errorCount) switch
        {
            (true, _, _) => ("Stopped", IdleBrush),
            (false, true, 0) => ("Completed", SuccessBrush),
            (false, true, _) => ($"Completed with {errorCount} error(s)", RunningBrush),
            _ => ("Failed", ErrorBrush),
        };

        Append(ok && errorCount == 0 ? LogKind.DatDone : LogKind.Phase,
               $"--- {StatusText} in {Elapsed} ---");
        FlushPending();
    }

    /// <summary>Report a GUI-side informational message into the log.</summary>
    public void ReportInfo(string message)
    {
        Append(LogKind.Info, message);
        FlushPending();
    }

    /// <summary>Report a GUI-side problem into the log.</summary>
    public void ReportError(string message)
    {
        Append(LogKind.Error, "ERROR: " + message);
        StatusText = "Error";
        StatusBrush = ErrorBrush;
        IsExpanded = true;
        FlushPending();
    }

    /// <summary>The full log as plain text (for clipboard / save).</summary>
    public string GetLogText()
    {
        var sb = new StringBuilder();
        foreach (var line in Lines)
            sb.AppendLine(line.Text);
        return sb.ToString();
    }

    private void FlushPending()
    {
        List<LogLine>? toAdd = null;
        lock (_pendingLock)
        {
            if (_pending.Count > 0)
            {
                toAdd = [.. _pending];
                _pending.Clear();
            }
        }
        if (toAdd is null)
            return;

        foreach (var line in toAdd)
            Lines.Add(line);
        if (toAdd.Count > 0)
            LastLine = toAdd[^1].Text;
        LinesFlushed?.Invoke();
    }
}
