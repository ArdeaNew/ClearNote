using System.IO;

namespace Folio.App;

/// <summary>Identifies attachment failures at the shared paste boundary, retaining the original cause.</summary>
internal sealed class ImageInputException : IOException
{
    public ImageInputException(Exception error) : base("The attached image could not be imported.", error)
    {
        HResult = error.HResult;
    }
}
