using System.IO;
using Folio.App.Controls;
using Folio.Core;

namespace Folio.App;

/// <summary>Creates ordinary, locally stored notes once for a new library.</summary>
internal static class WelcomeNotes
{
    private const string LandscapeResource = "Folio.App.Assets.WelcomeLandscape";

    public static bool EnsureCreated(LibraryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.IsNewLibrary) return false;

        using var resource = typeof(WelcomeNotes).Assembly.GetManifestResourceStream(LandscapeResource)
            ?? throw new InvalidDataException("The welcome picture could not be loaded.");
        using var imageBytes = new MemoryStream();
        resource.CopyTo(imageBytes);
        var landscape = ImageAttachments.ImportBytes(imageBytes.ToArray(), store);
        var now = DateTimeOffset.Now;

        return store.InitializeNewLibrary([
            Create(now, new NoteContent
            {
                Paragraphs = [
                    Paragraph("Make room for what matters.", bold: true),
                    Paragraph("Capture in Markdown. Add a picture. Find it again with tags."),
                    Paragraph("Review brings ideas back through spaced repetition."),
                    Paragraph("Yours, offline. Your notes and images stay in Log.")
                ]
            }),
            Create(now.AddMilliseconds(-1), new NoteContent
            {
                Paragraphs = [Paragraph("Leave a little room for wonder.")],
                Images = [landscape]
            })
        ]);
    }

    private static CardDocument Create(DateTimeOffset createdAt, NoteContent content) => new()
    {
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        Note = content,
        Memory = new MemoryState { DueAt = createdAt }
    };

    private static NoteParagraph Paragraph(string text, bool bold = false) => new()
    {
        Runs = [new NoteRun { Text = text, Bold = bold }]
    };
}
