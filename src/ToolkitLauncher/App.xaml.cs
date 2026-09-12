using System.Windows;

namespace ToolkitLauncher;

public partial class App : Application
{
    public bool PreviewMode { get; }
    public App() : this(false) { }
    public App(bool preview) => PreviewMode = preview;
    private MaintenanceSession? session;
    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF queues startup even when a UI test only pumps DispatcherFrame.
        // Preview applications load real resources without starting a live dashboard.
        if (PreviewMode) { base.OnStartup(e); return; }
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
        // Installer fixtures opt into their own data directory; ordinary launches
        // retain the per-user root and normal startup checks.
        var dataRoot = e.Args.Contains("--isolated-test-state") ? Path.Combine(AppContext.BaseDirectory, "qa-state") : null;
        MainWindow = new MainWindow(dataRoot, false, !e.Args.Contains("--skip-startup-checks"));
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { session?.Dispose(); base.OnExit(e); }
}
