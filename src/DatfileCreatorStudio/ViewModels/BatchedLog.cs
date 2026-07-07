using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>
/// A thread-safe activity log that coalesces high-frequency writes into
/// batched UI updates. Background threads call <see cref="Post"/> freely; the
/// bound <see cref="Lines"/> collection is only ever mutated on the UI thread,
/// and at most one flush is queued at a time no matter how fast lines arrive.
/// This prevents the per-line Dispatcher flooding that can overwhelm the app
/// when a tool emits tens of thousands of lines (e.g. validating a large dat).
/// </summary>
public sealed class BatchedLog
{
    /// <summary>Cap on displayed lines; the full log is always kept for saving.</summary>
    private const int MaxVisible = 20000;

    private static readonly IBrush TrimBrush = new ImmutableSolidColorBrush(Color.Parse("#8A8F98"));

    private readonly List<LogLine> _pending = [];
    private readonly List<string> _all = [];
    private readonly Lock _lock = new();
    private volatile bool _flushScheduled;
    private bool _trimmedNoticeShown;

    /// <summary>Bound to the ListBox; mutated only on the UI thread.</summary>
    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>The complete log text, safe to read after a run for saving.</summary>
    public IReadOnlyList<string> Snapshot
    {
        get
        {
            lock (_lock)
                return _all.ToArray();
        }
    }

    /// <summary>Queue a line from any thread.</summary>
    public void Post(string text, IBrush brush)
    {
        lock (_lock)
        {
            _pending.Add(new LogLine(text, brush));
            _all.Add(text);
        }
        if (!_flushScheduled)
        {
            _flushScheduled = true;
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _pending.Clear();
            _all.Clear();
        }
        Lines.Clear();
        _trimmedNoticeShown = false;
    }

    private void Flush()
    {
        _flushScheduled = false;
        LogLine[] batch;
        lock (_lock)
        {
            if (_pending.Count == 0)
                return;
            batch = [.. _pending];
            _pending.Clear();
        }
        foreach (var line in batch)
        {
            if (Lines.Count < MaxVisible)
            {
                Lines.Add(line);
            }
            else if (!_trimmedNoticeShown)
            {
                _trimmedNoticeShown = true;
                Lines.Add(new LogLine(
                    "… (log truncated for display — use Save Log for the full report) …", TrimBrush));
            }
        }
    }
}
