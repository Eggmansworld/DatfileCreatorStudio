using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class PreflightDialog : Window
{
    public PreflightDialog()
    {
        InitializeComponent();
    }

    private PreflightDialogViewModel? ViewModel => DataContext as PreflightDialogViewModel;

    private void OnProceedClick(object? sender, RoutedEventArgs e) =>
        Close(new PreflightOutcome(PreflightDecision.Proceed, ViewModel?.NewVersion.Trim() ?? ""));

    private void OnFullRehashClick(object? sender, RoutedEventArgs e) =>
        Close(new PreflightOutcome(PreflightDecision.FullRehash, ViewModel?.NewVersion.Trim() ?? ""));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnStopScanClick(object? sender, RoutedEventArgs e) => ViewModel?.StopScan();

    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.RunValidationAsync();
    }

    private async void OnSaveLogClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { InspectionLog.Count: > 0 } vm)
            return;

        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Pre-inspection Log",
            SuggestedFileName = "dat_creator_pre-inspection_log_" + ts + ".txt",
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
                string.Join("\n", vm.InspectionLog) + "\n"));
        }
        catch
        {
            // Save is best-effort
        }
    }
}
