using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class BulkHeaderUpdaterWindow : Window
{
    public BulkHeaderUpdaterWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(SourceBox, true);
    }

    private BulkHeaderUpdaterViewModel? ViewModel => DataContext as BulkHeaderUpdaterViewModel;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm && e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is string path)
            vm.SourcePath = path;
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a dat/xml file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Dat files") { Patterns = ["*.dat", "*.xml"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            ViewModel.SourcePath = path;
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder containing dat files",
            AllowMultiple = false,
        });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            ViewModel.SourcePath = path;
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.RunAsync();
            if (vm.Lines.Count > 0)
                LogList.ScrollIntoView(vm.Lines[^1]);
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e) => ViewModel?.Stop();

    private void OnClearLog(object? sender, RoutedEventArgs e) => ViewModel?.ClearLog();

    private async void OnSaveLog(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { LogLinesSnapshot.Count: > 0 } vm)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Log",
            SuggestedFileName = "header_updater_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Text files") { Patterns = ["*.txt"] }],
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(
                string.Join("\n", vm.LogLinesSnapshot) + "\n"));
        }
        catch
        {
            // best-effort
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
