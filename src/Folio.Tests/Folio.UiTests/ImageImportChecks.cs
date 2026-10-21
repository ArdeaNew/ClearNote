using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App.Controls;
using Folio.Core;

internal static class ImageImportChecks
{
    public static void Run(string root)
    {
        var directory = Path.Combine(root, "async-image-import");
        Directory.CreateDirectory(directory);
        var store = new LibraryStore(Path.Combine(directory, "Data"));
        store.Load();
        var bytes = Picture();
        var source = Path.Combine(directory, "original.png");
        File.WriteAllBytes(source, bytes);

        var imported = ImageAttachments.ImportPathsAsync([source], store).GetAwaiter().GetResult().Single();
        Assert(File.ReadAllBytes(source).SequenceEqual(bytes), "Worker imports must leave the original picture unchanged.");
        var path = store.GetImagePath(imported);
        var first = ImageAttachments.LoadPreview(path, 1000);
        var second = ImageAttachments.LoadPreview(path, 1000);
        Assert(first.IsFrozen && ReferenceEquals(first, second),
            "A worker-prepared preview must be safe on the UI thread and reused when the gallery refreshes.");

        var mutable = bytes.ToArray();
        var pending = ImageAttachments.ImportBytesAsync(mutable, store);
        Array.Clear(mutable);
        Assert(pending.GetAwaiter().GetResult().AssetName == imported.AssetName,
            "Async imports must snapshot caller-owned byte buffers before the worker starts.");

        using var stream = new MemoryStream(bytes.ToArray(), writable: true);
        stream.Position = 3;
        var data = new DataObject();
        data.SetData("PNG", stream, false);
        var paste = ImageAttachments.ImportPasteAsync(data, store);
        Assert(paste is not null && stream.Position == 3, "Paste must capture PNG data on the caller thread and restore stream position.");
        stream.Position = 0;
        stream.Write(new byte[bytes.Length]);
        Assert(paste!.GetAwaiter().GetResult().Single().AssetName == imported.AssetName,
            "The worker must use captured paste bytes rather than access the UI data object or its stream later.");
        Assert(ImageAttachments.ImportPasteAsync(new DataObject(DataFormats.UnicodeText, "plain text"), store) is null,
            "Ordinary text paste must remain available to the native editor.");

        var bitmap = BitmapSource.Create(16, 12, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat((byte)125, 16 * 12 * 4).ToArray(), 16 * 4);
        var bitmapTask = ImageAttachments.ImportBitmapAsync(bitmap, store);
        Assert(!bitmap.IsFrozen, "Preparing a worker bitmap must not freeze or alter the caller's surface.");
        Assert(bitmapTask.GetAwaiter().GetResult().PixelWidth == 16,
            "An unfrozen caller bitmap must import through its worker-safe snapshot.");

        var assetCount = Directory.GetFiles(Path.Combine(store.RootPath, "assets")).Length;
        var broken = Path.Combine(directory, "broken.png");
        File.WriteAllBytes(broken, [1, 2, 3]);
        var rejected = false;
        try { ImageAttachments.ImportPathsAsync([source, broken], store).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is FileFormatException or InvalidDataException or NotSupportedException or ArgumentException) { rejected = true; }
        Assert(rejected && Directory.GetFiles(Path.Combine(store.RootPath, "assets")).Length == assetCount,
            "A damaged later picture must reject the whole worker selection before creating any asset.");

        File.Delete(path);
        var missingRejected = false;
        try { ImageAttachments.LoadPreview(path, 1000); }
        catch (FileNotFoundException) { missingRejected = true; }
        Assert(missingRejected, "A cached thumbnail must not make a deleted asset appear to exist.");
    }

    private static byte[] Picture()
    {
        var pixels = new byte[80 * 64 * 4];
        new Random(1912).NextBytes(pixels);
        var bitmap = BitmapSource.Create(80, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
