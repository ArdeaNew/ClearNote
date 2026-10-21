using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.Core;
using Microsoft.Win32;

namespace Folio.App.Controls;

/// <summary>Validates portable PNG/JPEG copies and converts other pictures with Windows codecs.</summary>
public static class ImageAttachments
{
    public const int MaxImages = NoteImages.MaxImages;
    public const int MaxDimension = NoteImages.MaxDimension;
    public const long MaxPixels = NoteImages.MaxPixels;
    public const int MaxFileBytes = NoteImages.MaxImportBytes;
    private const int GalleryDecodeWidth = 1000;
    private const long MaxCachedPixels = 12_000_000;
    private const int MaxCachedPreviews = 64;
    private static readonly object PreviewGate = new();
    private static readonly Dictionary<PreviewKey, LinkedListNode<CachedPreview>> Previews = [];
    private static readonly LinkedList<CachedPreview> PreviewOrder = new();
    private static long _cachedPixels;

    public static IReadOnlyList<NoteImage> ImportFiles(Window? owner, LibraryStore store, int remainingSlots = MaxImages)
    {
        var files = PickFiles(owner, remainingSlots);
        return files.Length == 0 ? [] : ImportPaths(files, store, remainingSlots);
    }

    /// <summary>The picker stays on the caller's UI thread; codec work and durable copying do not block it.</summary>
    public static async Task<IReadOnlyList<NoteImage>> ImportFilesAsync(Window? owner, LibraryStore store,
        int remainingSlots = MaxImages)
    {
        var files = PickFiles(owner, remainingSlots);
        return files.Length == 0 ? [] : await ImportPathsAsync(files, store, remainingSlots);
    }

    private static string[] PickFiles(Window? owner, int remainingSlots)
    {
        EnsureRoom(remainingSlots, 1);
        var picker = new OpenFileDialog
        {
            Title = "Add images · ClearNote", Multiselect = true,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif;*.ico;*.jxr;*.wdp|All files|*.*",
            CheckFileExists = true, CheckPathExists = true
        };
        return (owner is null ? picker.ShowDialog() : picker.ShowDialog(owner)) == true ? picker.FileNames : [];
    }

