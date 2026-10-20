using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.Core;
using Microsoft.Win32;

namespace Folio.App.Controls;

/// <summary>Decodes local pictures into portable PNG assets. The clipboard is read only by the caller's paste event.</summary>
public static class ImageAttachments
{
    public const int MaxImages = NoteImages.MaxImages;
    public const int MaxDimension = NoteImages.MaxDimension;
    public const long MaxPixels = NoteImages.MaxPixels;
    public const int MaxFileBytes = NoteImages.MaxImportBytes;

    public static IReadOnlyList<NoteImage> ImportFiles(Window? owner, LibraryStore store, int remainingSlots = MaxImages)
    {
        EnsureRoom(remainingSlots, 1);
        var picker = new OpenFileDialog
        {
            Title = "Add images · ClearNote", Multiselect = true,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif;*.ico;*.jxr;*.wdp|All files|*.*",
            CheckFileExists = true, CheckPathExists = true
        };
        if ((owner is null ? picker.ShowDialog() : picker.ShowDialog(owner)) != true) return [];
        return ImportPaths(picker.FileNames, store, remainingSlots);
    }

    public static NoteImage ImportBytes(byte[] bytes, LibraryStore store)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(store);
        var image = NormalizeBytes(bytes);
        return store.ImportImage(image.Bytes, image.Width, image.Height);
    }

    private static NormalizedImage NormalizeBytes(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxFileBytes)
            throw new InvalidDataException("Choose an image smaller than 20 MB.");
        using var stream = new MemoryStream(bytes, writable: false);
        // Delay pixel decoding until the header's dimensions have passed the limits.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("This file does not contain a supported image.");
        var first = decoder.Frames[0];
        ValidateSize(first.PixelWidth, first.PixelHeight);
        return NormalizeBitmap(first, ReadOrientation(first));
    }

    public static NoteImage ImportBitmap(BitmapSource bitmap, LibraryStore store)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(store);
        var image = NormalizeBitmap(bitmap, bitmap is BitmapFrame frame ? ReadOrientation(frame) : 1);
        return store.ImportImage(image.Bytes, image.Width, image.Height);
    }

    /// <summary>Consumes synthetic or actual paste-event data without consulting the global clipboard.</summary>
    public static bool TryHandlePaste(IDataObject data, LibraryStore store, out IReadOnlyList<NoteImage> images,
        int remainingSlots = MaxImages)
    {
        ArgumentNullException.ThrowIfNull(data);
        images = [];
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            if (files.Length == 0) return false;
            images = ImportPaths(files, store, remainingSlots);
            return true;
        }
        // Browsers and image tools can offer lossless PNG clipboard data without a WPF Bitmap surface.
        var pngData = data.GetDataPresent("PNG", false) ? data.GetData("PNG", false) : null;
        if (pngData is byte[] or Stream)
        {
            EnsureRoom(remainingSlots, 1);
            images = [ImportBytes(ReadPngPaste(pngData)!, store)];
            return true;
        }
        if (!data.GetDataPresent(DataFormats.Bitmap) || data.GetData(DataFormats.Bitmap) is not BitmapSource bitmap)
            return false;
        EnsureRoom(remainingSlots, 1);
        images = [ImportBitmap(bitmap, store)];
        return true;
    }

    private static byte[]? ReadPngPaste(object? value)
    {
        if (value is byte[] bytes) return bytes;
        if (value is not Stream input) return null;
        var position = input.CanSeek ? input.Position : (long?)null;
        try
        {
            if (input.CanSeek)
            {
                if (input.Length == 0 || input.Length > MaxFileBytes) throw new InvalidDataException("Choose an image smaller than 20 MB.");
                input.Position = 0;
            }
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxFileBytes - output.Length + 1))) > 0)
            {
                if (output.Length + count > MaxFileBytes) throw new InvalidDataException("Choose an image smaller than 20 MB.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally { if (position is { } saved) input.Position = saved; }
    }

    public static BitmapSource LoadPreview(string path, int decodeWidth = 1000)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length == 0 || source.Length > MaxFileBytes) throw new InvalidDataException("The image file is too large.");
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        ValidateSize(frame.PixelWidth, frame.PixelHeight);
        source.Position = 0;
        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        preview.DecodePixelWidth = Math.Min(Math.Clamp(decodeWidth, 1, 1600), frame.PixelWidth);
        preview.StreamSource = source;
        preview.EndInit();
        preview.Freeze();
        return preview;
    }

    public static IReadOnlyList<NoteImage> ImportPaths(string[] files, LibraryStore store, int remainingSlots = MaxImages)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(store);
        EnsureRoom(remainingSlots, files.Length);
        // Read every file before writing an asset so a missing file cannot partly fill the editor.
        var contents = files.Select(file =>
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length == 0 || input.Length > MaxFileBytes) throw new InvalidDataException("Choose images smaller than 20 MB each.");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            return bytes;
        }).ToList();
        // Decode the whole selection before creating assets. A corrupt later image cannot leave a partially imported selection.
        var normalized = contents.Select(NormalizeBytes).ToList();
        return normalized.Select(image => store.ImportImage(image.Bytes, image.Width, image.Height)).ToList();
    }

    private static NormalizedImage NormalizeBitmap(BitmapSource bitmap, int orientation)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        ValidateSize(width, height);
        // WIC codecs expose indexed, CMYK, high-bit-depth and premultiplied surfaces. Materialize a conventional
        // pixel buffer while the input stream is open, then encode a fresh surface without source metadata or DPI.
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap :
            new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(pixels, stride, 0);
        if (orientation is >= 2 and <= 8)
        {
            var rotatedWidth = orientation >= 5 ? height : width;
            var rotatedHeight = orientation >= 5 ? width : height;
            var oriented = new byte[pixels.Length];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (targetX, targetY) = orientation switch
                {
                    2 => (width - 1 - x, y),
                    3 => (width - 1 - x, height - 1 - y),
                    4 => (x, height - 1 - y),
                    5 => (y, x),
                    6 => (height - 1 - y, x),
                    7 => (height - 1 - y, width - 1 - x),
                    _ => (y, width - 1 - x)
                };
                Buffer.BlockCopy(pixels, (y * width + x) * 4, oriented, (targetY * rotatedWidth + targetX) * 4, 4);
            }
            pixels = oriented;
            width = rotatedWidth;
            height = rotatedHeight;
        }
        var clean = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        clean.Freeze();
        BitmapSource candidate = clean;
        var scale = 1d;
        while (true)
        {
            var encoded = EncodePngWithinLimit(candidate);
            if (encoded is not null) return new NormalizedImage(encoded, candidate.PixelWidth, candidate.PixelHeight);
            // A valid compressed photo can grow beyond the asset limit as a lossless PNG. Reduce only those
            // pictures, keeping the original aspect ratio, transparency and input file intact.
            scale *= 0.8;
            if (Math.Round(width * scale) < 1 || Math.Round(height * scale) < 1)
                throw new InvalidDataException("The image could not be converted within the 20 MB image limit.");
            candidate = new TransformedBitmap(clean, new ScaleTransform(scale, scale));
            candidate.Freeze();
        }
    }

    private static byte[]? EncodePngWithinLimit(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var normalized = new LimitedImageStream();
        try { encoder.Save(normalized); }
        catch (Exception error) when (normalized.LimitExceeded && error is IOException or InvalidOperationException or NotSupportedException or COMException)
        { return null; }
        return normalized.LimitExceeded ? null : normalized.ToArray();
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata) return 1;
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                if (metadata.GetQuery(query) is ushort orientation && orientation is >= 1 and <= 8) return orientation;
        }
        // Broken or unsupported metadata must not make otherwise decodable pixels unusable.
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException or COMException) { }
        return 1;
    }

    private sealed record NormalizedImage(byte[] Bytes, int Width, int Height);

    /// <summary>Stops an encoder at the asset limit instead of buffering a possible 160 MB noisy PNG.</summary>
    private sealed class LimitedImageStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        public bool LimitExceeded { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => _buffer.Position = value; }
        public byte[] ToArray() => _buffer.ToArray();
        public override void Flush() => _buffer.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);
        public override void SetLength(long value) { CheckLength(value); _buffer.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count)
        { CheckLength(checked(Position + count)); _buffer.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { CheckLength(checked(Position + buffer.Length)); _buffer.Write(buffer); }
        public override void WriteByte(byte value)
        { CheckLength(checked(Position + 1)); _buffer.WriteByte(value); }
        private void CheckLength(long length)
        {
            if (length <= MaxFileBytes) return;
            LimitExceeded = true;
            throw new IOException("The normalized PNG exceeds the image size limit.");
        }
        protected override void Dispose(bool disposing)
        { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
    }

    private static void EnsureRoom(int available, int count)
    {
        if (count <= 0 || count > Math.Min(MaxImages, available))
            throw new InvalidDataException("A note can contain up to four images. Remove an image to add another.");
    }

    private static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
            throw new InvalidDataException("Choose an image up to 40 megapixels and 16,000 pixels on either side.");
    }
}
