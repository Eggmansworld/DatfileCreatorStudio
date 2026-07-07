using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>
/// Shared plumbing for the archive-oriented tool windows: a colour-coded
/// activity log fed from a background <see cref="ArchiveLog"/>, status line,
/// progress bar, and run/stop state.
/// </summary>
public abstract partial class ArchiveToolViewModel : ViewModelBase
{
    private static readonly Dictionary<string, IBrush> TagBrushes = new()
    {
        ["ok"] = new SolidColorBrush(Color.Parse("#3FB950")),
        ["fail"] = new SolidColorBrush(Color.Parse("#E5484D")),
        ["err"] = new SolidColorBrush(Color.Parse("#E5484D")),
        ["warn"] = new SolidColorBrush(Color.Parse("#E8A33D")),
        ["info"] = new SolidColorBrush(Color.Parse("#4A9EDA")),
        ["hdr"] = new SolidColorBrush(Color.Parse("#4A9EDA")),
        ["mute"] = new SolidColorBrush(Color.Parse("#8A8F98")),
        ["skip"] = new SolidColorBrush(Color.Parse("#8A8F98")),
        ["dim"] = new SolidColorBrush(Color.Parse("#8A8F98")),
        ["nested"] = new SolidColorBrush(Color.Parse("#B07AD0")),
    };

    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#8A8F98"));

    private CancellationTokenSource? _cancel;
    private readonly List<string> _logLines = [];

    public ObservableCollection<LogLine> Lines { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private double _progress;

    public IReadOnlyList<string> LogSnapshot => _logLines;

    public void Stop() => _cancel?.Cancel();

    public void ClearLog()
    {
        Lines.Clear();
        _logLines.Clear();
    }

    /// <summary>Build an ArchiveLog whose callbacks marshal onto the UI thread.</summary>
    protected ArchiveLog MakeLog() => new()
    {
        Line = Post,
        Stat = s => Dispatcher.UIThread.Post(() => StatusText = s),
        Progress = p => Dispatcher.UIThread.Post(() => Progress = p),
    };

    /// <summary>Append a (possibly multi-line) tagged message to the log.</summary>
    protected void Post(string tag, string message)
    {
        var brush = TagBrushes.GetValueOrDefault(tag, DefaultBrush);
        string[] pieces = message.Split('\n');
        int count = pieces.Length;
        if (count > 0 && pieces[^1].Length == 0)
            count--; // drop the segment after a trailing newline
        for (int i = 0; i < count; i++)
        {
            string text = pieces[i];
            Dispatcher.UIThread.Post(() => Lines.Add(new LogLine(text, brush)));
            _logLines.Add(text);
        }
    }

    /// <summary>Run a background work delegate with run/stop state management.</summary>
    protected async Task RunAsync(Action<CancellationToken> work)
    {
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        IsRunning = true;
        Progress = 0;
        try
        {
            await Task.Run(() => work(token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Post("err", "Tool crashed: " + ex.Message + "\n");
        }
        finally
        {
            IsRunning = false;
            _cancel = null;
        }
    }
}
