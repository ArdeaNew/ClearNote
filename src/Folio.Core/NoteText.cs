namespace Folio.Core;

/// <summary>Safe structured text and a read-only bridge from legacy canvas documents.</summary>
public static class NoteText
{
    public const int MaxParagraphs = 20000;
    public const int MaxRuns = 100000;
    public const int MaxTextLength = 2000000;

    public static NoteContent Read(CardDocument card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Note is not null) return Copy(card.Note);
        if (card.Blocks is null) throw new InvalidDataException("The legacy note is incomplete.");
        var note = new NoteContent();
        foreach (var block in card.Blocks.Where(block => block.Kind == "text").OrderBy(block => block.Y).ThenBy(block => block.X))
        {
            if (block.Text is null) throw new InvalidDataException("A legacy text element is incomplete.");
            foreach (var line in block.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                note.Paragraphs.Add(new NoteParagraph
                {
                    Kind = block.FontSize >= 26 ? "heading" : "body",
                    Runs = [new NoteRun { Text = line, Bold = block.Bold }]
                });
        }
        Validate(note);
        return note;
    }

    public static string PlainText(CardDocument card) => string.Join("\n", Read(card).Paragraphs
        .Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Text))));

    public static bool HasContent(CardDocument card) => HasContent(Read(card));

    public static bool HasContent(NoteContent content)
    {
        Validate(content);
        return content.Images.Count > 0 || content.Paragraphs
            .Any(paragraph => paragraph.Runs.Any(run => !string.IsNullOrWhiteSpace(run.Text)));
    }

    public static void SetContent(CardDocument card, NoteContent content)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(content);
        // Keep legacy blocks intact so original layout and assets remain recoverable.
        card.Note = Copy(content);
    }

    internal static void Validate(NoteContent content)
    {
        if (content.Images is null || content.Images.Count > NoteImages.MaxImages)
            throw new InvalidDataException("A note supports up to four images.");
        foreach (var image in content.Images) NoteImages.ValidateReference(image);
        if (content.Paragraphs is null || content.Paragraphs.Count > MaxParagraphs)
            throw new InvalidDataException("The note has missing paragraphs or exceeds the paragraph limit.");
        long characters = 0;
        long runs = 0;
        foreach (var paragraph in content.Paragraphs)
        {
            if (paragraph is null || paragraph.Kind is not ("body" or "heading" or "bullet" or "ordered" or "quote") || paragraph.Runs is null ||
                paragraph.HeadingLevel is < 1 or > 6 || paragraph.Number is < 1 or > 1000000)
                throw new InvalidDataException("A note paragraph has an unsupported kind or missing text runs.");
            runs += paragraph.Runs.Count;
            if (runs > MaxRuns) throw new InvalidDataException("The note exceeds the text run limit.");
            foreach (var run in paragraph.Runs)
            {
                if (run?.Text is null) throw new InvalidDataException("A note text run is incomplete.");
                if (!NoteColors.IsValid(run.Foreground) || !NoteColors.IsValid(run.Background))
                    throw new InvalidDataException("Text colors must use #RRGGBB, or automatic color.");
                characters += run.Text.Length;
                if (characters > MaxTextLength) throw new InvalidDataException("The note exceeds the two million character limit.");
            }
        }
    }

    internal static NoteContent Copy(NoteContent content)
    {
        Validate(content);
        return new NoteContent
        {
            Images = content.Images.Select(image => new NoteImage
            {
                AssetName = image.AssetName, PixelWidth = image.PixelWidth, PixelHeight = image.PixelHeight
            }).ToList(),
            Paragraphs = content.Paragraphs.Select(paragraph => new NoteParagraph
            {
                Kind = paragraph.Kind,
                HeadingLevel = paragraph.HeadingLevel,
                Number = paragraph.Number,
                Runs = paragraph.Runs.Select(run => new NoteRun
                {
                    Text = run.Text, Bold = run.Bold, Italic = run.Italic,
                    Foreground = NoteColors.Normalize(run.Foreground), Background = NoteColors.Normalize(run.Background)
                }).ToList()
            }).ToList()
        };
    }
}
