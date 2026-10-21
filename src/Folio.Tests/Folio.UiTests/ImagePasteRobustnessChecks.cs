using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Clipboard alternatives use synthetic IDataObjects, never the desktop clipboard.</summary>
internal static class ImagePasteRobustnessChecks
{
    public static void Run(string root)
    {
        PngAlternatives(root);
        LimitsRemainStrict(root);
        IndependentBitmapSnapshot(root);
        FileDropAndStorageFailures(root);
    }

    private static void PngAlternatives(string root)
    {
        var store = Store(root, "representations");
        var bitmap = Picture(32, 20, 70);
        var expected = Pixels(bitmap);
        using var empty = new MemoryStream();
        using var disposed = new MemoryStream([1, 2, 3]); disposed.Dispose();
        using var failed = new FailingReadStream();
        var variants = new object[] { Array.Empty<byte>(), new byte[] { 1, 2, 3 }, empty, disposed, failed, "unsupported PNG object" };
        foreach (var representation in variants)
        {
            var data = new DataObject();
            data.SetData("PNG", representation, false);
            data.SetData(DataFormats.Bitmap, bitmap);
            var imported = ImageAttachments.ImportPasteAsync(data, store)!.GetAwaiter().GetResult().Single();
            Assert(Pixels(Read(store.GetImagePath(imported))).SequenceEqual(expected),
                "An empty, damaged or inaccessible PNG representation must fall back to the independent usable Bitmap.");
            Assert(ImageAttachments.TryHandlePaste(data, store, out var sync) && sync.Single().AssetName == imported.AssetName,
                "Synchronous and worker paste must use the same representation policy.");
        }
        var faulting = new UnavailablePng(bitmap);
        var recovered = ImageAttachments.ImportPasteAsync(faulting, store)!.GetAwaiter().GetResult().Single();
        Assert(Pixels(Read(store.GetImagePath(recovered))).SequenceEqual(expected),
            "A provider throwing while retrieving PNG must still permit its usable Bitmap representation.");
        foreach (var format in new[] { "PNG", DataFormats.FileDrop })
        {
            var unavailableProbe = new UnavailablePng(bitmap, format);
            recovered = ImageAttachments.ImportPasteAsync(unavailableProbe, store)!.GetAwaiter().GetResult().Single();
            Assert(Pixels(Read(store.GetImagePath(recovered))).SequenceEqual(expected),
                "A provider failing one format probe must not hide another independently available Bitmap.");
        }

        var png = Encode(Picture(11, 7, 150));
        var preferred = new DataObject(); preferred.SetData("PNG", png, false); preferred.SetData(DataFormats.Bitmap, bitmap);
        var original = ImageAttachments.ImportPasteAsync(preferred, store)!.GetAwaiter().GetResult().Single();
        Assert(File.ReadAllBytes(store.GetImagePath(original)).SequenceEqual(png),
            "A healthy lossless PNG representation must keep priority over an alternate Bitmap.");

        using var nonseekable = new ReadOnlyUnseekable(png);
        var streamed = new DataObject(); streamed.SetData("PNG", nonseekable, false);
        var captured = ImageAttachments.ImportPasteAsync(streamed, store)!;
        nonseekable.Dispose();
        Assert(captured.GetAwaiter().GetResult().Single().AssetName == original.AssetName,
            "A nonseekable PNG stream must be captured before the caller disposes it.");
        Assert(ImageAttachments.ImportPasteAsync(new DataObject(DataFormats.UnicodeText, "Ordinary text"), store) is null,
            "Ordinary text must retain native paste behavior.");

        var unsupported = new DataObject(); unsupported.SetData("PNG", "unsupported PNG object", false);
        unsupported.SetData(DataFormats.UnicodeText, "Keep this text");
        Assert(ImageAttachments.ImportPasteAsync(unsupported, store) is null &&
            !ImageAttachments.TryHandlePaste(unsupported, store, out _),
            "An unrelated advertised PNG object must not consume a normal text paste.");
        var before = Assets(store);
        var broken = new DataObject(); broken.SetData("PNG", new byte[] { 1, 2, 3 }, false);
        Reject(() => ImageAttachments.ImportPasteAsync(broken, store)!.GetAwaiter().GetResult());
        Assert(Assets(store).SequenceEqual(before), "A rejected paste without a valid alternative must not create assets.");
    }

