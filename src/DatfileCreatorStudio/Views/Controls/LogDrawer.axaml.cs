using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views.Controls;

public partial class LogDrawer : UserControl
{
    private const double CollapsedHeight = 46;
    private const double ExpandedHeight = 320;

    private LogDrawerViewModel? _viewModel;

    // Auto-scroll (tail-follow) state. We drive the ListBox's own ScrollViewer
    // directly instead of ScrollIntoView, which — called synchronously before
    // freshly added virtualized rows are measured — scrolls to a stale offset
    // and drifts behind the output.
    private ScrollViewer? _scroll;
    private bool _autoScroll = true;

    public LogDrawer()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.LinesFlushed -= OnLinesFlushed;
        }

        _viewModel = DataContext as LogDrawerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.LinesFlushed += OnLinesFlushed;
            UpdateHeight();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogDrawerViewModel.IsExpanded))
            UpdateHeight();
    }

    private void UpdateHeight() =>
        DrawerRoot.Height = _viewModel?.IsExpanded == true ? ExpandedHeight : CollapsedHeight;

    private void OnLinesFlushed()
    {
        if (_viewModel is not { IsExpanded: true })
            return;
        HookScroll();
        // Best-effort pin now; the authoritative one happens in
        // OnLogScrollChanged once the new rows have been laid out.
        if (_autoScroll)
            PinToBottom();
    }

    private void HookScroll()
    {
        if (_scroll is not null)
            return;
        _scroll = LogList.FindDescendantOfType<ScrollViewer>();
        if (_scroll is not null)
            _scroll.ScrollChanged += OnLogScrollChanged;
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null)
            return;

        // The log grew (new lines added): re-pin to the bottom while following.
        // ScrollChanged fires after layout, so Extent is current here.
        if (Math.Abs(e.ExtentDelta.Y) > 0.5)
        {
            if (_autoScroll)
                PinToBottom();
            return;
        }

        // A pure offset change is the user scrolling: keep following only while
        // they are parked within a line's height of the bottom.
        if (Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            double distanceFromBottom =
                _scroll.Extent.Height - (_scroll.Offset.Y + _scroll.Viewport.Height);
            _autoScroll = distanceFromBottom <= 24.0;
        }
    }

    private void PinToBottom()
    {
        if (_scroll is null)
            return;
        double max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        _scroll.Offset = new Vector(_scroll.Offset.X, max);
    }

    private async void OnCopyLogClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (_viewModel is not null && clipboard is not null && _viewModel.Lines.Count > 0)
            await clipboard.SetTextAsync(_viewModel.GetLogText());
    }

    private async void OnCopySelectionClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        var sb = new StringBuilder();
        foreach (var item in LogList.SelectedItems ?? (System.Collections.IList)Array.Empty<object>())
        {
            if (item is LogLine line)
                sb.AppendLine(line.Text);
        }
        if (sb.Length > 0)
            await clipboard.SetTextAsync(sb.ToString());
    }

    #region Open in File Explorer

    /// <summary>Extract a usable path from the selected log line, if any.</summary>
    private string? GetSelectedPath()
    {
        if (LogList.SelectedItem is not LogLine line)
            return null;

        // Log lines carry paths after markers like ">> ", "★ Dat written: ",
        // "[scan] " — find the longest existing path substring.
        string text = line.Text;
        foreach (string marker in new[] { "Dat written: ", ">> ", "[scan] ", "Config: ", "saved to " })
        {
            int idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                string candidate = text[(idx + marker.Length)..].Trim();
                // Trim trailing annotations like "  (12 items)"
                int paren = candidate.LastIndexOf("  (", StringComparison.Ordinal);
                if (paren > 0)
                    candidate = candidate[..paren].Trim();
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private void OnLogMenuOpening(object? sender, CancelEventArgs e)
    {
        OpenInExplorerItem.IsEnabled = GetSelectedPath() is not null;
    }

    private void OnOpenInExplorerClick(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedPath() is not string path)
            return;

        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch
        {
            // Opening a file manager is best-effort only
        }
    }

    #endregion
}
