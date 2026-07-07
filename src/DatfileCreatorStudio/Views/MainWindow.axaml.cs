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

        // Folder drag & drop onto the two path boxes
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(InputBox, true);
        DragDrop.SetAllowDrop(OutputBox, true);
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

        // A dropped file counts as its containing folder
        if (File.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;

        if (ReferenceEquals(e.Source, OutputBox) || IsInside(e.Source as Control, OutputBox))
            ViewModel.OutputRoot = path;
        else
            ViewModel.InputRoot = path;
    }

    private static bool IsInside(Control? control, Control target)
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
