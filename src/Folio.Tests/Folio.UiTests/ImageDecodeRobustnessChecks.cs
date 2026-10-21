using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Real decoder regressions for readable source containers that are unsuitable as stored assets.</summary>
internal static class ImageDecodeRobustnessChecks
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "image-container-compatibility");
        Directory.CreateDirectory(directory);
        var jpeg = Encode(jpeg: true);
        var png = Encode(jpeg: false);
        var variants = new (string Name, byte[] Bytes, byte[] Expected)[]
        {
            ("jpeg-trailing-padding", [.. jpeg, 0, 0, 0, 0], jpeg),
            ("jpeg-trailing-metadata", [.. jpeg, .. "Editor index\0"u8.ToArray()], jpeg),
            ("jpeg-duplicate-eoi", [.. jpeg, 255, 217], jpeg),
            ("jpeg-concatenated", [.. jpeg, .. jpeg], jpeg),
            ("jpeg-missing-terminal-eoi", jpeg[..^2], jpeg),
            ("png-trailing-padding", [.. png, 0, 0, 0, 0], png),
            ("png-duplicate-iend", [.. png, .. png[^12..]], png),
            ("png-ignored-ancillary-crc", WithInvalidAncillaryCrc(png), png)
        };
        foreach (var (name, bytes, expected) in variants)
            ReadableSourceNormalizes(directory, name, bytes, expected);
        PortableBytesStayUnchanged(directory, jpeg, png);
        RejectedBatchWritesNothing(directory, variants[0].Bytes, png);
    }

    private static void ReadableSourceNormalizes(string root, string name, byte[] bytes, byte[] expected)
    {
        var snapshot = bytes.ToArray();
        var originalPixels = Pixels(Decode(expected));
        var readable = Decode(bytes);
        Assert(Pixels(readable).SequenceEqual(originalPixels),
            $"The {name} fixture must decode every expected pixel before it exercises import compatibility.");
        RejectsStrictAsset(bytes, readable.PixelWidth, readable.PixelHeight);
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        // The extension deliberately disagrees with the encoded header: import is content based.
        var source = Path.Combine(directory, "source-picture.dat");
        File.WriteAllBytes(source, bytes);
        var store = new LibraryStore(Path.Combine(directory, "Log", "Data"));
        store.Load();
        var image = ImageAttachments.ImportPathsAsync([source], store).GetAwaiter().GetResult().Single();
        var saved = File.ReadAllBytes(store.GetImagePath(image));
        Assert(image.AssetName.EndsWith(".png", StringComparison.Ordinal) &&
            NoteImages.ValidateBytes(saved, image.PixelWidth, image.PixelHeight) == "png" &&
            Pixels(Decode(saved)).SequenceEqual(originalPixels),
            $"A WIC-readable {name} must become a strict PNG asset with the same decoded pixels.");
        Assert(File.ReadAllBytes(source).SequenceEqual(snapshot) && bytes.SequenceEqual(snapshot),
            $"Importing {name} must never rewrite the selected file or caller-owned buffer.");
        store.Upsert(new CardDocument
        {
            Note = new NoteContent
            {
                Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "Synthetic container regression" }] }],
                Images = [image]
            }
        });
        var reopened = new LibraryStore(store.RootPath);
        reopened.Load();
        var reference = NoteText.Read(reopened.Cards.Single()).Images.Single();
        var coldPath = Path.Combine(directory, "cold-preview.png");
        File.Copy(reopened.GetImagePath(reference), coldPath);
        var preview = ImageAttachments.LoadPreview(coldPath, 37);
        Assert(preview.IsFrozen && preview.PixelWidth > 0 && preview.PixelHeight > 0 &&
            Math.Max(preview.PixelWidth, preview.PixelHeight) <= 37 && Math.Abs(preview.DpiX - 96) < .1 &&
            Math.Abs(preview.DpiY - 96) < .1 && ReferenceEquals(preview, ImageAttachments.LoadPreview(coldPath, 37)),
            $"The normalized {name} must persist, reopen and produce a detached cold preview reused by later rendering.");
        using var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert(exclusive.Length == snapshot.Length, "Decoder and source streams must be released after importing.");
    }

    private static void PortableBytesStayUnchanged(string root, byte[] jpeg, byte[] png)
    {
        var store = new LibraryStore(Path.Combine(root, "portable", "Data"));
        store.Load();
        foreach (var bytes in new[] { jpeg, png })
        {
            var image = ImageAttachments.ImportBytes(bytes, store);
            Assert(File.ReadAllBytes(store.GetImagePath(image)).SequenceEqual(bytes),
                "Strict original PNG/JPEG files must retain their fast, lossless byte-copy path.");
        }
    }

    private static void RejectedBatchWritesNothing(string root, byte[] readable, byte[] png)
    {
        var directory = Path.Combine(root, "rejected-batch");
        Directory.CreateDirectory(directory);
        var good = Path.Combine(directory, "readable-with-trailer.jpg");
        var bad = Path.Combine(directory, "truncated-pixels.png");
        File.WriteAllBytes(good, readable);
        File.WriteAllBytes(bad, png[..30]);
        var store = new LibraryStore(Path.Combine(directory, "Data"));
        store.Load();
        var failed = false;
        try { ImageAttachments.ImportPaths([good, bad], store); }
        catch (Exception error) when (error is IOException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException or COMException)
        { failed = true; }
        Assert(failed && (!Directory.Exists(store.AssetDirectory) || !Directory.EnumerateFiles(store.AssetDirectory).Any()),
            "A later image with unreadable pixels must reject the selection before even its normalized readable asset is written.");
        Assert(File.ReadAllBytes(good).SequenceEqual(readable), "Rejected batch imports must leave the valid source intact.");
    }

    private static void RejectsStrictAsset(byte[] bytes, int width, int height)
    {
        try { NoteImages.ValidateBytes(bytes, width, height); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("The source fixture must exercise a real strict-asset rejection, not just another valid encoded picture.");
    }

    private static byte[] WithInvalidAncillaryCrc(byte[] png)
    {
        // tEXt: note\0test. WIC ignores this non-pixel metadata CRC; Core correctly
        // rejects it as a durable asset until the actual pixels are re-encoded.
        byte[] textChunk = [0, 0, 0, 9, .. "tEXt"u8.ToArray(), .. "note\0test"u8.ToArray(), 0, 0, 0, 0];
        return [.. png[..^12], .. textChunk, .. png[^12..]];
    }

    private static byte[] Encode(bool jpeg)
    {
        const int width = 40, height = 30;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)(index * 13);
            pixels[index + 1] = (byte)(index * 7);
            pixels[index + 2] = (byte)(index * 3);
            pixels[index + 3] = (byte)(index / 4 % 256);
        }
        var bitmap = BitmapSource.Create(width, height, 300, 72, PixelFormats.Bgra32, null, pixels, width * 4);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 90 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        BitmapSource converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = Pixels(converted);
        var detached = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, converted.PixelWidth * 4);
        detached.Freeze();
        return detached;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        if (bitmap.Format != PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var bytes = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(bytes, stride, 0);
        return bytes;
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
