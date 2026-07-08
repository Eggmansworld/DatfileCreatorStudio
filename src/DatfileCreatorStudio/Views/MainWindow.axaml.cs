using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Folder drag & drop onto the path boxes
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(InputBox, true);
        DragDrop.SetAllowDrop(OutputBox, true);
        DragDrop.SetAllowDrop(IncrDatBox, true);

        // The view supplies the Pre-flight Check dialog to the view model
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
                vm.PreflightHandler = ShowPreflightAsync;
        };

        // Keep the copper-bar animation awake while the user is interacting;
        // it idles itself after five minutes of silence. Tunnel + handledEventsToo
        // so we see every pointer/key event regardless of where it lands.
        AddHandler(InputElement.PointerMovedEvent, (_, _) => Copper.RegisterActivity(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerPressedEvent, (_, _) => Copper.RegisterActivity(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyDownEvent, (_, _) => Copper.RegisterActivity(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private async Task<PreflightOutcome?> ShowPreflightAsync(DatfileCreator.Core.DatSettings settings)
    {
        var dialog = new PreflightDialog
        {
            DataContext = new PreflightDialogViewModel(settings),
        };
        return await dialog.ShowDialog<PreflightOutcome?>(this);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.Data.Contains(DataFormats.Files))
            return;
        var first = e.Data.GetFiles()?.FirstOrDefault();
        if (first?.TryGetLocalPath() is not string path)
            return;

        // The incremental box accepts a dat FILE or a folder of dats as-is
        if (ReferenceEquals(e.Source, IncrDatBox) || IsInside(e.Source as Control, IncrDatBox))
        {
            ViewModel.IncrementalDatPath = path;
            return;
        }

        // A dropped file counts as its containing folder
        if (File.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;

        if (ReferenceEquals(e.Source, OutputBox) || IsInside(e.Source as Control, OutputBox))
            ViewModel.OutputRoot = path;
        else
            ViewModel.InputRoot = path;
    }

    private async void OnBrowseIncrFile(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the existing dat file to update",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Dat files") { Patterns = ["*.xml", "*.dat"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path && ViewModel is not null)
            ViewModel.IncrementalDatPath = path;
    }

    private async void OnBrowseIncrFolder(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select the folder containing your existing dats") is string path
            && ViewModel is not null)
            ViewModel.IncrementalDatPath = path;
    }

    internal static bool IsInside(Control? control, Control target)
    {
        while (control is not null)
        {
            if (ReferenceEquals(control, target))
                return true;
            control = control.Parent as Control;
        }
        return false;
    }

    private async void OnBrowseInput(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select the input top-level folder") is string path && ViewModel is not null)
            ViewModel.InputRoot = path;
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select the output folder (dat root)") is string path && ViewModel is not null)
            ViewModel.OutputRoot = path;
    }

    private void OnOpenAnalyzer(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var window = new AnalyzerWindow
        {
            DataContext = new AnalyzerViewModel(vm),
        };
        window.Show(this);
    }

    private void OnOpenPathRepair(object? sender, RoutedEventArgs e)
    {
        var window = new LongPathRepairWindow
        {
            DataContext = new LongPathRepairViewModel(),
        };
        window.Show(this);
    }

    private void OnOpenHeaderUpdater(object? sender, RoutedEventArgs e) =>
        new BulkHeaderUpdaterWindow { DataContext = new BulkHeaderUpdaterViewModel() }.Show(this);

    private void OnOpenCounter(object? sender, RoutedEventArgs e) =>
        new GameRomCounterWindow { DataContext = new GameRomCounterViewModel() }.Show(this);

    private void OnOpenValidator(object? sender, RoutedEventArgs e) =>
        new ValidateDatfilesWindow { DataContext = new ValidateDatfilesViewModel() }.Show(this);

    private void OnOpenMerge(object? sender, RoutedEventArgs e) =>
        new MergeDatfilesWindow { DataContext = new MergeDatfilesViewModel() }.Show(this);

    private void OnOpenExtractor(object? sender, RoutedEventArgs e) =>
        new RecursiveArchiveExtractorWindow
        {
            DataContext = new RecursiveArchiveExtractorViewModel(ViewModel?.SevenZipPath ?? ""),
        }.Show(this);

    private void OnOpenPacker(object? sender, RoutedEventArgs e) =>
        new ZipStorePackerWindow { DataContext = new ZipStorePackerViewModel() }.Show(this);

    private void OnOpenReadOnly(object? sender, RoutedEventArgs e) =>
        new RemoveReadOnlyWindow { DataContext = new RemoveReadOnlyViewModel() }.Show(this);

    private void OnOpenAbout(object? sender, RoutedEventArgs e) =>
        new AboutWindow().ShowDialog(this);

    private void OnOpenRainbowControls(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            new RainbowControlsWindow { DataContext = vm }.Show(this);
    }

    private void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/Eggmansworld/DatfileCreatorStudio") { UseShellExecute = true });
        }
        catch
        {
            // best-effort
        }
    }

    private void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || vm.PreviewEntries.Count == 0)
            return;
        var window = new PreviewWindow
        {
            DataContext = new PreviewWindowViewModel(vm.PreviewEntries),
        };
        window.Show(this);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }
}
