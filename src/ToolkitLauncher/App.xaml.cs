using System.Windows;

namespace ToolkitLauncher;

public partial class App : Application
{
    private MaintenanceSession? session;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--shutdown-for-maintenance"))
        {
            Shutdown(MaintenanceSession.RequestShutdown());
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Production Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        session = MaintenanceSession.Start(this);
        if (session is null) { Shutdown(); return; }
        MainWindow = new MainWindow(null, false, !e.Args.Contains("--skip-startup-checks"));
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { session?.Dispose(); base.OnExit(e); }
}
