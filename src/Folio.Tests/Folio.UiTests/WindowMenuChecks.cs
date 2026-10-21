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
    private const uint Close = 0xF060, Move = 0xF010, Minimize = 0xF020, Restore = 0xF120, ResizeHeight = 0x0100;
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
            for (var i = 0; i < 4; i++) Update(menu, WindowState.Normal);
            Assert(Commands(menu).SequenceEqual<uint>([Move, ResizeHeight, Minimize, 0, Close]),
                "A normal fixed-width window must expose only Move, Resize height, Minimize and Close, without duplicate separators.");
            Assert(Label(menu, ResizeHeight).Replace("&", "") == "Resize height" && GetMenuDefaultItem(menu, false, 0) == Close,
                "The size command must describe its real vertical action and preserve Close as the default.");
            Assert(Commands(menu).Where(id => id != 0).All(id => (GetMenuState(menu, id, 0) & 3) == 0),
                "Every offered normal-state command must be enabled.");
            Update(menu, WindowState.Minimized);
            Assert(Commands(menu).SequenceEqual<uint>([Restore, 0, Close]) && GetMenuDefaultItem(menu, false, 0) == Restore,
                "A minimized window must offer Restore and Close, retain Restore as the default, and omit unusable commands.");
            Update(menu, WindowState.Maximized);
            Assert(Commands(menu).SequenceEqual<uint>([Restore, Minimize, 0, Close]),
                "An externally maximized window must retain a working Restore path.");
            Update(menu, WindowState.Normal);
            AppendMenu(menu, 0, 42, "Extension");
            Update(menu, WindowState.Normal);
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
            Assert(Commands(actual).SequenceEqual<uint>([Move, ResizeHeight, Minimize, 0, Close]) &&
                (GetWindowLong(handle, -16) & 0x10000) == 0 && window.MinWidth == window.MaxWidth,
                "SourceInitialized must configure the real unshown native frame and keep width constrained.");
            SendMessage(handle, 0x0117, actual, IntPtr.Zero);
            Assert(Commands(actual).SequenceEqual<uint>([Move, ResizeHeight, Minimize, 0, Close]),
                "The menu-opening event must be idempotent.");
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

    private static void Update(IntPtr menu, WindowState state) => MenuType.GetMethod("UpdateMenu", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [menu, state]);
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
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