    private static void LimitsRemainStrict(string root)
    {
        var store = Store(root, "limits");
        var bitmap = Picture(2, 2, 30);
        var oversized = new DataObject(); oversized.SetData("PNG", new byte[ImageAttachments.MaxFileBytes + 1], false);
        oversized.SetData(DataFormats.Bitmap, bitmap);
        Throws<InvalidDataException>(() => ImageAttachments.ImportPasteAsync(oversized, store),
            "A usable alternate must not bypass the encoded clipboard-size limit.");
        Throws<InvalidDataException>(() => ImageAttachments.TryHandlePaste(oversized, store, out _),
            "Synchronous paste must enforce the same encoded-size guard before fallback.");
        var tooWide = Picture(ImageAttachments.MaxDimension + 1, 1, 90);
        var dimensions = new DataObject(); dimensions.SetData(DataFormats.Bitmap, tooWide);
        Throws<InvalidDataException>(() => ImageAttachments.ImportPasteAsync(dimensions, store),
            "Bitmap dimensions must be rejected before allocating a detached surface.");
        var valid = new DataObject(); valid.SetData(DataFormats.Bitmap, bitmap);
        Throws<InvalidDataException>(() => ImageAttachments.ImportPasteAsync(valid, store, 0),
            "Paste must reject a full attachment list before capturing any surface.");
        Assert(Assets(store).Length == 0, "Rejected limits must not create any managed asset.");
    }

