using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DatfileCreatorStudio.Views;

public partial class StructureGuideWindow : Window
{
    public StructureGuideWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
