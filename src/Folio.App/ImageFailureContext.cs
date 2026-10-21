namespace Folio.App;

internal enum ImageFailureStage { ReadSource, Decode, Store, Clipboard }

/// <summary>Preserves exception types and causes while carrying the failing image-operation boundary.</summary>
internal static class ImageFailureContext
{
    private const string Key = "ClearNote.ImageFailureStage";

    internal static void Mark(Exception error, ImageFailureStage stage)
    {
        if (error is not OutOfMemoryException && Get(error) is null) error.Data[Key] = stage;
    }

    internal static ImageFailureStage? Get(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current.Data[Key] is ImageFailureStage stage) return stage;
        return null;
    }
}
