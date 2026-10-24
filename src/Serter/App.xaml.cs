using System.Windows;
using System.Windows.Threading;

namespace Serter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never crash mid-set: report and carry on.
        MessageBox.Show(e.Exception.Message, "Serter", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
