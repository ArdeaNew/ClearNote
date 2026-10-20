using System.Buffers.Binary;

namespace Folio.Core;

/// <summary>Limits and format checks for pictures imported into continuous notes.</summary>
public static class NoteImages
{
    public const int MaxImages = 4;
    public const int MaxImportBytes = 20 * 1024 * 1024;
    public const int MaxDimension = 16000;
    public const long MaxPixels = 40000000;
    private static readonly uint[] CrcTable = BuildCrcTable();

    internal static void ValidateReference(NoteImage? image)
    {
        if (image is null) throw new InvalidDataException("An image reference is missing.");
        ValidateDimensions(image.PixelWidth, image.PixelHeight);
        var name = image.AssetName;
        if (name is null || name.Length != 68 || name[64..] is not (".png" or ".jpg") ||
            name.AsSpan(0, 64).ContainsAnyExcept("0123456789abcdef"))
            throw new InvalidDataException("An image must reference an imported asset.");
    }

    internal static void ValidateDimensions(int width, int height)
    {
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension || (long)width * height > MaxPixels)
            throw new InvalidDataException("Images must be at most 16,000 pixels on each side and 40 million pixels in total.");
    }

    internal static string ValidateBytes(ReadOnlySpan<byte> bytes, int width, int height)
    {
        ValidateDimensions(width, height);
        if (bytes.Length is <= 0 or > MaxImportBytes)
            throw new InvalidDataException("An imported image must be no larger than 20 MB.");
        var (extension, actualWidth, actualHeight) = bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            ? ReadPng(bytes) : ReadJpeg(bytes);
        ValidateDimensions(actualWidth, actualHeight);
        if (actualWidth != width || actualHeight != height)
            throw new InvalidDataException("The image dimensions do not match its encoded header.");
        return extension;
    }

    private static (string, int, int) ReadPng(ReadOnlySpan<byte> bytes)
    {
        var offset = 8;
        var width = 0;
        var height = 0;
        var hasData = false;
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > int.MaxValue || length > bytes.Length - offset - 12)
                throw new InvalidDataException("The PNG image contains a truncated chunk.");
            var count = (int)length;
            var type = bytes.Slice(offset + 4, 4);
            if (type.ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8))
                throw new InvalidDataException("The PNG image contains an invalid chunk name.");
            var body = bytes.Slice(offset + 8, count);
            var expected = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + count, 4));
            uint crc = uint.MaxValue;
            foreach (var value in bytes.Slice(offset + 4, count + 4)) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
            if (~crc != expected) throw new InvalidDataException("The PNG image failed its chunk integrity check.");
            if (offset == 8)
            {
                if (!type.SequenceEqual("IHDR"u8) || count != 13)
                    throw new InvalidDataException("The PNG image is missing its header.");
                var w = BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
                var h = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
                if (w > int.MaxValue || h > int.MaxValue) throw new InvalidDataException("The PNG dimensions are too large.");
                width = (int)w; height = (int)h;
                var bitDepth = body[8]; var color = body[9];
                var validDepth = color switch
                {
                    0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => bitDepth is 8 or 16,
                    3 => bitDepth is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!validDepth || body[10] != 0 || body[11] != 0 || body[12] > 1)
                    throw new InvalidDataException("The PNG header has an unsupported encoding.");
            }
            else if (type.SequenceEqual("IHDR"u8)) throw new InvalidDataException("The PNG image has duplicate headers.");
            if (type.SequenceEqual("IDAT"u8) && count > 0) hasData = true;
            offset += count + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                if (count != 0 || !hasData || offset != bytes.Length)
                    throw new InvalidDataException("The PNG image is incomplete.");
                return ("png", width, height);
            }
        }
        throw new InvalidDataException("The PNG image is incomplete.");
    }

    private static (string, int, int) ReadJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216)
            throw new InvalidDataException("Imported images must use PNG or JPEG.");
        var offset = 2;
        var width = 0;
        var height = 0;
        var scanned = false;
        var inScan = false;
        while (offset < bytes.Length)
        {
            if (inScan)
            {
                while (offset < bytes.Length && bytes[offset] != 255) offset++;
                if (offset == bytes.Length) break;
            }
            else if (bytes[offset] != 255) throw new InvalidDataException("The JPEG image contains an invalid marker.");
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset == bytes.Length) break;
            var marker = bytes[offset++];
            if (inScan && (marker == 0 || marker is >= 0xD0 and <= 0xD7)) continue;
            inScan = false;
            if (marker == 0xD9)
            {
                if (!scanned || width == 0 || offset != bytes.Length) break;
                return ("jpg", width, height);
            }
            if (marker is 0 or 0xD8 or >= 0xD0 and <= 0xD7 || offset > bytes.Length - 2)
                throw new InvalidDataException("The JPEG image contains an invalid marker.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || length > bytes.Length - offset)
                throw new InvalidDataException("The JPEG image contains a truncated segment.");
            var body = bytes.Slice(offset + 2, length - 2);
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (width != 0 || body.Length < 6 || body[0] != 8 || body[5] is < 1 or > 4 || body.Length != 6 + 3 * body[5])
                    throw new InvalidDataException("The JPEG frame header is invalid.");
                height = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(3, 2));
            }
            else if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                throw new InvalidDataException("This JPEG frame encoding is unsupported.");
            if (marker == 0xDA)
            {
                if (width == 0 || body.Length < 4 || body[0] is < 1 or > 4 || body.Length != 4 + 2 * body[0])
                    throw new InvalidDataException("The JPEG scan header is invalid.");
                scanned = true; inScan = true;
            }
            offset += length;
        }
        throw new InvalidDataException("The JPEG image is incomplete.");
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
