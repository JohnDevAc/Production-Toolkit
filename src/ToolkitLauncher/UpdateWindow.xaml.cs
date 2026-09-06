using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public partial class UpdateWindow : Window, INotifyPropertyChanged
{
    private readonly SelfUpdateService updater;
    private readonly ToolkitUpdate update;
    private readonly CancellationTokenSource cancellation = new();
    public string VersionText => $"Version {SelfUpdateService.CurrentVersion} → {update.Release.Tag.TrimStart('v', 'V')}";
    public bool Busy { get; private set; }
    public bool CanUpdate => !Busy;
    public string CancelText => Busy ? "Cancel download" : "Later";
    public string Activity { get; private set; } = "";
    public double Progress { get; private set; }
    public string? InstallerPath { get; private set; }

    public UpdateWindow(SelfUpdateService updater, ToolkitUpdate update)
    {
        this.updater = updater; this.update = update;
        InitializeComponent(); DataContext = this;
        Closing += (_, e) =>
        {
            if (Busy) { cancellation.Cancel(); e.Cancel = true; }
        };
        Closed += (_, _) => cancellation.Dispose();
    }

    private async void UpdateClick(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        Busy = true; Activity = "Downloading installer…"; Notify("");
        var progress = new Progress<TransferProgress>(p => { if (!Busy) return; Progress = p.Percent; Activity = p.Message; Notify(""); });
        try
        {
            InstallerPath = await updater.DownloadAsync(update, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Busy = false; DialogResult = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Busy = false; DialogResult = false; }
        catch (Exception error) { Busy = false; Activity = "Could not prepare the update. " + error.Message; Notify(""); }
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (Busy) cancellation.Cancel();
        else DialogResult = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
