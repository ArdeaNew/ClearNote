using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        Thread.CurrentThread.CurrentUICulture = english;
        // Applies only to this application's UI thread, including native file pickers.
        SetThreadUILanguage(0x0409);
        base.OnStartup(e);
        var dataIndex = Array.IndexOf(e.Args, "--data");
        var hasDataOverride = dataIndex >= 0 && dataIndex + 1 < e.Args.Length;
        var path = hasDataOverride
            ? Path.GetFullPath(e.Args[dataIndex + 1]) : Path.Combine(AppContext.BaseDirectory, "Log", "Data");
        _instance = new Mutex(true, InstanceName(path), out var fresh);
        if (!fresh)
        {
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
            MainWindow.Show();
            if (!string.IsNullOrEmpty(store.RecoveryNotice)) DialogBox.Show(MainWindow, store.RecoveryNotice, "Library recovered", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
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
    protected override void OnExit(ExitEventArgs e)
    {
        _legacyInstance?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
    private static string InstanceName(string path) => "Local\\Folio-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..16];
}


