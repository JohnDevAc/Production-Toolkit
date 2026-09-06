using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ToolkitLauncher;

public static class WindowSizing
{
    // Measure at progressively wider sizes before accepting vertical scrolling.
    // Heights include window borders/title bar, so a fit means the client area fits too.
    public static Size Select(Size available, Size chrome, Func<double, double> measureHeight)
    {
        var firstWidth = Math.Min(1120, available.Width);
        var lastWidth = Math.Min(1440, available.Width);
        var widths = new List<double> { firstWidth };
        for (var width = firstWidth + 80; width < lastWidth; width += 80) widths.Add(width);
        if (lastWidth > firstWidth) widths.Add(lastWidth);
        // Very wide desktops may avoid wrapping that cannot be avoided at 1440 DIP.
        if (available.Width > lastWidth) widths.Add(available.Width);
        var best = new Size(firstWidth, double.MaxValue);
        foreach (var width in widths)
        {
            var height = Math.Ceiling(measureHeight(Math.Max(1, width - chrome.Width)) + chrome.Height + 2);
            if (height <= available.Height) return new(width, height);
            if (height < best.Height) best = new(width, height);
        }
        return new(best.Width, Math.Min(best.Height, available.Height));
    }

    public static Rect WorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var source = HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)),
            transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
