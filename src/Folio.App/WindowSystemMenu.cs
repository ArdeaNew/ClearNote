using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Folio.App;

/// <summary>Owns native-frame commands and follows the window's lifetime, without touching note state.</summary>
internal sealed class WindowSystemMenu : IDisposable
{
    private const int InitMenuPopup = 0x0117, UninitMenuPopup = 0x0125,
        ExitMenuLoop = 0x0212, SystemCommand = 0x0112;
    private const uint Size = 0xF000, Move = 0xF010, Minimize = 0xF020,
        Maximize = 0xF030, Close = 0xF060, Restore = 0xF120;
    private const uint ByPosition = 0x0400, Separator = 0x0800;
    private readonly Window _window;
    private HwndSource? _source;
    private bool _disposed, _popupOpen;

    internal WindowSystemMenu(Window window)
    {
        _window = window;
        window.SourceInitialized += OnSourceInitialized;
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Initialize();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => Initialize();
    private void OnStateChanged(object? sender, EventArgs e) => Refresh();
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private void Initialize()
    {
        if (_disposed || _source is not null) return;
        var handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(handle);
        if (_source is null) return;
        // The reading width is controlled by the application's width actions.
        SetWindowLong(handle, -16, GetWindowLong(handle, -16) & ~0x00010000);
        _source.AddHook(OnWindowMessage);
        Refresh();
    }

    private void Refresh()
    {
        if (_source is not { IsDisposed: false }) return;
        UpdateMenu(GetSystemMenu(_source.Handle, false), _window.WindowState, _popupOpen);
    }

    internal static void UpdateMenu(IntPtr menu, WindowState state, bool popupOpen)
    {
        if (menu == IntPtr.Zero) return;
        // Remove only commands owned by the native frame, preserving Close and any extension items.
        foreach (var command in new[] { Size, Move, Minimize, Maximize, Restore })
            DeleteMenu(menu, command, 0);
        TrimSeparators(menu);
        if (state == WindowState.Normal)
        {
            InsertMenu(menu, Close, 0, Move, "&Move");
            // Keep native SC_SIZE available for standard frame resizing between popups.
            // Hide it only while the menu is visible; width constraints remain in WPF.
            if (!popupOpen) InsertMenu(menu, Close, 0, Size, "&Size");
            InsertMenu(menu, Close, 0, Minimize, "Mi&nimize");
        }
        else
        {
            InsertMenu(menu, Close, 0, Restore, "&Restore");
            if (state != WindowState.Minimized) InsertMenu(menu, Close, 0, Minimize, "Mi&nimize");
        }
        InsertMenu(menu, Close, Separator, 0, null);
        ModifyMenu(menu, Close, 0, Close, "&Close\tAlt+F4");
        SetMenuDefaultItem(menu, state == WindowState.Minimized ? Restore : Close, false);
    }

    private static void TrimSeparators(IntPtr menu)
    {
        // Removing inactive commands must not leave a leading or doubled separator.
        var previousSeparator = true;
        for (var index = 0; index < GetMenuItemCount(menu);)
        {
            var separator = (GetMenuState(menu, (uint)index, ByPosition) & Separator) != 0;
            if (separator && (previousSeparator || index == GetMenuItemCount(menu) - 1))
                DeleteMenu(menu, (uint)index, ByPosition);
            else { previousSeparator = separator; index++; }
        }
    }

    private IntPtr OnWindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == InitMenuPopup && wParam == GetSystemMenu(handle, false))
        {
            _popupOpen = true;
            Refresh();
        }
        else if ((message == UninitMenuPopup && wParam == GetSystemMenu(handle, false)) ||
            (message == ExitMenuLoop && _popupOpen))
        {
            _popupOpen = false;
            Refresh();
        }
        if (message != SystemCommand) return IntPtr.Zero;
        var command = (uint)wParam.ToInt64() & 0xFFF0;
        if (command == Maximize)
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.StateChanged -= OnStateChanged;
        _window.Closed -= OnClosed;
        if (_source is { IsDisposed: false }) _source.RemoveHook(OnWindowMessage);
        _source = null;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] bool revert);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteMenu(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll", EntryPoint = "InsertMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InsertMenu(IntPtr menu, uint position, uint flags, nuint id, string? label);
    [DllImport("user32.dll", EntryPoint = "ModifyMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ModifyMenu(IntPtr menu, uint item, uint flags, nuint id, string label);
    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);
}
