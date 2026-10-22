using System.Text.Json.Serialization;

namespace Folio.Core;

/// <summary>Independent feeds; only Notes participate in spaced repetition.</summary>
public enum NoteCollection { Notes = 0, Memo = 1, Opinions = 2, EconomicAnalysis = 3 }

/// <summary>A5 portrait page, measured in device independent pixels at 96 DPI.</summary>
public static class PageSize
{
    public const double Width = 148d / 25.4 * 96;
    public const double Height = 210d / 25.4 * 96;
}

public sealed class CardDocument
{
    /// <summary>Absent in earlier libraries, which contain Notes only.</summary>
    public NoteCollection Collection { get; set; } = NoteCollection.Notes;
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    [JsonRequired] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    [JsonRequired] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    [JsonRequired] public List<NoteBlock> Blocks { get; set; } = [];
    /// <summary>Continuous text note. Null reads the preserved legacy blocks without modifying them.</summary>
    public NoteContent? Note { get; set; }
    [JsonRequired] public MemoryState Memory { get; set; } = new();
    [JsonRequired] public List<ReviewLog> Reviews { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NoteContent
{
    [JsonRequired] public List<NoteParagraph> Paragraphs { get; set; } = [];
    /// <summary>Imported pictures, in display order. Omitted by libraries written before schema 4.</summary>
    public List<NoteImage> Images { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NoteImage
{
    [JsonRequired] public string AssetName { get; set; } = "";
    [JsonRequired] public int PixelWidth { get; set; }
    [JsonRequired] public int PixelHeight { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NoteParagraph
{
    [JsonRequired] public string Kind { get; set; } = "body";
    public int HeadingLevel { get; set; } = 2;
    public int Number { get; set; } = 1;
    [JsonRequired] public List<NoteRun> Runs { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NoteRun
{
    [JsonRequired] public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public string? Foreground { get; set; }
    public string? Background { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ComposerDraft
{
    [JsonRequired] public string CardId { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public NoteContent Content { get; set; } = new();
}

public sealed class NoteBlock
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string Kind { get; set; } = "text";
    public string Text { get; set; } = "";
    public string AssetName { get; set; } = "";
    [JsonRequired] public double X { get; set; }
    [JsonRequired] public double Y { get; set; }
    [JsonRequired] public double Width { get; set; } = 220;
    [JsonRequired] public double Height { get; set; } = 70;
    public double FontSize { get; set; } = 20;
    public double Radius { get; set; } = 12;
    public bool Bold { get; set; }
}

public sealed class MemoryState
{
    [JsonRequired] public DateTimeOffset? LastReviewAt { get; set; }
    [JsonRequired] public DateTimeOffset DueAt { get; set; } = DateTimeOffset.Now;
    /// <summary>Days until estimated recall reaches 90 percent.</summary>
    [JsonRequired] public double Stability { get; set; }
    [JsonRequired] public double Difficulty { get; set; } = 5;
    [JsonRequired] public int Repetitions { get; set; }
    [JsonRequired] public int Lapses { get; set; }
}

public enum ReviewRating
{
    Again = 1,
    Hard = 2,
    Good = 3
}

public sealed class ReviewLog
{
    public bool Automatic { get; set; }
    [JsonRequired] public DateTimeOffset ReviewedAt { get; set; }
    [JsonRequired] public ReviewRating Rating { get; set; }
    [JsonRequired] public double ElapsedDays { get; set; }
    [JsonRequired] public double RecallBefore { get; set; }
    [JsonRequired] public double StabilityAfter { get; set; }
    [JsonRequired] public DateTimeOffset DueAt { get; set; }
}
