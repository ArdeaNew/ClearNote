using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Folio.App;

public partial class MainWindow
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FitStartupToWorkArea(WidthWorkArea());
    }

    private void FitStartupToWorkArea(Rect workArea)
    {
        var width = Math.Min(Width, Math.Max(280, workArea.Width - 24));
        var height = Math.Max(1, workArea.Height - 24);
        ApplyFixedWidth(width);
        MinHeight = Math.Min(MinHeight, height);
        Height = height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = workArea.Left + (workArea.Width - width) / 2;
        Top = workArea.Top + (workArea.Height - height) / 2;
    }

    private void ApplyFixedWidth(double width)
    {
        MinWidth = 0;
        MaxWidth = width;
        Width = width;
        MinWidth = width;
    }

    private static double NextWidth(double current, bool wider) => current * (wider ? 1.07 : 0.93);

    private bool CanAdjustWidth(bool wider, Rect workArea)
    {
        var next = NextWidth(Width, wider);
        return next >= WindowWidthPreferences.MinimumWidth &&
            next <= Math.Min(WindowWidthPreferences.MaximumWidth, workArea.Width - 24);
    }

    private bool TryAdjustWidth(bool wider, Rect workArea)
    {
        if (!CanAdjustWidth(wider, workArea)) return false;
        var width = NextWidth(Width, wider);
        try
        {
            _widthPreferences.Save(width);
            var center = double.IsFinite(Left) ? Left + Width / 2 : workArea.Left + workArea.Width / 2;
            ApplyFixedWidth(width);
            Left = Math.Clamp(center - width / 2, workArea.Left + 12, workArea.Right - width - 12);
            ClearError("width");
            return true;
        }
        catch (Exception error)
        {
            SetError("width", "Window width could not be saved. Existing settings are preserved.", error);
            return false;
        }
    }

    private Rect WidthWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
}
