using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ToolkitLauncher.Core;

namespace ToolkitLauncher.Acceptance;

internal static class WpfStartupCheck
{
    // A separate process exercises real Application startup/BAML without permitting actions.
    // Never passes the VM guard or runs ProductionHost.Run, refresh, installers or OS discovery.
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var root = Path.Combine(output, "state-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProductionHost.CheckSeams();
            var app = ProductionHost.CreateApplication();
            var window = new MainWindow(root, false, false)
            { IsEnabled = false, IsHitTestVisible = false, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            var networkAttempts = 0;
            void NetworkAttempt(string _) => Interlocked.Increment(ref networkAttempts);
            ProductionHost.ConfigureTransports(window, new LocalState(root), new HttpClient(new RefuseNetwork(NetworkAttempt)), NetworkAttempt);
            app.MainWindow = window;
            var errors = new StringWriter();
            using var trace = new TextWriterTraceListener(errors);
            PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
            Exception? failure = null;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, _) => { failure = new TimeoutException("WPF startup did not complete."); app.Shutdown(2); };
            app.DispatcherUnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; app.Shutdown(2); };
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!app.StartupReached || window.PreviewMode || app.Windows.Count != 1)
                        throw new InvalidOperationException("Wrong application startup/window mode.");
                    if (app.Resources["Primary"] is not Style || app.Resources[typeof(Button)] is not Style ||
                        app.Resources["VisibleIf"] is not BooleanToVisibilityConverter || app.Resources["Ink"] is not SolidColorBrush)
                        throw new InvalidDataException("Production application resources did not load.");
                    window.UpdateLayout();
                    if (window.ActualWidth <= 0 || window.Cards.Any(c => c.Installed is not null || c.Icon.PixelWidth == 0))
                        throw new InvalidOperationException("Production window/icons did not render safely.");
                    PresentationTraceSources.DataBindingSource.Flush();
                    if (errors.ToString().Contains("Error:")) throw new InvalidDataException("WPF binding errors: " + errors);
                    if (networkAttempts != 0 || Directory.Exists(Path.Combine(root, "Operations")) || File.Exists(Path.Combine(root, "activity.log")))
                        throw new InvalidOperationException("Resource check unexpectedly entered an operation or refresh.");
                    File.WriteAllText(Path.Combine(output, "wpf-startup.txt"), "PASS: actual QA Application startup, production MainWindow BAML, styles/converter and six bundled icons; one non-preview window, disabled interaction, blocked transport with zero requests, no refresh/operations, no binding errors.");
                }
                catch (Exception error) { failure = error; }
                finally { timeout.Stop(); window.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            timeout.Start();
            try { app.Run(window); }
            finally { timeout.Stop(); PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); }
            if (failure is not null) throw failure;
            Console.WriteLine(File.ReadAllText(Path.Combine(output, "wpf-startup.txt")));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "wpf-startup-error.txt"), error.ToString());
            LaunchFailure.Save(output, "isolated WPF startup check", error);
            throw;
        }
    }
}
