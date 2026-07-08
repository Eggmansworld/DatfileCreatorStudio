using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DatfileCreatorStudio.Services;
using DatfileCreatorStudio.ViewModels;
using DatfileCreatorStudio.Views;

namespace DatfileCreatorStudio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new SettingsService();
            settings.Load();
            MainWindowViewModel.ApplyTheme(settings.Config.Theme);
            AccentThemes.CaptureSystemDefaults();
            AccentThemes.Apply(settings.Config.AccentTheme);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(settings),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
