using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DatfileCreatorStudio.Views;

/// <summary>Minimal modal text-input dialog (Avalonia has no built-in prompt).</summary>
public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public static Task<string?> ShowAsync(Window owner, string title, string prompt, string initial = "")
    {
        var dialog = new InputDialog { Title = title };
        dialog.PromptText.Text = prompt;
        dialog.ValueBox.Text = initial;
        return dialog.ShowDialog<string?>(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(ValueBox.Text);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
