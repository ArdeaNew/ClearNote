using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Folio.App;
using Folio.Core;

/// <summary>Native menus and an unshown test-owned HWND; no desktop window is opened or controlled.</summary>
internal static class WindowMenuChecks
{
    private const uint Close = 0xF060, Size = 0xF000, Move = 0xF010, Minimize = 0xF020, Restore = 0xF120;
    private static readonly uint[] VisibleCommands = [Move, Minimize, 0, Close];
    private static readonly uint[] ClosedCommands = [Move, Size, Minimize, 0, Close];
    private static readonly Type MenuType = typeof(MainWindow).Assembly.GetType("Folio.App.WindowSystemMenu", true)!;

    internal static void Run(string root)
    {
        var menu = CreatePopupMenu();
        try
        {
            foreach (var command in new uint[] { Restore, Move, 0xF000, Minimize, 0xF030 })
                AppendMenu(menu, 0, command, "Original");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, Close, "Close\tAlt+F4");
            for (var i = 0; i < 4; i++) Update(menu, WindowState.Normal, true);
            Assert(Commands(menu).SequenceEqual(VisibleCommands),
                "The visible window menu must offer only Move, Minimize and Close, with no resize entry or duplicate separators.");
            Assert(Label(menu, Close).Contains("Alt+F4") && GetMenuDefaultItem(menu, false, 0) == Close,
                "Close must retain its keyboard label and remain the default command.");
            Assert(Commands(menu).Where(id => id != 0).All(id => (GetMenuState(menu, id, 0) & 3) == 0),
                "Every offered normal-state command must be enabled.");
            Update(menu, WindowState.Normal, false);
            Assert(Commands(menu).SequenceEqual(ClosedCommands) && GetMenuState(menu, Size, 0) == 0,
                "Outside the popup, native Size must remain enabled for border resizing.");
            Update(menu, WindowState.Minimized, true);
            Assert(Commands(menu).SequenceEqual<uint>([Restore, 0, Close]) && GetMenuDefaultItem(menu, false, 0) == Restore,
                "A minimized window must offer Restore and Close, retain Restore as the default, and omit unusable commands.");
            Update(menu, WindowState.Maximized, true);
            Assert(Commands(menu).SequenceEqual<uint>([Restore, Minimize, 0, Close]),
                "An externally maximized window must retain a working Restore path.");
            Update(menu, WindowState.Normal, true);
            AppendMenu(menu, 0, 42, "Extension");
            Update(menu, WindowState.Normal, true);
            Assert(Commands(menu).Count(id => id == 42) == 1 && Commands(menu).Count(id => id == Close) == 1,
                "Refreshing the native menu must preserve unrelated extension commands and Close.");
        }
        finally { DestroyMenu(menu); }

