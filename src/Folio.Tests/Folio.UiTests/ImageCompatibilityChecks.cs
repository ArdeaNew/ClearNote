using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Uses real WIC encoders and synthetic paste data, without opening a picker or reading the clipboard.</summary>
public static class ImageCompatibilityChecks
{
    public static void Run(string? testRoot = null)
    {
        var root = Path.Combine(testRoot ?? Path.GetTempPath(), "StillNote.ImageCompatibility-" + Guid.NewGuid().ToString("N"));
        var store = new LibraryStore(Path.Combine(root, "Data"));
        store.Load();
        EncodedVariants(store);
        ExifOrientations(store);
        MultiFrameImages(store);
        BitmapAndPngPaste(store);
        SourcePaths(root, store);
        RejectedInputsLeaveSelectionIntact(root, store);
        LargePhotoFitsAssetLimit(store);
        LargeTransparentBitmapFitsAssetLimit(store);
    }

    private static void EncodedVariants(LibraryStore store)
    {
        var formats = new[] { PixelFormats.Bgra32, PixelFormats.Pbgra32, PixelFormats.Bgr24, PixelFormats.Bgr32,
            PixelFormats.Gray8, PixelFormats.Gray16, PixelFormats.Rgb48, PixelFormats.Rgba64, PixelFormats.Cmyk32,
            PixelFormats.Indexed1, PixelFormats.Indexed2, PixelFormats.Indexed4, PixelFormats.Indexed8 };
        foreach (var format in formats)
        foreach (var codec in new[] { "png", "jpg", "gif", "bmp", "tiff" })
        {
            var bytes = Encode(Picture(format), codec);
            var expected = Decode(bytes);
            var image = ImageAttachments.ImportBytes(bytes, store);
            Assert(image.PixelWidth == expected.PixelWidth && image.PixelHeight == expected.PixelHeight,
                $"The {format}/{codec} image dimensions must survive import.");
            var saved = File.ReadAllBytes(store.GetImagePath(image));
            Assert(saved[24] == 8 && saved[25] == 6, $"The {format}/{codec} image must normalize to conventional 8-bit RGBA PNG.");
            var actual = Decode(saved);
            Assert(VisuallyEqual(Pixels(expected), Pixels(actual)), $"The decoded {format}/{codec} colors and transparency must survive conversion to 8-bit PNG.");
            Assert(Math.Abs(actual.DpiX - 96) < .1 && Math.Abs(actual.DpiY - 96) < .1,
                "An unusual source DPI must not carry through to attachment display.");
        }
    }

    private static void ExifOrientations(LibraryStore store)
    {
        // Expected source-corner order at output TL, TR, BL, BR. This also verifies mirrored orientations.
        int[][] corners = [[0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 3, 0, 1],
            [0, 2, 1, 3], [2, 0, 3, 1], [3, 1, 2, 0], [1, 3, 0, 2]];
        foreach (var codec in new[] { "jpg", "tiff" })
        foreach (ushort orientation in Enumerable.Range(1, 8))
        {
            var metadata = new BitmapMetadata(codec);
            metadata.SetQuery(codec == "jpg" ? "/app1/ifd/{ushort=274}" : "/ifd/{ushort=274}", orientation);
            var bytes = Encode(Picture(PixelFormats.Bgr24), codec, metadata);
            var original = Decode(bytes);
            var image = ImageAttachments.ImportBytes(bytes, store);
            Assert(image.PixelWidth == (orientation < 5 ? original.PixelWidth : original.PixelHeight) &&
                   image.PixelHeight == (orientation < 5 ? original.PixelHeight : original.PixelWidth),
                $"EXIF {codec} orientation {orientation} must use the displayed image dimensions.");
            var normalized = Decode(File.ReadAllBytes(store.GetImagePath(image)));
            var before = CornerPixels(original); var after = CornerPixels(normalized);
            Assert(after.Select((pixel, index) => pixel == before[corners[orientation - 1][index]]).All(equal => equal),
                $"EXIF {codec} orientation {orientation} must rotate or mirror actual image content.");
        }
    }