    private static void IndependentBitmapSnapshot(string root)
    {
        var store = Store(root, "bitmap-snapshot");
        var original = new WriteableBitmap(Picture(40, 24, 110));
        var expected = Pixels(original);
        var operation = ImageAttachments.ImportBitmapAsync(original, store);
        original.WritePixels(new Int32Rect(0, 0, 40, 24), new byte[40 * 24 * 4], 40 * 4, 0);
        var saved = operation.GetAwaiter().GetResult();
        Assert(!original.IsFrozen && Pixels(Read(store.GetImagePath(saved))).SequenceEqual(expected),
            "Worker bitmap import must own detached pixels while leaving the original mutable surface unchanged by the importer.");

        var encoded = Encode(Picture(18, 12, 190));
        var source = new MemoryStream(encoded, writable: false);
        var lazy = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        var snapshot = ImageAttachments.ImportBitmapAsync(lazy, store);
        source.Dispose();
        var detached = snapshot.GetAwaiter().GetResult();
        Assert(detached.PixelWidth == 18 && detached.PixelHeight == 12 && Pixels(Read(store.GetImagePath(detached))).Length > 0,
            "A worker must not retain an external lazy decoder stream after bitmap capture returns.");

        var metadata = new BitmapMetadata("jpg"); metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Picture(18, 12, 210), null, metadata, null));
        using var bytes = new MemoryStream(); encoder.Save(bytes); bytes.Position = 0;
        var frame = BitmapDecoder.Create(bytes, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var oriented = ImageAttachments.ImportBitmapAsync(frame, store).GetAwaiter().GetResult();
        Assert(oriented.PixelWidth == 12 && oriented.PixelHeight == 18,
            "Detaching a BitmapFrame must preserve its orientation separately from its raw pixels.");
    }

    private static void FileDropAndStorageFailures(string root)
    {
        var store = Store(root, "filedrop");
        Directory.CreateDirectory(store.RootPath);
        var good = Path.Combine(store.RootPath, "source.dat"); File.WriteAllBytes(good, Encode(Picture(5, 3, 60)));
        var other = Path.Combine(store.RootPath, "ordinary.txt"); File.WriteAllText(other, "This is not an image.");
        var before = Assets(store);
        var files = new DataObject(DataFormats.FileDrop, new[] { good, other });
        Reject(() => ImageAttachments.ImportPasteAsync(files, store)!.GetAwaiter().GetResult());
        Assert(Assets(store).SequenceEqual(before) && File.ReadAllText(other) == "This is not an image.",
            "A mixed file selection must reject before writing assets or modifying its original files.");

        var blocked = Store(root, "storage"); Directory.CreateDirectory(blocked.RootPath);
        File.WriteAllText(blocked.AssetDirectory, "Occupied by a file");
        var data = new DataObject(); data.SetData("PNG", Encode(Picture(3, 2, 45)), false);
        data.SetData(DataFormats.Bitmap, Picture(4, 2, 75));
        var error = Reject(() => ImageAttachments.ImportPasteAsync(data, blocked)!.GetAwaiter().GetResult());
        Assert(Stage(error) == "Store" && File.ReadAllText(blocked.AssetDirectory) == "Occupied by a file",
            "A Log write failure must retain Store context and must never retry by decoding another clipboard representation.");
        var syncError = Reject(() => ImageAttachments.TryHandlePaste(data, blocked, out _));
        Assert(Stage(syncError) == "Store", "Synchronous paste must not relabel a storage failure as clipboard corruption.");
    }

    private static LibraryStore Store(string root, string name) { var store = new LibraryStore(Path.Combine(root, "paste-robustness", name, "Data")); store.Load(); return store; }
    private static string[] Assets(LibraryStore store) => Directory.Exists(store.AssetDirectory) ? Directory.GetFiles(store.AssetDirectory).Order().ToArray() : [];
    private static BitmapSource Picture(int width, int height, byte value)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4) { pixels[index] = value; pixels[index + 1] = (byte)(value / 2); pixels[index + 2] = 190; pixels[index + 3] = 255; }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }
    private static byte[] Encode(BitmapSource bitmap) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray(); }
    private static BitmapSource Read(string path) { using var stream = File.OpenRead(path); return BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0]; }
    private static byte[] Pixels(BitmapSource bitmap) { BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); var stride = source.PixelWidth * 4; var pixels = new byte[stride * source.PixelHeight]; source.CopyPixels(pixels, stride, 0); return pixels; }
    private static string? Stage(Exception error) => typeof(MainWindow).Assembly.GetType("Folio.App.ImageFailureContext")!.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, [error])?.ToString();
    private static Exception Reject(Action action) { try { action(); } catch (Exception error) { return error; } throw new InvalidOperationException("An invalid picture must fail."); }
    private static void Throws<T>(Action action, string message) where T : Exception { Assert(Reject(action) is T, message); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Synthetic clipboard provider failure.");
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class ReadOnlyUnseekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public override bool CanRead => _inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class UnavailablePng : IDataObject
    {
        private readonly DataObject _inner = new();
        private readonly string? _failedProbe;
        public UnavailablePng(BitmapSource bitmap, string? failedProbe = null) { _failedProbe = failedProbe; _inner.SetData("PNG", new byte[] { 1 }, false); _inner.SetData(DataFormats.Bitmap, bitmap); }
        public object? GetData(string format, bool autoConvert) => format == "PNG" ? throw new COMException("Synthetic unavailable PNG representation") : _inner.GetData(format, autoConvert);
        public object? GetData(string format) => GetData(format, true); public object? GetData(Type format) => _inner.GetData(format);
        public bool GetDataPresent(string format, bool autoConvert) => format == _failedProbe ? throw new COMException("Synthetic unavailable format probe") : _inner.GetDataPresent(format, autoConvert); public bool GetDataPresent(string format) => GetDataPresent(format, true); public bool GetDataPresent(Type format) => _inner.GetDataPresent(format);
        public string[] GetFormats(bool autoConvert) => _inner.GetFormats(autoConvert); public string[] GetFormats() => _inner.GetFormats();
        public void SetData(string format, object data, bool autoConvert) => _inner.SetData(format, data, autoConvert); public void SetData(string format, object data) => _inner.SetData(format, data); public void SetData(Type format, object data) => _inner.SetData(format, data); public void SetData(object data) => _inner.SetData(data);
    }
}
