using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class RecursiveArchiveExtractorWindow : Window
{
    public RecursiveArchiveExtractorWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(SourceBox, true);
        DragDrop.SetAllowDrop(CustomDestBox, true);
        DragDrop.SetAllowDrop(MoveDestBox, true);
    }

    private RecursiveArchiveExtractorViewModel? ViewModel => DataContext as RecursiveArchiveExtractorViewModel;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm || e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is not string path)
            return;
        if (File.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;
        if (MainWindow.IsInside(e.Source as Control, CustomDestBox))
            vm.CustomDest = path;
        else if (MainWindow.IsInside(e.Source as Control, MoveDestBox))
            vm.MoveDest = path;
        else
            vm.SourcePath = path;
    }

    private async void OnBrowseSource(object? s, RoutedEventArgs e) => await PickFolder(p => ViewModel!.SourcePath = p);
    private async void OnBrowseCustomDest(object? s, RoutedEventArgs e) => await PickFolder(p => ViewModel!.CustomDest = p);
    private async void OnBrowseMoveDest(object? s, RoutedEventArgs e) => await PickFolder(p => ViewModel!.MoveDest = p);

    private async void OnBrowse7z(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select 7z.exe",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("7z.exe") { Patterns = ["7z.exe", "*.exe"] }],
        });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            ViewModel.SevenZipPath = path;
    }

    private async Task PickFolder(Action<string> set)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            set(path);
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
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnSaveLog(object? sender, RoutedEventArgs e) =>
        await ToolLog.SaveAsync(this, ViewModel?.LogSnapshot, "archive_extractor_log");
}