    private static void MultiFrameImages(LibraryStore store)
    {
        foreach (var codec in new[] { "gif", "tiff" })
        {
            var first = Picture(PixelFormats.Bgr24);
            var encoder = Encoder(codec);
            encoder.Frames.Add(BitmapFrame.Create(first));
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(13, 7, 96, 96, PixelFormats.Bgr24, null, new byte[13 * 7 * 3], 13 * 3)));
            using var stream = new MemoryStream(); encoder.Save(stream);
            var bytes = stream.ToArray();
            var image = ImageAttachments.ImportBytes(bytes, store);
            Assert(Pixels(Decode(bytes)).SequenceEqual(Pixels(Decode(File.ReadAllBytes(store.GetImagePath(image))))),
                $"A multiple-frame {codec} must import its first frame as a stable picture.");
        }
    }

    private static void BitmapAndPngPaste(LibraryStore store)
    {
        foreach (var format in new[] { PixelFormats.BlackWhite, PixelFormats.Gray2, PixelFormats.Gray4, PixelFormats.Gray32Float,
            PixelFormats.Rgb128Float, PixelFormats.Rgba128Float, PixelFormats.Prgba128Float, PixelFormats.Bgr555, PixelFormats.Bgr565, PixelFormats.Bgr101010 })
        {
            var bitmap = Picture(format);
            Assert(ImageAttachments.TryHandlePaste(new DataObject(DataFormats.Bitmap, bitmap), store, out var images) && images.Count == 1,
                $"A synthetic {format} bitmap paste must normalize successfully.");
        }
        var png = Encode(Picture(PixelFormats.Bgra32), "png");
        var bytesData = new DataObject(); bytesData.SetData("PNG", png, false);
        Assert(ImageAttachments.TryHandlePaste(bytesData, store, out var byteImages) && byteImages.Count == 1,
            "PNG clipboard bytes must import even when a Bitmap format is absent.");
        using var stream = new MemoryStream(png, writable: false); stream.Position = 4;
        var streamData = new DataObject(); streamData.SetData("PNG", stream, false);
        Assert(ImageAttachments.TryHandlePaste(streamData, store, out var streamImages) && streamImages.Single().AssetName == byteImages.Single().AssetName,
            "A PNG clipboard stream must preserve transparent pixels through the same import path.");
        Assert(stream.Position == 4 && stream.CanRead, "Importing PNG paste data must retain its caller-owned stream and position.");
        var actual = Pixels(Decode(File.ReadAllBytes(store.GetImagePath(byteImages.Single()))));
        Assert(actual.Where((_, index) => index % 4 == 3).Distinct().Count() > 2, "PNG attachment normalization must retain partial transparency.");
        Throws<InvalidDataException>(() => ImageAttachments.TryHandlePaste(bytesData, store, out _, 0),
            "PNG-only paste must obey the same four-image limit.");
    }

    private static void SourcePaths(string root, LibraryStore store)
    {
        var directory = Path.Combine(root, "Pictures # \u4e2d\u6587 \U0001F5BC", new string('a', 90), new string('b', 90));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "picture.with.a.changed-extension.dat");
        var bytes = Encode(Picture(PixelFormats.Rgba64), "png");
        File.WriteAllBytes(path, bytes);
        Assert(path.Length > 260, "The source-path fixture must exercise a path longer than the legacy Windows limit.");
        var images = ImageAttachments.ImportPaths([path], store);
        Assert(images.Count == 1 && File.ReadAllBytes(path).SequenceEqual(bytes),
            "Unicode, spaces, punctuation, long paths and an inaccurate extension must not prevent content-based image import.");
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(exclusive.Length == bytes.Length, "Imported sources must not retain a decoder file lock.");
        Assert(ImageAttachments.TryHandlePaste(new DataObject(DataFormats.FileDrop, new[] { path }), store, out var pasted) &&
               pasted.Single().AssetName == images.Single().AssetName, "File-drop paste must use the same tested source-file path.");
        var preview = ImageAttachments.LoadPreview(store.GetImagePath(images.Single()), 5);
        var pixels = Pixels(preview);
        Assert(preview.IsFrozen && preview.PixelWidth <= 5 && pixels.Length > 0,
            "Preview pixels must stay readable after the asset stream closes.");
    }

    private static void RejectedInputsLeaveSelectionIntact(string root, LibraryStore store)
    {
        var directory = Path.Combine(root, "Rejected"); Directory.CreateDirectory(directory);
        var good = Path.Combine(directory, "good.png"); var bad = Path.Combine(directory, "bad.png");
        var goodBytes = Encode(Picture(PixelFormats.Bgr24, width: 17), "png");
        File.WriteAllBytes(good, goodBytes); File.WriteAllBytes(bad, [1, 2, 3, 4]);
        var before = Directory.GetFiles(store.AssetDirectory).Order().ToArray();
        Rejects(() => ImageAttachments.ImportPaths([good, bad], store), "A damaged image in a selection must fail before any assets are written.");
        Assert(before.SequenceEqual(Directory.GetFiles(store.AssetDirectory).Order()), "A rejected selection must not partly import its valid first image.");
        Throws<FileNotFoundException>(() => ImageAttachments.ImportPaths([good, Path.Combine(directory, "missing.png")], store),
            "A missing selected file must fail before importing another selected file.");
        using (var locked = new FileStream(good, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Throws<IOException>(() => ImageAttachments.ImportPaths([good], store), "A locked source must report a recoverable read failure.");
        Throws<InvalidDataException>(() => ImageAttachments.ImportPaths([good, good], store, 1), "The remaining image slots must be checked before import.");
        Throws<InvalidDataException>(() => ImageAttachments.ImportBytes([], store), "Empty image data must be rejected.");
        Throws<InvalidDataException>(() => ImageAttachments.ImportBytes(new byte[ImageAttachments.MaxFileBytes + 1], store), "Oversized input must be rejected before decoding.");
        Rejects(() => ImageAttachments.ImportBytes(goodBytes[..30], store), "A truncated PNG must not import.");
        var tooWide = WithDimensions(goodBytes, 16001, 1);
        Throws<InvalidDataException>(() => ImageAttachments.ImportBytes(tooWide, store), "An excessive image axis must be rejected before decoding pixels.");
        var tooManyPixels = WithDimensions(goodBytes, 10000, 5000);
        Throws<InvalidDataException>(() => ImageAttachments.ImportBytes(tooManyPixels, store), "The forty-megapixel limit must remain in force before decoding.");
    }

    private static void LargePhotoFitsAssetLimit(LibraryStore store)
    {
        // Before 1.6.1 this valid 8,079,191-byte JPEG failed because its full-size PNG grew beyond 20 MiB.
        var pixels = new byte[3000 * 3000 * 3]; new Random(161).NextBytes(pixels);
        var source = BitmapSource.Create(3000, 3000, 96, 96, PixelFormats.Bgr24, null, pixels, 3000 * 3);
        var input = Encode(source, "jpg");
        Assert(input.Length < ImageAttachments.MaxFileBytes, "The large-photo regression must start with an allowed-size source.");
        var snapshot = input.ToArray();
        var image = ImageAttachments.ImportBytes(input, store);
        Assert(image.PixelWidth == 2400 && image.PixelHeight == 2400 && input.SequenceEqual(snapshot),
            "Only a PNG that exceeds the asset limit should be reduced, preserving aspect ratio and original input bytes.");
        var saved = File.ReadAllBytes(store.GetImagePath(image));
        var decoded = Decode(saved);
        Assert(saved.Length <= ImageAttachments.MaxFileBytes && decoded.PixelWidth == image.PixelWidth && decoded.PixelHeight == image.PixelHeight,
            "Reduced PNG assets must remain below the limit and record their actual encoded dimensions.");
    }

    private static void LargeTransparentBitmapFitsAssetLimit(LibraryStore store)
    {
        var pixels = new byte[3000 * 2100 * 4]; new Random(162).NextBytes(pixels);
        var source = BitmapSource.Create(3000, 2100, 96, 96, PixelFormats.Bgra32, null, pixels, 3000 * 4);
        var image = ImageAttachments.ImportBitmap(source, store);
        var saved = File.ReadAllBytes(store.GetImagePath(image));
        var result = Pixels(Decode(saved));
        Assert(image.PixelWidth < 3000 && image.PixelHeight < 2100 &&
               Math.Abs((double)image.PixelWidth / image.PixelHeight - 3000d / 2100) < .001,
            "An oversized transparent bitmap must reduce with its non-square aspect ratio intact.");
        Assert(saved.Length <= ImageAttachments.MaxFileBytes && result.Where((_, index) => index % 4 == 3).Distinct().Count() > 16,
            "Adaptive conversion must preserve varying alpha values instead of flattening transparency.");
    }

    private static BitmapSource Picture(PixelFormat format, int width = 13, int height = 7)
    {
        var stride = (width * format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * height];
        if (!format.ToString().Contains("Float"))
            for (var index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index * 7);
        var palette = format.ToString().StartsWith("Indexed", StringComparison.Ordinal)
            ? new BitmapPalette(Enumerable.Range(0, 1 << format.BitsPerPixel).Select(index =>
                Color.FromArgb((byte)(index == 0 ? 0 : 255), (byte)index, (byte)(index * 2), (byte)(index * 3))).ToList()) : null;
        var bitmap = BitmapSource.Create(width, height, 300, 72, format, palette, pixels, stride); bitmap.Freeze(); return bitmap;
    }

    private static BitmapEncoder Encoder(string codec) => codec switch
    {
        "png" => new PngBitmapEncoder(), "jpg" => new JpegBitmapEncoder { QualityLevel = 90 },
        "gif" => new GifBitmapEncoder(), "bmp" => new BmpBitmapEncoder(), _ => new TiffBitmapEncoder()
    };

    private static byte[] Encode(BitmapSource bitmap, string codec, BitmapMetadata? metadata = null)
    {
        var encoder = Encoder(codec); encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze(); return frame;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4; var pixels = new byte[stride * source.PixelHeight]; source.CopyPixels(pixels, stride, 0); return pixels;
    }

    private static uint[] CornerPixels(BitmapSource source)
    {
        var pixels = Pixels(source); var width = source.PixelWidth; var height = source.PixelHeight;
        return new[] { 0, width - 1, (height - 1) * width, height * width - 1 }
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(index * 4, 4))).ToArray();
    }

    private static bool VisuallyEqual(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length) return false;
        for (var index = 0; index < expected.Length; index += 4)
        {
            var expectedAlpha = expected[index + 3]; var actualAlpha = actual[index + 3];
            if (expectedAlpha != actualAlpha) return false;
            // WIC can round high-bit-depth transparent colors while converting through premultiplied surfaces.
            for (var channel = 0; channel < 3; channel++)
                if (Math.Abs(expected[index + channel] * expectedAlpha - actual[index + channel] * actualAlpha) > 2 * 255) return false;
        }
        return true;
    }

    private static byte[] WithDimensions(byte[] bytes, int width, int height)
    {
        var copy = bytes.ToArray(); BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(16, 4), width); BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(20, 4), height);
        uint crc = uint.MaxValue;
        foreach (var value in copy.AsSpan(12, 17))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(29, 4), ~crc); return copy;
    }

    private static void Rejects(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException(message); }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
