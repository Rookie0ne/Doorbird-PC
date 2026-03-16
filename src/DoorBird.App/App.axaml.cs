using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DoorBird.App.Services;
using DoorBird.App.ViewModels;
using DoorBird.App.Views;

namespace DoorBird.App;

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
            var missing = DependencyChecker.Check();
            if (missing.Count > 0)
            {
                var message = DependencyChecker.FormatMessage(missing);
                desktop.MainWindow = new Window
                {
                    Title = "DoorBird - Missing Dependencies",
                    Width = 550,
                    Height = 350,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = new ScrollViewer
                    {
                        Margin = new Avalonia.Thickness(20),
                        Content = new SelectableTextBlock
                        {
                            Text = message,
                            FontFamily = "monospace",
                            FontSize = 13,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        }
                    }
                };
            }
            else
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
