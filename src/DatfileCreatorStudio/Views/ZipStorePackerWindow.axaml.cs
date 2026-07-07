using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class ZipStorePackerWindow : Window
{
    public ZipStorePackerWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(SourceBox, true);
    }

    private ZipStorePackerViewModel? ViewModel => DataContext as ZipStorePackerViewModel;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm || e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is not string path)
            return;
        if (File.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;
        vm.SourcePath = path;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            ViewModel.SourcePath = path;
    }

    private void OnAddExt(object? sender, RoutedEventArgs e) => ViewModel?.AddExtensions();

    private void OnExtKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.AddExtensions();
            e.Handled = true;
        }
    }

    private void OnRemoveExt(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string ext })
            ViewModel?.RemoveExtension(ext);
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
        await ToolLog.SaveAsync(this, ViewModel?.LogSnapshot, "zip_store_packer_log");
}
