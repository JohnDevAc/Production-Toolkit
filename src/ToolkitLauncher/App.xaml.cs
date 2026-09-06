using System.Windows;

namespace ToolkitLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Production Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
