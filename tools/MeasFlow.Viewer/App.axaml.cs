using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MeasFlow.Viewer.ViewModels;
using MeasFlow.Viewer.Views;

namespace MeasFlow.Viewer;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Open file from command line args
            if (desktop.Args is { Length: > 0 } && File.Exists(desktop.Args[0]))
            {
                vm.OpenFile(desktop.Args[0]);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
