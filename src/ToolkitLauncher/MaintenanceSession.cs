using System.Security.Cryptography;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace ToolkitLauncher;

/// <summary>Coordinates only this user's instance in this installation directory.</summary>
public sealed class MaintenanceSession : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle close;
    private readonly EventWaitHandle activate;
    private readonly RegisteredWaitHandle closeWait;
    private readonly RegisteredWaitHandle activateWait;
    private static string Key => "Local\\ProductionToolkit." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        WindowsIdentity.GetCurrent().User!.Value + "|" + Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\').ToUpperInvariant())));

    private MaintenanceSession(Mutex ownedMutex, Application app)
    {
        mutex = ownedMutex;
        close = new(false, EventResetMode.AutoReset, Key + ".Close");
        activate = new(false, EventResetMode.AutoReset, Key + ".Activate");
        closeWait = ThreadPool.RegisterWaitForSingleObject(close, (_, _) => app.Dispatcher.BeginInvoke(() =>
        {
            if (app.MainWindow is MainWindow window && window.CanCloseForMaintenance) window.Close();
        }), null, Timeout.Infinite, false);
        activateWait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => app.Dispatcher.BeginInvoke(() =>
        {
            if (app.MainWindow is not Window window) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }), null, Timeout.Infinite, false);
    }

    public static MaintenanceSession? Start(Application app)
    {
        var mutex = new Mutex(true, Key, out var created);
        if (created) return new(mutex, app);
        mutex.Dispose();
        try { using var activate = EventWaitHandle.OpenExisting(Key + ".Activate"); activate.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
        return null;
    }

    public static int RequestShutdown()
    {
        var instances = new List<Process>();
        foreach (var candidate in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
        {
            try
            {
                if (candidate.Id != Environment.ProcessId && string.Equals(candidate.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                { instances.Add(candidate); continue; }
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            candidate.Dispose();
        }
        try
        {
            using var mutex = Mutex.OpenExisting(Key);
            using var close = EventWaitHandle.OpenExisting(Key + ".Close");
            close.Set();
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return 1;
            mutex.ReleaseMutex();
            // WPF releases the mutex in OnExit, before Windows releases the executable
            // mapping. Wait for the actual processes too so Restart Manager sees no stale app.
            return instances.All(p => p.WaitForExit(30000)) ? 0 : 1;
        }
        catch (WaitHandleCannotBeOpenedException) { return instances.All(p => p.WaitForExit(30000)) ? 2 : 1; }
        finally { foreach (var instance in instances) instance.Dispose(); }
    }

    public void Dispose()
    {
        closeWait.Unregister(null); activateWait.Unregister(null);
        close.Dispose(); activate.Dispose();
        mutex.ReleaseMutex(); mutex.Dispose();
    }
}
