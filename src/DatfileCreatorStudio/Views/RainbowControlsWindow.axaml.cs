using Avalonia.Controls;
using Avalonia.Interactivity;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class RainbowControlsWindow : Window
{
    public RainbowControlsWindow()
    {
        InitializeComponent();
    }

    private void OnReset(object? sender, RoutedEventArgs e) =>
        (DataContext as MainWindowViewModel)?.ResetCopperDefaults();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
