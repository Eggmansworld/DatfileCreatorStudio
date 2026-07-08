using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DatfileCreatorStudio.Views;

public partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/Eggmansworld/DatfileCreatorStudio";
    private const string CoffeeUrl = "https://buymeacoffee.com/eggmansworld";

    public AboutWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "Version 0.1.0" : $"Version {v.Major}.{v.Minor}.{v.Build}";
    }

    private void OnGitHubClick(object? sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

    private void OnCoffeeClick(object? sender, RoutedEventArgs e) => OpenUrl(CoffeeUrl);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Opening a browser is best-effort only
        }
    }
}
