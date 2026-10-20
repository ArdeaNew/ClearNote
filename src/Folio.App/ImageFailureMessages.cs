using System.IO;
using System.Runtime.InteropServices;

namespace Folio.App;

internal static class ImageFailureMessages
{
    public static string Describe(Exception error)
    {
        if (error is UnauthorizedAccessException or System.Security.SecurityException)
            return "Cannot read the image or write to Log. Check folder permissions and retry.";
        if (error is PathTooLongException)
            return "The image path is too long. Move it to a shorter folder path and retry.";
        if (error is FileNotFoundException or DirectoryNotFoundException)
            return "The selected image is no longer available. Choose the file again.";
        if (error is IOException && (error.HResult & 0xffff) is 0x70 or 0x27)
            return "There is not enough disk space to save the image.";
        if (error is IOException && (error.HResult & 0xffff) is 0x20 or 0x21)
            return "The image file is in use. Close the app editing it, then retry.";
        if (error is InvalidDataException)
        {
            var message = error.Message;
            if (message.Contains("four images", StringComparison.OrdinalIgnoreCase))
                return "A note can contain four images. Remove one before adding another.";
            if (message.Contains("20 MB", StringComparison.OrdinalIgnoreCase))
                return "The image is too large. Choose a file under 20 MB.";
            if (message.Contains("megapixel", StringComparison.OrdinalIgnoreCase) || message.Contains("16,000", StringComparison.OrdinalIgnoreCase))
                return "The image is too large. Use one up to 40 megapixels and 16,000 pixels per side.";
            return "The image could not be read. Try exporting it as PNG or JPEG.";
        }
        if (error is NotSupportedException or FormatException or COMException or ArgumentException)
            return "This image cannot be decoded. Try exporting it as PNG or JPEG.";
        if (error is IOException)
            return "The image could not be saved. Check access to the image and the Log folder.";
        return "The image could not be added. Existing text and attachments have been preserved.";
    }
}
