using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Folio.Core;

namespace Folio.App;

public partial class App : Application
{
    private Mutex? _instance;
    private Mutex? _legacyInstance;
    [DllImport("kernel32.dll")]
    private static extern ushort SetThreadUILanguage(ushort languageId);

    protected override void OnStartup(StartupEventArgs e)
    {
        var verificationIndex = Array.IndexOf(e.Args, "--verify-portable");
        var verification = verificationIndex >= 0;
        var report = verification && verificationIndex + 1 < e.Args.Length ? e.Args[verificationIndex + 1] : null;
        var dataIndex = Array.IndexOf(e.Args, "--data");
        var hasDataOverride = dataIndex >= 0 && dataIndex + 1 < e.Args.Length;
        if (verification && (!hasDataOverride || string.IsNullOrWhiteSpace(report))) { Shutdown(2); return; }
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        Thread.CurrentThread.CurrentUICulture = english;
        // Applies only to this application's UI thread, including native file pickers.
        SetThreadUILanguage(0x0409);
        base.OnStartup(e);
        var path = hasDataOverride
            ? Path.GetFullPath(e.Args[dataIndex + 1]) : Path.Combine(AppContext.BaseDirectory, "Log", "Data");
        // Packaging checks may only initialize a fresh, explicitly supplied example library.
        if (verification && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) { Shutdown(2); return; }
        _instance = new Mutex(true, InstanceName(path), out var fresh);
        if (!fresh)
        {
            if (verification) { Shutdown(2); return; }
            DialogBox.Show("ClearNote is already running. Return to the open window to continue.", "ClearNote");
            Shutdown();
            return;
        }
        LibraryStore? store = null;
        try
        {
            if (!hasDataOverride)
            {
                _legacyInstance = new Mutex(true, InstanceName(Path.Combine(AppContext.BaseDirectory, "Data")), out var legacyFresh);
                if (!legacyFresh)
                {
                    DialogBox.Show("An older Folio window is still using this folder. Close it before opening the new version.", "ClearNote");
                    Shutdown();
                    return;
                }
                path = PortableStorage.Prepare(AppContext.BaseDirectory).LibraryPath;
            }
            store = new LibraryStore(path);
            store.Load();
            WelcomeNotes.EnsureCreated(store);
            MainWindow = new MainWindow(store);
            if (verification)
            {
                MainWindow.Measure(new Size(662, 820));
                MainWindow.Arrange(new Rect(0, 0, 662, 820));
                MainWindow.UpdateLayout();
                WritePortableReport(report!, path);
                Shutdown(0);
                return;
            }
            MainWindow.Show();
            if (!string.IsNullOrEmpty(store.RecoveryNotice)) DialogBox.Show(MainWindow, store.RecoveryNotice, "Library recovered", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (verification)
            {
                try { WritePortableReport(report!, path, ex); } catch { /* The exit code still reports failure. */ }
                Shutdown(1);
                return;
            }
            var choice = DialogBox.Show("ClearNote could not open your library. Your existing files have been preserved.\n\n" + ex.Message + "\n\nLibrary folder: " + path + "\n\nWould you like to restore a ClearNote backup?", "ClearNote - Recover library", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Yes && store != null)
            {
                var dialog = new OpenFileDialog { Title = "Choose a ClearNote backup", Filter = "ClearNote backup|*.zip" };
                if (dialog.ShowDialog() == true)
                {
                    try
                    {
                        store.Restore(dialog.FileName);
                        MainWindow = new MainWindow(store);
                        MainWindow.Show();
                        DialogBox.Show(MainWindow, store.RecoveryNotice ?? "Your library has been restored.", "Restore complete");
                        return;
                    }
                    catch (Exception recoveryError) { DialogBox.Show("The backup could not be restored. Your existing files have been preserved.\n\n" + recoveryError.Message, "ClearNote", MessageBoxButton.OK, MessageBoxImage.Error); }
                }
            }
            Shutdown(1);
        }
    }
    private void WritePortableReport(string report, string libraryPath, Exception? error = null)
    {
        using var process = Process.GetCurrentProcess();
        var coreClrPaths = process.Modules.Cast<ProcessModule>().Where(module =>
            string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase))
            .Select(module => module.FileName).ToArray();
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            Success = error is null,
            Error = error?.ToString(),
            ProcessPath = Environment.ProcessPath,
            BaseDirectory = AppContext.BaseDirectory,
            LibraryPath = libraryPath,
            Runtime = RuntimeInformation.FrameworkDescription,
            RuntimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
            CoreClrPaths = coreClrPaths,
            CoreLibLocation = typeof(object).Assembly.Location,
            WpfAssembly = typeof(Window).Assembly.GetName().Name,
            WpfLocation = typeof(Window).Assembly.Location,
            WindowCreated = MainWindow is not null,
            WindowVisible = MainWindow?.IsVisible ?? false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _legacyInstance?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
    private static string InstanceName(string path) => "Local\\Folio-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..16];
}


