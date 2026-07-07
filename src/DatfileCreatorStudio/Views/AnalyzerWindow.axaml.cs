using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class AnalyzerWindow : Window
{
    public AnalyzerWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(PathBox, true);
    }

    private AnalyzerViewModel? ViewModel => DataContext as AnalyzerViewModel;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.Data.Contains(DataFormats.Files))
            return;
        if (e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is not string path)
            return;
        if (File.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;
        ViewModel.FolderPath = path;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder to analyze",
            AllowMultiple = false,
        });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            ViewModel.FolderPath = path;
    }

    private async void OnAnalyze(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.RunAnalyzeAsync();
    }

    private void OnStop(object? sender, RoutedEventArgs e) => ViewModel?.CancelScan();

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.ApplyToMainWindow() is not null)
            Close();
    }

    private void OnOpenRepair(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Result?.PathStats is not { } ps)
            return;
        var window = new LongPathRepairWindow
        {
            DataContext = new LongPathRepairViewModel(ps),
        };
        window.Show(Owner as Window ?? this);
    }

    private async void OnSaveLog(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || vm.Result is null)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Analysis Log",
            SuggestedFileName = "analysis_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Text files") { Patterns = ["*.txt"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(
                string.Join("\n", vm.BuildAnalysisLog())));
        }
        catch
        {
            // best-effort
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
