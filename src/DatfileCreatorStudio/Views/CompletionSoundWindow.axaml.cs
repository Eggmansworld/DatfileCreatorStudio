using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.Services;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class CompletionSoundWindow : Window
{
    public CompletionSoundWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var start = await StorageProvider.TryGetFolderFromPathAsync(SoundService.SoundsDir);
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a completion sound",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter =
            [
                new FilePickerFileType("Sound files") { Patterns = ["*.wav", "*.mp3"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (result.Count > 0 && result[0].TryGetLocalPath() is string path)
            vm.SoundFile = SoundService.ToPortablePath(path);
    }

    private void OnTest(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        if (SoundService.Resolve(vm.SoundFile) is string cue)
        {
            SoundService.Play(cue);
            StatusText.Text = "Playing: " + Path.GetFileName(cue);
        }
        else
        {
            StatusText.Text = "File not found: "
                + (vm.SoundFile.Trim().Length > 0 ? vm.SoundFile : "sounds/" + SoundService.DefaultFileName);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
