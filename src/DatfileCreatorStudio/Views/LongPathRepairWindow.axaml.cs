using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class LongPathRepairWindow : Window
{
    public LongPathRepairWindow()
    {
        InitializeComponent();
    }

    private LongPathRepairViewModel? ViewModel => DataContext as LongPathRepairViewModel;

    private List<RepairItem> SelectedItems() =>
        PathList.SelectedItems?.OfType<RepairItem>().ToList() ?? [];

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ViewModel?.SetSelected(PathList.SelectedItems?.OfType<RepairItem>().FirstOrDefault());

    private void OnStemKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.PreviewEdit();
            e.Handled = true;
        }
    }

    // ── Toolbar ──────────────────────────────────────────────────────────

    private async void OnScanFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder to scan for long paths",
            AllowMultiple = false,
        });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path)
            await vm.ScanFolderAsync(path);
    }

    private async void OnImportLog(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Analysis Log",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Text files") { Patterns = ["*.txt"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (result.Count == 0 || result[0].TryGetLocalPath() is not string path)
            return;
        try
        {
            vm.ImportFromLogText(await File.ReadAllLinesAsync(path));
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Import error: " + ex.Message;
        }
    }

    // ── Sort headers ─────────────────────────────────────────────────────

    private void OnSortStatus(object? s, RoutedEventArgs e) => ViewModel?.SortBy("status");
    private void OnSortLength(object? s, RoutedEventArgs e) => ViewModel?.SortBy("length");
    private void OnSortDirectory(object? s, RoutedEventArgs e) => ViewModel?.SortBy("directory");
    private void OnSortFilename(object? s, RoutedEventArgs e) => ViewModel?.SortBy("filename");
    private void OnSortExt(object? s, RoutedEventArgs e) => ViewModel?.SortBy("ext");

    // ── Edit panel actions ───────────────────────────────────────────────

    private void OnAutoSuggest(object? sender, RoutedEventArgs e) =>
        ViewModel?.AutoSuggest(SelectedItems());

    private void OnPreviewEdit(object? sender, RoutedEventArgs e) => ViewModel?.PreviewEdit();

    private void OnClearEdit(object? sender, RoutedEventArgs e) =>
        ViewModel?.ClearEdit(SelectedItems());

    private async void OnRenameParent(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { Selected: { } item } vm)
            return;
        string folderName = Path.GetFileName(item.DirPart);
        string? newName = await InputDialog.ShowAsync(this, "Rename Parent Folder",
            "Renaming folder:\n  " + folderName + "\n\nNew name:", folderName);
        if (newName is null)
            return;
        string message = vm.RenameParentFolder(newName);
        if (message.Length > 0)
            vm.StatusMessage = message;
    }

    private void OnApplyThis(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { Selected: { } item } vm)
            vm.ApplyItems([item]);
    }

    // ── Bottom bar ───────────────────────────────────────────────────────

    private void OnApplySelected(object? sender, RoutedEventArgs e) =>
        ViewModel?.ApplyItems(SelectedItems());

    private void OnApplyAll(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.ApplyItems(vm.AllPending());
    }

    private void OnUndoLast(object? sender, RoutedEventArgs e) => ViewModel?.UndoLast();

    private async void OnSaveRenameLog(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        if (!vm.HasRenames)
        {
            vm.StatusMessage = "No renames have been applied in this session.";
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Rename Log",
            SuggestedFileName = "rename_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
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
                string.Join("\n", vm.BuildRenameLog())));
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Save error: " + ex.Message;
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { Selected: { } item } vm)
            return;
        try
        {
            string cur = Path.GetFullPath(item.CurrentPath);
            string dir = Path.GetFullPath(item.DirPart);
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(cur))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{cur}\"") { UseShellExecute = false });
                else if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
                else
                    vm.StatusMessage = "Neither the file nor its parent folder could be found: " + dir;
            }
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Open error: " + ex.Message;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