    public static Task<IReadOnlyList<NoteImage>> ImportPathsAsync(string[] files, LibraryStore store,
        int remainingSlots = MaxImages)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(store);
        EnsureRoom(remainingSlots, files.Length);
        var snapshot = files.ToArray();
        return Task.Run(() => ImportPaths(snapshot, store, remainingSlots));
    }

    public static Task<NoteImage> ImportBytesAsync(byte[] bytes, LibraryStore store)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(store);
        ValidateFileLength(bytes.Length);
        var snapshot = bytes.ToArray();
        return Task.Run(() => ImportBytes(snapshot, store));
    }

    public static Task<NoteImage> ImportBitmapAsync(BitmapSource bitmap, LibraryStore store)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(store);
        var snapshot = SnapshotBitmap(bitmap);
        return Task.Run(() => StoreImage(DecodeBitmapSnapshot(snapshot), store));
    }

    /// <summary>Captures paste-event data now, then imports on a worker. Null leaves ordinary text paste native.</summary>
    public static Task<IReadOnlyList<NoteImage>>? ImportPasteAsync(IDataObject data, LibraryStore store,
        int remainingSlots = MaxImages)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            Exception? captureError = null;
            if (CaptureFileDrop(data, ref captureError) is { Length: > 0 } files)
                return ImportPathsAsync(files, store, remainingSlots);
            var picture = CapturePastePicture(data, remainingSlots, captureError);
            return picture is null ? null : Task.Run<IReadOnlyList<NoteImage>>(() => [StoreImage(DecodePastePicture(picture), store)]);
        }
        catch (Exception error) { ImageFailureContext.Mark(error, ImageFailureStage.Clipboard); throw; }
    }

    private sealed record BitmapSnapshot(BitmapSource Pixels, int Orientation);
    private sealed record PastePicture(byte[]? Png, BitmapSnapshot? Bitmap);

    private static BitmapSnapshot SnapshotBitmap(BitmapSource bitmap)
    {
        ValidateSize(bitmap.PixelWidth, bitmap.PixelHeight);
        var orientation = bitmap is BitmapFrame frame ? ReadOrientation(frame) : 1;
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap :
            new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        // A frozen clone can still retain a delayed decoder and its caller-owned stream.
        // Detach pixels now while clipboard data is available; only encoding runs on the worker.
        var detached = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        detached.Freeze();
        return new BitmapSnapshot(detached, orientation);
    }

    private static string[]? CaptureFileDrop(IDataObject data, ref Exception? captureError)
    {
        try { return data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null; }
        catch (Exception error) when (IsPictureRepresentationFailure(error))
        { ImageFailureContext.Mark(error, ImageFailureStage.Clipboard); captureError ??= error; return null; }
    }

    private static bool HasPasteFormat(IDataObject data, string format, bool autoConvert, ref Exception? captureError)
    {
        try { return data.GetDataPresent(format, autoConvert); }
        catch (Exception error) when (IsPictureRepresentationFailure(error))
        { ImageFailureContext.Mark(error, ImageFailureStage.Clipboard); captureError ??= error; return false; }
    }

    private static PastePicture? CapturePastePicture(IDataObject data, int remainingSlots, Exception? captureError)
    {
        var hasPng = HasPasteFormat(data, "PNG", false, ref captureError);
        var hasBitmap = HasPasteFormat(data, DataFormats.Bitmap, true, ref captureError);
        if (!hasPng && !hasBitmap)
        {
            if (captureError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captureError).Throw();
            return null;
        }
        EnsureRoom(remainingSlots, 1);
        byte[]? png = null;
        BitmapSnapshot? bitmap = null;
        var limitExceeded = false;
        try
        {
            if (hasPng && ReadPngPaste(data.GetData("PNG", false), out limitExceeded) is { } bytes)
                png = bytes.ToArray();
        }
        catch (Exception error) when (!limitExceeded && IsPictureRepresentationFailure(error))
        {
            ImageFailureContext.Mark(error, ImageFailureStage.Clipboard);
            captureError = error;
        }
        BitmapSource? source = null;
        try { if (hasBitmap) source = data.GetData(DataFormats.Bitmap) as BitmapSource; }
        catch (Exception error) when (IsPictureRepresentationFailure(error))
        {
            ImageFailureContext.Mark(error, ImageFailureStage.Clipboard);
            captureError ??= error;
        }
        if (source is not null)
        {
            // Size limits apply to every supplied image surface, before fallback or allocation.
            ValidateSize(source.PixelWidth, source.PixelHeight);
            try { bitmap = SnapshotBitmap(source); }
            catch (Exception error) when (IsPictureRepresentationFailure(error))
            {
                ImageFailureContext.Mark(error, ImageFailureStage.Clipboard);
                captureError ??= error;
            }
        }
        if (png is null && bitmap is null && captureError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captureError).Throw();
        return png is null && bitmap is null ? null : new PastePicture(png, bitmap);
    }

    private static NormalizedImage DecodePastePicture(PastePicture picture)
    {
        if (picture.Png is not null)
        {
            try { return NormalizeBytes(picture.Png); }
            catch (Exception error) when (IsPictureRepresentationFailure(error))
            {
                if (picture.Bitmap is null) { ImageFailureContext.Mark(error, ImageFailureStage.Decode); throw; }
                // A clipboard can advertise a broken PNG alongside a usable DIB. Decode an
                // alternate representation before storing anything; storage failures never retry.
            }
        }
        return DecodeBitmapSnapshot(picture.Bitmap!);
    }

    private static NormalizedImage DecodeBitmapSnapshot(BitmapSnapshot snapshot)
    {
        try { return NormalizeBitmap(snapshot.Pixels, snapshot.Orientation); }
        catch (Exception error) { ImageFailureContext.Mark(error, ImageFailureStage.Decode); throw; }
    }

    private static bool IsPictureRepresentationFailure(Exception error) => error is
        InvalidDataException or IOException or InvalidOperationException or NotSupportedException or
        ArgumentException or FormatException or ExternalException;

    public static NoteImage ImportBytes(byte[] bytes, LibraryStore store)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(store);
        var image = NormalizeBytes(bytes);
        return StoreImage(image, store);
    }

    private static NormalizedImage NormalizeBytes(byte[] bytes)
    {
        try { return NormalizeBytesCore(bytes); }
        catch (Exception error) { ImageFailureContext.Mark(error, ImageFailureStage.Decode); throw; }
    }

    private static NormalizedImage NormalizeBytesCore(byte[] bytes)
    {
        ValidateFileLength(bytes.Length);
        using var stream = new MemoryStream(bytes, writable: false);
        // Delay pixel decoding until the header's dimensions have passed the limits.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("This file does not contain a supported image.");
        var first = decoder.Frames[0];
        ValidateSize(first.PixelWidth, first.PixelHeight);
        var orientation = ReadOrientation(first);
        if (orientation == 1 && decoder is PngBitmapDecoder or JpegBitmapDecoder)
        {
            // Core deliberately accepts a narrow, self-contained asset format. Camera and
            // editing software can append JPEG/PNG metadata or use containers WIC can read
            // but that are unsuitable for direct storage. Normalize those decoded pixels;
            // never weaken asset validation or ask the user to re-export a readable picture.
            try { NoteImages.ValidateBytes(bytes, first.PixelWidth, first.PixelHeight); }
            catch (InvalidDataException) { return NormalizeBitmap(first, orientation); }
            // Strict portable sources keep their bytes after full pixel validation.
            BitmapSource source = first.Format == PixelFormats.Bgra32 ? first :
                new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0);
            var stride = checked(source.PixelWidth * 4);
            source.CopyPixels(new byte[checked(stride * source.PixelHeight)], stride, 0);
            return new NormalizedImage(bytes, first.PixelWidth, first.PixelHeight, CreatePreview(source));
        }
        return NormalizeBitmap(first, orientation);
    }

    public static NoteImage ImportBitmap(BitmapSource bitmap, LibraryStore store)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(store);
        var image = NormalizeBitmap(bitmap, bitmap is BitmapFrame frame ? ReadOrientation(frame) : 1);
        return StoreImage(image, store);
    }

    /// <summary>Consumes synthetic or actual paste-event data without consulting the global clipboard.</summary>
    public static bool TryHandlePaste(IDataObject data, LibraryStore store, out IReadOnlyList<NoteImage> images,
        int remainingSlots = MaxImages)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(store);
        images = [];
        try
        {
            Exception? captureError = null;
            if (CaptureFileDrop(data, ref captureError) is { Length: > 0 } files)
            {
                images = ImportPaths(files, store, remainingSlots);
                return true;
            }
            var picture = CapturePastePicture(data, remainingSlots, captureError);
            if (picture is null) return false;
            images = [StoreImage(DecodePastePicture(picture), store)];
            return true;
        }
        catch (Exception error) { ImageFailureContext.Mark(error, ImageFailureStage.Clipboard); throw; }
    }

    private static byte[]? ReadPngPaste(object? value, out bool limitExceeded)
    {
        limitExceeded = false;
        if (value is byte[] bytes)
        {
            limitExceeded = bytes.Length > MaxFileBytes;
            if (limitExceeded) ValidateFileLength(bytes.Length);
            if (bytes.Length == 0) throw new InvalidDataException("The clipboard image is empty.");
            return bytes;
        }
        if (value is not Stream input) return null;
        var position = input.CanSeek ? input.Position : (long?)null;
        try
        {
            if (input.CanSeek)
            {
                limitExceeded = input.Length > MaxFileBytes;
                if (limitExceeded) throw new InvalidDataException("Choose an image smaller than 20 MB.");
                if (input.Length == 0) throw new InvalidDataException("The clipboard image is empty.");
                input.Position = 0;
            }
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxFileBytes - output.Length + 1))) > 0)
            {
                if (output.Length + count > MaxFileBytes)
                {
                    limitExceeded = true;
                    throw new InvalidDataException("Choose an image smaller than 20 MB.");
                }
                output.Write(buffer, 0, count);
            }
            if (output.Length == 0) throw new InvalidDataException("The clipboard image is empty.");
            return output.ToArray();
        }
        finally { if (position is { } saved) input.Position = saved; }
    }

    public static BitmapSource LoadPreview(string path, int decodeWidth = 1000)
    {
        decodeWidth = Math.Clamp(decodeWidth, 1, 1600);
        var key = PreviewFileKey(path, decodeWidth);
        lock (PreviewGate)
        {
            if (Previews.TryGetValue(key, out var cached))
            {
                PreviewOrder.Remove(cached);
                PreviewOrder.AddLast(cached);
                return cached.Value.Bitmap;
            }
        }
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length == 0 || source.Length > MaxFileBytes) throw new InvalidDataException("The image file is too large.");
        var display = DecodePreview(source, decodeWidth);
        CachePreview(key, display);
        return display;
    }

    private static BitmapSource DecodePreview(Stream source, int decodeWidth)
    {
        try { return DecodePreviewCore(source, decodeWidth); }
        catch (Exception error) { ImageFailureContext.Mark(error, ImageFailureStage.Decode); throw; }
    }

    private static BitmapSource DecodePreviewCore(Stream source, int decodeWidth)
    {
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("This file does not contain a supported image.");
        var frame = decoder.Frames[0];
        ValidateSize(frame.PixelWidth, frame.PixelHeight);
        source.Position = 0;
        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        // A portrait does not need a many-thousand-pixel-tall decode for a 308-DIP gallery.
        // Set only the limiting axis so WIC preserves its natural proportions.
        if (frame.PixelHeight > frame.PixelWidth) preview.DecodePixelHeight = Math.Min(decodeWidth, frame.PixelHeight);
        else preview.DecodePixelWidth = Math.Min(decodeWidth, frame.PixelWidth);
        preview.StreamSource = source;
        preview.EndInit();
        return CreatePreview(preview, decodeWidth);
    }

    public static IReadOnlyList<NoteImage> ImportPaths(string[] files, LibraryStore store, int remainingSlots = MaxImages)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(store);
        EnsureRoom(remainingSlots, files.Length);
        // Validate and normalize the whole selection before writing assets. Read each source only
        // while it is being decoded, rather than retaining four large original byte buffers as well.
        var normalized = files.Select(file =>
        {
            byte[] bytes;
            try
            {
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length == 0) throw new InvalidDataException("The image file is empty.");
                if (input.Length > MaxFileBytes) throw new InvalidDataException("Choose images smaller than 20 MB each.");
                bytes = new byte[checked((int)input.Length)];
                input.ReadExactly(bytes);
            }
            catch (Exception error) { ImageFailureContext.Mark(error, ImageFailureStage.ReadSource); throw; }
            return NormalizeBytes(bytes);
        }).ToList();
        return normalized.Select(image => StoreImage(image, store)).ToList();
    }

    private static NormalizedImage NormalizeBitmap(BitmapSource bitmap, int orientation)
    {
        try { return NormalizeBitmapCore(bitmap, orientation); }
        catch (Exception error) { ImageFailureContext.Mark(error, ImageFailureStage.Decode); throw; }
    }

    private static NormalizedImage NormalizeBitmapCore(BitmapSource bitmap, int orientation)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        ValidateSize(width, height);
        // WIC codecs expose indexed, CMYK, high-bit-depth and premultiplied surfaces. Materialize a conventional
        // pixel buffer while the input stream is open, then encode a fresh surface without source metadata or DPI.
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap :
            new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        if (orientation is >= 2 and <= 8)
        {
            // Let Windows Imaging Component perform the exact EXIF orthogonal transform.
            // A managed per-pixel loop copied every photo twice and kept the UI busy for large images.
            var matrix = orientation switch
            {
                2 => new Matrix(-1, 0, 0, 1, 0, 0),
                3 => new Matrix(-1, 0, 0, -1, 0, 0),
                4 => new Matrix(1, 0, 0, -1, 0, 0),
                5 => new Matrix(0, 1, 1, 0, 0, 0),
                6 => new Matrix(0, 1, -1, 0, 0, 0),
                7 => new Matrix(0, -1, -1, 0, 0, 0),
                _ => new Matrix(0, -1, 1, 0, 0, 0)
            };
            source = new TransformedBitmap(source, new MatrixTransform(matrix));
            width = source.PixelWidth;
            height = source.PixelHeight;
        }
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(pixels, stride, 0);
        var clean = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        clean.Freeze();
        BitmapSource candidate = clean;
        var scale = 1d;
        while (true)
        {
            var encoded = EncodePngWithinLimit(candidate);
            if (encoded is not null) return new NormalizedImage(encoded, candidate.PixelWidth, candidate.PixelHeight,
                CreatePreview(candidate));
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

    private static NoteImage StoreImage(NormalizedImage image, LibraryStore store)
    {
        try
        {
            var stored = store.ImportImage(image.Bytes, image.Width, image.Height);
            // ImportImage has already validated these immutable bytes and their destination. The UI
            // validates again when it requests the asset; avoid another full PNG CRC pass just to cache a thumbnail.
            var path = Path.Combine(store.AssetDirectory, stored.AssetName);
            CachePreview(PreviewFileKey(path, GalleryDecodeWidth), image.Preview);
            return stored;
        }
        catch (Exception error) { ImageFailureContext.Mark(error, ImageFailureStage.Store); throw; }
    }

    private static BitmapSource CreatePreview(BitmapSource source, int maxDimension = GalleryDecodeWidth)
    {
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var scale = Math.Min(1, maxDimension / (double)Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        // Detach from the full-resolution source, so the cache retains only thumbnail pixels.
        var stride = checked(scaled.PixelWidth * 4);
        var pixels = new byte[checked(stride * scaled.PixelHeight)];
        scaled.CopyPixels(pixels, stride, 0);
        var preview = BitmapSource.Create(scaled.PixelWidth, scaled.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        preview.Freeze();
        return preview;
    }

    private static PreviewKey PreviewFileKey(string path, int decodeWidth)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The image file is unavailable.", path);
        if (file.Length == 0 || file.Length > MaxFileBytes) throw new InvalidDataException("The image file is too large.");
        return new PreviewKey(file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks, decodeWidth);
    }

    private static void CachePreview(PreviewKey key, BitmapSource bitmap)
    {
        var pixels = (long)bitmap.PixelWidth * bitmap.PixelHeight;
        if (pixels > MaxCachedPixels) return;
        lock (PreviewGate)
        {
            if (Previews.ContainsKey(key)) return;
            while (PreviewOrder.First is { } oldest &&
                (_cachedPixels + pixels > MaxCachedPixels || Previews.Count >= MaxCachedPreviews))
            {
                _cachedPixels -= (long)oldest.Value.Bitmap.PixelWidth * oldest.Value.Bitmap.PixelHeight;
                Previews.Remove(oldest.Value.Key);
                PreviewOrder.RemoveFirst();
            }
            var node = PreviewOrder.AddLast(new CachedPreview(key, bitmap));
            Previews.Add(key, node);
            _cachedPixels += pixels;
        }
    }

    private static void ValidateFileLength(int length)
    {
        if (length == 0) throw new InvalidDataException("The image file is empty.");
        if (length > MaxFileBytes) throw new InvalidDataException("Choose an image smaller than 20 MB.");
    }

    private sealed record NormalizedImage(byte[] Bytes, int Width, int Height, BitmapSource Preview);
    private sealed record PreviewKey(string Path, long Length, long LastWriteTicks, int DecodeWidth);
    private sealed record CachedPreview(PreviewKey Key, BitmapSource Bitmap);

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
