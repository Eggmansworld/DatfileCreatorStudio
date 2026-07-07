using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class RemoveReadOnlyWindow : Window
{
    public RemoveReadOnlyWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(TargetBox, true);
    }

    private RemoveReadOnlyViewModel? ViewModel => DataContext as RemoveReadOnlyViewModel;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm && e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is string path)
            vm.TargetPath = path;
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            ViewModel.TargetPath = path;
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            ViewModel.TargetPath = path;
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
        await ToolLog.SaveAsync(this, ViewModel?.LogSnapshot, "remove_readonly_log");
}
