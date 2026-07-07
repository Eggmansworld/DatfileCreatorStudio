using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class GameRomCounterWindow : Window
{
    public GameRomCounterWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(PathBox, true);
    }

    private GameRomCounterViewModel? ViewModel => DataContext as GameRomCounterViewModel;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is null
            || e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is not string path)
            return;
        if (File.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;
        ViewModel.FolderPath = path;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder containing dat files",
            AllowMultiple = false,
        });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            ViewModel.FolderPath = path;
    }

    private async void OnScan(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.ScanAsync();
    }

    private void OnStop(object? sender, RoutedEventArgs e) => ViewModel?.Stop();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ViewModel?.UpdateSelectionInfo(
            DatList.SelectedItems?.OfType<CounterRow>().ToList() ?? []);

    private void OnSortName(object? s, RoutedEventArgs e) => ViewModel?.SortBy("name");
    private void OnSortGames(object? s, RoutedEventArgs e) => ViewModel?.SortBy("games");
    private void OnSortRoms(object? s, RoutedEventArgs e) => ViewModel?.SortBy("roms");
    private void OnSortSize(object? s, RoutedEventArgs e) => ViewModel?.SortBy("size");

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        DatList.SelectedItems?.Clear();
        foreach (var row in vm.Rows.Where(r => !r.IsFolder))
            DatList.SelectedItems?.Add(row);
    }

    private void OnDeselectAll(object? sender, RoutedEventArgs e) =>
        DatList.SelectedItems?.Clear();

    private async void OnCopySummary(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { HasResults: true } vm && Clipboard is not null)
            await Clipboard.SetTextAsync(string.Join("\n", vm.SummaryLines()));
    }

    private async void OnSaveLog(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { HasResults: true } vm)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Log",
            SuggestedFileName = "dat_game_rom_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Text files") { Patterns = ["*.txt"] }],
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(
                string.Join("\n", vm.BuildLogLines())));
        }
        catch
        {
            // best-effort
        }
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { HasResults: true } vm)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export CSV",
            SuggestedFileName = "dat_game_rom_counts_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV files") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            // UTF-8 with BOM, matching the suite (Excel-friendly)
            await stream.WriteAsync(new UTF8Encoding(true).GetPreamble());
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(vm.BuildCsv()));
        }
        catch
        {
            // best-effort
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
