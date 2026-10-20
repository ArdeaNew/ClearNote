using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

internal static class ErrorHandlingChecks
{
    public static void Run(string root)
    {
        var data = Path.Combine(root, "errors", "Log", "Data");
        var store = new LibraryStore(data);
        store.Load();
        var window = new MainWindow(store);
        try
        {
            var composer = (NoteEditor)window.FindName("Composer");
            composer.PlainText = "Keep this unsent private thought";
            composer.TextBox.SelectAll();
            var panel = (AttachmentPanel)window.GetType().GetField("_composerMedia", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var picture = Picture();
            var image = ImageAttachments.ImportBitmap(picture, store);
            panel.AddImages([image, image, image, image]);
            var source = new DataObject(DataFormats.Bitmap, picture);
            var paste = new DataObjectPastingEventArgs(source, false, DataFormats.Bitmap) { RoutedEvent = DataObject.PastingEvent };
            composer.TextBox.RaiseEvent(paste);
            var status = (TextBlock)window.FindName("ErrorStatus");
            Assert(paste.CommandCancelled && panel.Count == 4 && composer.PlainText.Contains("private thought"), "An invalid attachment paste must preserve the unsent draft.");
            Assert(status.Text.Contains("Remove one", StringComparison.Ordinal) && status.Visibility == Visibility.Visible, "The failed paste must explain the image limit and recovery action.");
            var log = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(data)!, "Diagnostics"), "*.jsonl").Single();
            var entry = File.ReadAllText(log);
            Assert(!entry.Contains("private thought", StringComparison.Ordinal), "Diagnostics must not serialize note text.");
            using var json = JsonDocument.Parse(entry.Trim());
            Assert(json.RootElement.GetProperty("operation").GetString() == "input" && json.RootElement.GetProperty("detail").GetString()!.Contains("four images", StringComparison.Ordinal), "Detailed causes must be recorded under Log.");
            composer.PlainText = "Continue writing";
            Assert(status.Visibility == Visibility.Collapsed, "A new successful edit must clear the obsolete input error.");
        }
        finally { window.Close(); }

        var blockedData = Path.Combine(root, "blocked-diagnostics", "Log", "Data");
        var blockedStore = new LibraryStore(blockedData);
        blockedStore.Load();
        var obstruction = Path.Combine(Path.GetDirectoryName(blockedData)!, "Diagnostics");
        File.WriteAllText(obstruction, "Preserve this file");
        var blockedWindow = new MainWindow(blockedStore);
        try
        {
            var method = typeof(MainWindow).GetMethod("SetError", BindingFlags.Instance | BindingFlags.NonPublic)!;
            method.Invoke(blockedWindow, ["test", "The operation failed safely.", new UnauthorizedAccessException("Synthetic denial")]);
            Assert(((TextBlock)blockedWindow.FindName("ErrorStatus")).Text == "The operation failed safely." && File.ReadAllText(obstruction) == "Preserve this file", "Logging failure must neither hide the original error nor replace an existing file.");
        }
        finally { blockedWindow.Close(); }
    }

    private static BitmapSource Picture() => BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 100, 120, 140, 255 }, 4);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
