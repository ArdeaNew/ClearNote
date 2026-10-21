using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

internal static class ImageFailureChecks
{
    private static readonly Assembly AppAssembly = typeof(MainWindow).Assembly;
    private static readonly Type Context = AppAssembly.GetType("Folio.App.ImageFailureContext", true)!;
    private static readonly Type Stage = AppAssembly.GetType("Folio.App.ImageFailureStage", true)!;

    public static void Run(string root)
    {
        Classification();
        FileAndStorageFailures(root);
        MissingAssetAndRecovery(root);
    }

    private static void Classification()
    {
        var codec = new NotSupportedException("Outer WPF error", new COMException("Missing component", unchecked((int)0x88982F50)));
        Mark(codec, "Decode");
        Assert(Describe(codec).Contains("no decoder", StringComparison.Ordinal), "Wrapped WIC missing-component errors must identify codec availability.");
        var damaged = new COMException("Bad pixels", unchecked((int)0x88982F60));
        Assert(Describe(damaged).Contains("damaged or incomplete", StringComparison.Ordinal), "Bad pixel data must differ from an unavailable codec.");
        var storage = new IOException("Asset store failure", codec);
        Mark(storage, "Store");
        // A deeper stage is intentionally retained: outer boundaries must not relabel the cause.
        Assert(GetStage(storage) == "Decode", "A parent operation must preserve an existing, more precise failure stage.");
        storage = new IOException("Asset store failure"); Mark(storage, "Store"); Mark(storage, "Decode");
        Assert(GetStage(storage) == "Store" && Describe(storage).Contains("stored in Log", StringComparison.Ordinal),
            "Storage failures must retain their exception type and stage rather than suggest re-exporting a valid picture.");
        Assert(Describe(new OutOfMemoryException()).Contains("memory", StringComparison.Ordinal) &&
            Describe(new IOException("Full disk", unchecked((int)0x80070070))).Contains("disk space", StringComparison.Ordinal),
            "Memory and disk limits must have distinct recovery actions.");
    }

    private static void FileAndStorageFailures(string root)
    {
        var directory = Path.Combine(root, "image-failure-stages");
        Directory.CreateDirectory(directory);
        var bytes = Picture();
        var source = Path.Combine(directory, "source.png"); File.WriteAllBytes(source, bytes);
        var store = new LibraryStore(Path.Combine(directory, "Log", "Data")); store.Load();
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Failure(() => ImageAttachments.ImportPaths([source], store));
            Assert(error is IOException && GetStage(error) == "ReadSource" && Describe(error).Contains("in use", StringComparison.Ordinal),
                "A locked source must remain an IO failure, retain its source stage and allow retry.");
        }
        var empty = Path.Combine(directory, "empty.png"); File.WriteAllBytes(empty, []);
        var emptyError = Failure(() => ImageAttachments.ImportPaths([empty], store));
        Assert(Describe(emptyError).Contains("empty", StringComparison.Ordinal) && !Describe(emptyError).Contains("too large", StringComparison.Ordinal),
            "An empty file must not be reported as oversized.");
        File.WriteAllText(store.AssetDirectory, "Synthetic blocked asset directory");
        var storageError = Failure(() => ImageAttachments.ImportPaths([source], store));
        Assert(storageError is IOException && GetStage(storageError) == "Store" && Describe(storageError).Contains("Log", StringComparison.Ordinal) &&
            File.ReadAllBytes(source).SequenceEqual(bytes), "A Log write failure must not mutate its valid source or become a decoder error.");
        File.Delete(store.AssetDirectory);
        var image = ImageAttachments.ImportPaths([source], store).Single();
        Assert(File.ReadAllBytes(store.GetImagePath(image)).SequenceEqual(bytes), "The same valid selection must import after a transient storage failure is removed.");
    }

    private static void MissingAssetAndRecovery(string root)
    {
        var store = new LibraryStore(Path.Combine(root, "image-error-recovery", "Log", "Data")); store.Load();
        var image = ImageAttachments.ImportBytes(Picture(), store);
        var path = store.GetImagePath(image); File.Delete(path);
        var missing = Failure(() => store.GetImagePath(image));
        Assert(Describe(missing).Contains("missing from Log", StringComparison.Ordinal) && !Describe(missing).Contains("too large", StringComparison.Ordinal),
            "A missing saved asset must not be mistaken for the twenty-megabyte limit.");
        var placeholder = new AttachmentPanel(store); placeholder.Load([image], false);
        placeholder.Measure(new Size(500, 400)); placeholder.Arrange(new Rect(0, 0, 500, 400)); placeholder.UpdateLayout();
        Assert(placeholder.Capture().Single().AssetName == image.AssetName,
            "A preview failure must retain its attachment reference rather than silently remove content.");
        var log = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(store.RootPath)!, "Diagnostics"), "*.jsonl").Single();
        Assert(File.ReadLines(log).Any(line => JsonDocument.Parse(line).RootElement.GetProperty("operation").GetString() == "image-preview"),
            "Unavailable previews must record their cause in local diagnostics.");

        image = ImageAttachments.ImportBytes(Picture(), store);
        var card = new CardDocument { Note = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "Keep this inline edit" }] }] } };
        store.Upsert(card);
        var window = new MainWindow(store);
        try
        {
            var row = ((FeedView)window.FindName("NotesFeed")).RenderedCards.Single();
            row.BeginEdit(); row.Body.PlainText = "Still editing";
            typeof(MainWindow).GetMethod("ShowInputError", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new InvalidDataException("Previous image error")]);
            var status = (TextBlock)window.FindName("ErrorStatus");
            Assert(status.Visibility == Visibility.Visible, "The recovery test must begin with a visible import failure.");
            row.Attachments!.AddImages([image]);
            Assert(status.Visibility == Visibility.Collapsed && row.IsEditing && row.Body.PlainText == "Still editing",
                "A successful inline image retry must clear its obsolete input error without saving or replacing the editor.");
            row.CancelEdit();
        }
        finally { window.Close(); }
    }

    private static byte[] Picture()
    {
        var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 30, 100, 200, 255, 150, 60, 80, 128 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes = new MemoryStream(); encoder.Save(bytes); return bytes.ToArray();
    }
    private static Exception Failure(Action operation) { try { operation(); } catch (Exception error) { return error; } throw new InvalidOperationException("The synthetic failure was not reproduced."); }
    private static void Mark(Exception error, string stage) => Context.GetMethod("Mark", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [error, Enum.Parse(Stage, stage)]);
    private static string? GetStage(Exception error) => Context.GetMethod("Get", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [error])?.ToString();
    private static string Describe(Exception error) => (string)AppAssembly.GetType("Folio.App.ImageFailureMessages", true)!.GetMethod("Describe")!.Invoke(null, [error])!;
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