        var store = new LibraryStore(Path.Combine(root, "window-menu", "Log", "Data"));
        store.Load();
        var window = new MainWindow(store) { ShowInTaskbar = false };
        var closing = 0;
        System.ComponentModel.CancelEventHandler cancel = (_, e) => { closing++; e.Cancel = true; };
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Assert(!IsWindowVisible(handle), "Native menu checks must never show a desktop window.");
            var actual = GetSystemMenu(handle, false);
            var limits = InspectFrame(handle, actual);
            Assert(Commands(actual).SequenceEqual(ClosedCommands) &&
                (GetWindowLong(handle, -16) & 0x10000) == 0 && window.MinWidth == window.MaxWidth,
                "SourceInitialized must configure the real unshown native frame and keep width constrained.");
            for (var round = 0; round < 3; round++)
            {
                SendMessage(handle, 0x0117, actual, IntPtr.Zero);
                SendMessage(handle, 0x0117, actual, IntPtr.Zero);
                Assert(Commands(actual).SequenceEqual(VisibleCommands),
                    "Opening the popup repeatedly must hide both Size and Resize height without duplicating commands.");
                var unrelated = CreatePopupMenu();
                try
                {
                    SendMessage(handle, 0x0125, unrelated, IntPtr.Zero);
                    Assert(Commands(actual).SequenceEqual(VisibleCommands),
                        "Closing an unrelated popup must not expose Size in the visible window menu.");
                }
                finally { DestroyMenu(unrelated); }
                SendMessage(handle, 0x0125, actual, IntPtr.Zero);
                Assert(Commands(actual).SequenceEqual(ClosedCommands) && GetMenuState(actual, Size, 0) == 0,
                    "Closing the system popup must restore the native resizing command.");
                SendMessage(handle, 0x0117, actual, IntPtr.Zero);
                SendMessage(handle, 0x0212, IntPtr.Zero, IntPtr.Zero);
                Assert(Commands(actual).SequenceEqual(ClosedCommands) && GetMenuState(actual, Size, 0) == 0,
                    "Exiting or cancelling the menu loop must also restore border resizing.");
            }
            ResizeFromEdge(window, handle, limits, fromTop: true);
            ResizeFromEdge(window, handle, limits, fromTop: false);
            InspectFrame(handle, actual);
            SendMessage(handle, 0x0112, new IntPtr(0xF030), IntPtr.Zero);
            Assert(window.WindowState == WindowState.Normal && !IsWindowVisible(handle),
                "A native maximize request must not bypass the fixed-width policy.");
            window.Closing += cancel;
            SendMessage(handle, 0x0112, new IntPtr(Close), IntPtr.Zero);
            Assert(closing == 1 && !IsWindowVisible(handle),
                "Native Close must reach WPF Closing and honor cancellation without bypassing save guards.");
        }
        finally
        {
            window.Closing -= cancel;
            window.Close();
        }
        var owner = typeof(MainWindow).GetField("_windowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Assert((bool)MenuType.GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!,
            "Closing the window must detach native hooks and lifecycle events.");
    }

    private static void Update(IntPtr menu, WindowState state, bool popupOpen) => MenuType.GetMethod("UpdateMenu", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [menu, state, popupOpen]);

    private static MinMaxInfo InspectFrame(IntPtr handle, IntPtr menu)
    {
        Assert(GetWindowRect(handle, out var rect), "The test frame must have a native rectangle.");
        var top = HitTest(handle, (rect.Left + rect.Right) / 2, rect.Top + 1);
        var bottom = HitTest(handle, (rect.Left + rect.Right) / 2, rect.Bottom - 1);
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>());
        MinMaxInfo limits;
        try
        {
            // USER32 supplies these defaults before asking WPF to constrain them.
            // Sending an all-zero struct would corrupt WPF's cached native limits.
            Marshal.StructureToPtr(new MinMaxInfo
            {
                MaxSize = new NativePoint { X = GetSystemMetrics(61), Y = GetSystemMetrics(62) },
                MaxPosition = new NativePoint { X = -GetSystemMetrics(32), Y = -GetSystemMetrics(33) },
                MinTrack = new NativePoint { X = GetSystemMetrics(34), Y = GetSystemMetrics(35) },
                MaxTrack = new NativePoint { X = GetSystemMetrics(59), Y = GetSystemMetrics(60) }
            }, memory, false);
            SendMessage(handle, 0x0024, IntPtr.Zero, memory);
            limits = Marshal.PtrToStructure<MinMaxInfo>(memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
        var sizeState = GetMenuState(menu, 0xF000, 0);
        Console.WriteLine($"Native frame: style=0x{GetWindowLong(handle, -16):X8}; SC_SIZE=0x{sizeState:X8}; top={top}; bottom={bottom}; track={limits.MinTrack.X}x{limits.MinTrack.Y}..{limits.MaxTrack.X}x{limits.MaxTrack.Y}");
        Assert(sizeState != uint.MaxValue && (sizeState & 3) == 0,
            "The native Size command must remain enabled outside the visible popup so border resizing can start.");
        Assert((GetWindowLong(handle, -16) & 0x40000) != 0 && top == 12 && bottom == 15,
            "The unshown native frame must retain top and bottom resize hit targets.");
        Assert(Math.Abs(limits.MaxTrack.X - limits.MinTrack.X) <= 1 && limits.MaxTrack.Y > limits.MinTrack.Y,
            "Native sizing limits must lock width while allowing a height range.");
        return limits;
    }
    private static void ResizeFromEdge(MainWindow window, IntPtr handle, MinMaxInfo limits, bool fromTop)
    {
        Assert(GetWindowRect(handle, out var before), "The native test frame must have a rectangle before resizing.");
        var height = before.Bottom - before.Top;
        var minimum = limits.MinTrack.Y + 2;
        var maximum = limits.MaxTrack.Y - 2;
        Assert(maximum > minimum, "The native height range must permit a bounded test resize.");
        var next = Math.Clamp(height - 30, minimum, maximum);
        if (next == height) next = Math.Clamp(height + 30, minimum, maximum);
        Assert(next != height, "The test resize must actually change the height.");
        var top = fromTop ? before.Bottom - next : before.Top;
        Assert(SetWindowPos(handle, IntPtr.Zero, before.Left, top, before.Right - before.Left, next, 0x0014),
            "A native height-only geometry update must succeed on the unshown test frame.");
        window.Dispatcher.Invoke(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Loaded);
        Assert(GetWindowRect(handle, out var after) && after.Right - after.Left == before.Right - before.Left &&
            after.Bottom - after.Top == next && after.Left == before.Left &&
            (fromTop ? after.Bottom == before.Bottom : after.Top == before.Top),
            "Top and bottom edge geometry updates must change height while preserving width and the opposite edge.");
        var transform = HwndSource.FromHwnd(handle)!.CompositionTarget.TransformFromDevice;
        Assert(Math.Abs(window.Height - transform.Transform(new Point(0, next)).Y) <= 1 &&
            window.MinWidth == window.MaxWidth && !IsWindowVisible(handle),
            "Native height updates must reach WPF without unlocking width or showing the test window.");
    }
    private static int HitTest(IntPtr handle, int x, int y) => SendMessage(handle, 0x0084, IntPtr.Zero,
        new IntPtr(unchecked((int)((uint)(ushort)x | (uint)(ushort)y << 16)))).ToInt32();

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    private static List<uint> Commands(IntPtr menu) => Enumerable.Range(0, GetMenuItemCount(menu)).Select(index => GetMenuItemID(menu, index)).ToList();
    private static string Label(IntPtr menu, uint id) { var text = new StringBuilder(128); GetMenuString(menu, id, text, text.Capacity, 0); return text.ToString(); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? label);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int index);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMenuStringW", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int capacity, uint flags);
    [DllImport("user32.dll")] private static extern uint GetMenuDefaultItem(IntPtr menu, bool byPosition, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr handle, bool revert);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
