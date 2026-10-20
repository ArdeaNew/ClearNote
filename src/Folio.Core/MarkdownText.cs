using System.Globalization;
using System.Text;

namespace Folio.Core;

/// <summary>A bounded, non-recursive text shorthand parser. It never imports or executes content.</summary>
public static class MarkdownText
{
    public static NoteContent Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > NoteText.MaxTextLength)
            throw new InvalidDataException("The note exceeds the two million character limit.");
        var result = new NoteContent();
        char fence = '\0';
        var fenceLength = 0;
        var previousOrdered = false;
        var previousNumber = 0;
        var runCount = 0;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (result.Paragraphs.Count >= NoteText.MaxParagraphs)
                throw new InvalidDataException("The note exceeds the paragraph limit.");
            var paragraph = new NoteParagraph();
            var literal = fence != '\0' || line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t');
            if (TryFence(line, out var marker, out var length, out var tail))
            {
                literal = true;
                if (fence == '\0') { fence = marker; fenceLength = length; }
                else if (marker == fence && length >= fenceLength && string.IsNullOrWhiteSpace(tail)) fence = '\0';
            }
            var body = line;
            if (!literal)
            {
                var offset = 0;
                while (offset < line.Length && offset < 3 && line[offset] == ' ') offset++;
                var heading = offset;
                while (heading < line.Length && line[heading] == '#') heading++;
                if (heading > offset && heading - offset <= 6 && heading < line.Length && line[heading] == ' ')
                {
                    paragraph.Kind = "heading";
                    paragraph.HeadingLevel = heading - offset;
                    body = line[(heading + 1)..];
                }
                else if (offset + 1 < line.Length && line[offset] is '-' or '*' or '+' && line[offset + 1] == ' ')
                {
                    paragraph.Kind = "bullet";
                    body = line[(offset + 2)..];
                }
                else if (offset < line.Length && line[offset] == '>' && (offset + 1 == line.Length || line[offset + 1] == ' '))
                {
                    paragraph.Kind = "quote";
                    body = line[Math.Min(line.Length, offset + 2)..];
                }
                else
                {
                    var end = offset;
                    while (end < line.Length && char.IsAsciiDigit(line[end])) end++;
                    if (end > offset && end - offset <= 7 && end + 1 < line.Length && line[end] is '.' or ')' && line[end + 1] == ' ' &&
                        int.TryParse(line.AsSpan(offset, end - offset), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 1000000)
                    {
                        paragraph.Kind = "ordered";
                        paragraph.Number = previousOrdered ? checked(previousNumber + 1) : number;
                        if (paragraph.Number > 1000000) throw new InvalidDataException("The numbered list exceeds the supported number range.");
                        body = line[(end + 2)..];
                    }
                }
            }
            previousOrdered = paragraph.Kind == "ordered";
            previousNumber = paragraph.Number;
            paragraph.Runs = literal ? [new NoteRun { Text = line }] : Inline(body);
            runCount += paragraph.Runs.Count;
            if (runCount > NoteText.MaxRuns) throw new InvalidDataException("The note exceeds the text run limit.");
            result.Paragraphs.Add(paragraph);
        }
        NoteText.Validate(result);
        return result;
    }

    private static bool TryFence(string line, out char marker, out int length, out string tail)
    {
        var offset = 0;
        while (offset < line.Length && offset < 3 && line[offset] == ' ') offset++;
        marker = offset < line.Length ? line[offset] : '\0';
        var end = offset;
        if (marker is '`' or '~') while (end < line.Length && line[end] == marker) end++;
        length = end - offset;
        tail = line[end..];
        return length >= 3;
    }

    private sealed class Token(string text, char marker = '\0', int width = 0)
    {
        public string Text { get; } = text;
        public char Marker { get; } = marker;
        public int Width { get; } = width;
        public bool Matched { get; set; }
    }

    private static List<NoteRun> Inline(string line)
    {
        // Precompute balanced raw link spans so repeated brackets never cause rescanning.
        var brackets = Pairs(line, '[', ']');
        var parentheses = Pairs(line, '(', ')');
        var nextAngle = new int[line.Length];
        var angle = -1;
        for (var i = line.Length - 1; i >= 0; i--) { if (line[i] == '>') angle = i; nextAngle[i] = angle; }
        var tokens = new List<Token>();
        var stack = new Stack<int>();
        var buffer = new StringBuilder();
        void Flush() { if (buffer.Length != 0) { tokens.Add(new Token(buffer.ToString())); buffer.Clear(); } }
        void Delimiter(char marker, int width, bool canOpen, bool canClose)
        {
            Flush();
            var token = new Token(new string(marker, width), marker, width);
            var index = tokens.Count;
            tokens.Add(token);
            if (canClose && stack.TryPeek(out var opening) && tokens[opening].Marker == marker && tokens[opening].Width == width)
            {
                stack.Pop();
                tokens[opening].Matched = token.Matched = true;
            }
            else if (canOpen) stack.Push(index);
        }
        for (var i = 0; i < line.Length;)
        {
            if (tokens.Count > NoteText.MaxRuns * 2) return [new NoteRun { Text = line }];
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] is >= '!' and <= '~' && !char.IsAsciiLetterOrDigit(line[i + 1]))
            {
                buffer.Append(line[i + 1]); i += 2; continue;
            }
            var bracket = c == '!' && i + 1 < line.Length && line[i + 1] == '[' ? i + 1 : i;
            if (line[bracket] == '[' && brackets.TryGetValue(bracket, out var close) && close + 1 < line.Length && line[close + 1] == '(' &&
                parentheses.TryGetValue(close + 1, out var end))
            {
                buffer.Append(line, i, end + 1 - i); i = end + 1; continue;
            }
            if (c == '<' && nextAngle[i] >= 0)
            {
                var endAngle = nextAngle[i]; buffer.Append(line, i, endAngle + 1 - i); i = endAngle + 1; continue;
            }
            if (c is not ('*' or '_')) { buffer.Append(c); i++; continue; }
            var markerEnd = i + 1;
            while (markerEnd < line.Length && line[markerEnd] == c) markerEnd++;
            var count = markerEnd - i;
            if (count > 3 || c == '_' && count != 1 || c == '_' && i > 0 && markerEnd < line.Length && char.IsLetterOrDigit(line[i - 1]) && char.IsLetterOrDigit(line[markerEnd]))
            { buffer.Append(line, i, count); i = markerEnd; continue; }
            var canOpen = markerEnd < line.Length && !char.IsWhiteSpace(line[markerEnd]);
            var canClose = i > 0 && !char.IsWhiteSpace(line[i - 1]);
            if (count == 3)
            {
                var singleFirst = canClose && stack.TryPeek(out var opening) && tokens[opening].Marker == c && tokens[opening].Width == 1;
                Delimiter(c, singleFirst ? 1 : 2, canOpen, canClose);
                Delimiter(c, singleFirst ? 2 : 1, canOpen, canClose);
            }
            else Delimiter(c, count, canOpen, canClose);
            i = markerEnd;
        }
        Flush();
        var runs = new List<NoteRun>();
        var bold = false;
        var italic = false;
        void FlushRun()
        {
            if (buffer.Length == 0) return;
            runs.Add(new NoteRun { Text = buffer.ToString(), Bold = bold, Italic = italic });
            buffer.Clear();
        }
        foreach (var token in tokens)
        {
            if (!token.Matched) buffer.Append(token.Text);
            else
            {
                FlushRun();
                if (token.Width == 2) bold = !bold; else italic = !italic;
            }
        }
        FlushRun();
        if (runs.Count == 0) runs.Add(new NoteRun());
        return runs;
    }

    private static Dictionary<int, int> Pairs(string text, char opening, char closing)
    {
        var result = new Dictionary<int, int>();
        var stack = new Stack<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == opening) stack.Push(i);
            else if (text[i] == closing && stack.TryPop(out var start)) result[start] = i;
        }
        return result;
    }
}
