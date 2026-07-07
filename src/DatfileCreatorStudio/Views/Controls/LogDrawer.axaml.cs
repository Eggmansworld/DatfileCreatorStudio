using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views.Controls;

public partial class LogDrawer : UserControl
{
    private const double CollapsedHeight = 46;
    private const double ExpandedHeight = 320;

    private LogDrawerViewModel? _viewModel;

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
            _viewModel.LinesFlushed -= ScrollToEnd;
        }

        _viewModel = DataContext as LogDrawerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.LinesFlushed += ScrollToEnd;
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

    private void ScrollToEnd()
    {
        if (_viewModel is { IsExpanded: true } vm && vm.Lines.Count > 0)
            LogList.ScrollIntoView(vm.Lines[^1]);
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
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open");
                if (File.Exists(path))
                    psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            else
            {
                string target = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;
                var psi = new ProcessStartInfo("xdg-open");
                psi.ArgumentList.Add(target);
                Process.Start(psi);
            }
        }
        catch
        {
            // Opening a file manager is best-effort only
        }
    }

    #endregion
}
