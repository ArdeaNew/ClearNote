using System.Text;

namespace Folio.Core;

/// <summary>Tags are derived from visible text; reading them never modifies a saved note.</summary>
public static class NoteTags
{
    public const int MaxTagLength = 64;
    public const int MaxTags = 64;

    public static IReadOnlyList<string> Read(CardDocument card) => Read(NoteText.Read(card));

    public static IReadOnlyList<string> Read(NoteContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        NoteText.Validate(content);
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var paragraph in content.Paragraphs)
        {
            var text = string.Concat(paragraph.Runs.Select(run => run.Text));
            var runes = text.EnumerateRunes().ToArray();
            for (var index = 0; index < runes.Length; index++)
            {
                if (runes[index].Value != '#' || index > 0 && (IsTagRune(runes[index - 1]) || runes[index - 1].Value == '#')) continue;
                var end = index + 1;
                while (end < runes.Length && IsTagRune(runes[end])) end++;
                var length = end - index - 1;
                if (length is > 0 and <= MaxTagLength)
                {
                    var builder = new StringBuilder();
                    for (var part = index + 1; part < end; part++) builder.Append(runes[part].ToString());
                    var tag = builder.ToString();
                    if (seen.Add(tag)) tags.Add(tag);
                    if (tags.Count == MaxTags) return tags.AsReadOnly();
                }
                index = end - 1;
            }
        }
        return tags.AsReadOnly();
    }

    public static bool Matches(CardDocument card, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return true;
        var query = tag.Trim();
        if (query.StartsWith('#')) query = query[1..];
        return query.Length > 0 && Read(card).Contains(query, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsTagRune(Rune rune) => Rune.IsLetterOrDigit(rune) || rune.Value is '_' or '-';
}
