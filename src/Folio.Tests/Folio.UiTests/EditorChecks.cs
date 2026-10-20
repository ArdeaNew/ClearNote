using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Offscreen checks: no windows are shown and the system clipboard is never used.</summary>
public static class EditorChecks
{
    public static void Run()
    {
        Typography();
        HeadingLevelsAndUndo();
        PrefixTyping();
        TypedOrderedNumbers();
        InlineTyping();
        NumberingAndEnter();
        MarkdownPaste();
        RejectedInput();
        TextColors();
        InlineLayout();
        ReadOnlyBoundary();
    }

    private static NoteEditor Editor(string text = "")
    {
        var editor = new NoteEditor();
        editor.Load(Card(new NoteContent
        {
            Paragraphs = text.Split('\n').Select(line => new NoteParagraph { Runs = [new NoteRun { Text = line }] }).ToList()
        }), true);
        editor.Measure(new Size(440, 300));
        editor.Arrange(new Rect(0, 0, 440, 300));
        editor.UpdateLayout();
        End(editor);
        return editor;
    }

    private static CardDocument Card(NoteContent note) => new() { Note = note };
    private static void End(NoteEditor editor) => editor.TextBox.CaretPosition = editor.TextBox.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)!;
    private static NoteParagraph First(NoteEditor editor) => editor.Capture().Paragraphs.First();
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void Typography()
    {
        Assert(AppTypography.Text.FamilyMaps.Count == 2, "Typography must explicitly map CJK and Latin fonts.");
        Assert(AppTypography.Text.FamilyMaps[0].Target == "FangSong" && AppTypography.Text.FamilyMaps[0].Unicode.Contains("4E00-9FFF", StringComparison.OrdinalIgnoreCase), "Chinese characters must use installed FangSong.");
        Assert(AppTypography.Text.FamilyMaps[1].Target == "Consolas", "Latin text must use Consolas.");
        var editor = Editor("English \u4e2d\u6587");
        Assert(ReferenceEquals(editor.TextBox.FontFamily, AppTypography.Text) && ReferenceEquals(editor.TextBox.Document.FontFamily, AppTypography.Text), "Editor and document must use the same composite font.");
        Assert(editor.TextBox.Foreground is SolidColorBrush { Color: var color } && color == Color.FromRgb(15, 20, 25), "Automatic note text must use the high-contrast ink color.");
    }

    private static void TypedOrderedNumbers()
    {
        foreach (var (prefix, number) in new[] { ("1", 1), ("12", 12), ("1000000", 1000000), ("7.", 7), ("7)", 7) })
        {
            var editor = Editor();
            TypeText(editor, prefix);
            Assert(PreviewText(editor, " "), "A typed number followed by space at the paragraph start must consume the space.");
            Assert(First(editor).Kind == "ordered" && First(editor).Number == number && editor.PlainText == "", "Number-space conversion must create a native list with the entered starting number.");
            editor.Undo();
            Assert(editor.PlainText == prefix && First(editor).Kind == "body", "One Undo must restore the literal number before autoformat.");
            editor.Redo();
            Assert(First(editor).Kind == "ordered" && First(editor).Number == number && editor.PlainText == "", "Redo must restore the native list and its starting number.");
        }

        foreach (var prefix in new[] { "Version 12", "12.5", "2026-10-20", "0", "01", "-12", "1000001", "999999999", "\uFF11\uFF12", "1\uFF10" })
        {
            var editor = Editor(prefix);
            TypeText(editor, " ");
            Assert(First(editor).Kind == "body" && editor.PlainText == prefix + " ", "Ordinary text, unsupported ordinals and non-ASCII digits must keep their typed space.");
        }

        var pasted = Editor();
        var data = new DataObject(DataFormats.UnicodeText, "12 first\n13 second");
        var paste = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
        pasted.TextBox.RaiseEvent(paste);
        Assert(paste.CommandCancelled && pasted.PlainText == "12 first\n13 second" && pasted.Capture().Paragraphs.All(paragraph => paragraph.Kind == "body"), "Pasted bare numeric lines must remain literal text.");

        var continued = Editor();
        TypeText(continued, "12 first");
        Assert(First(continued).Kind == "ordered" && First(continued).Number == 12 && continued.PlainText == "first", "Typing number-space and content must leave only the item content in storage.");
        Assert(PreviewEnter(continued), "Enter after a number-space item must be handled.");
        Assert(continued.Capture().Paragraphs.Last() is { Kind: "ordered", Number: 13 }, "Number-space lists must continue from the entered ordinal.");
        TypeText(continued, "42 people");
        Assert(continued.Capture().Paragraphs.Last().Runs.Any(run => run.Text == "42 people"), "Bare numbers inside an existing item must remain item content.");
        Assert(PreviewEnter(continued) && PreviewEnter(continued), "An empty continued item must support Enter to leave the list.");
        Assert(continued.Capture().Paragraphs.Last().Kind == "body", "Enter on an empty item must return to body text.");
        continued.Undo();
        Assert(continued.Capture().Paragraphs.Last() is { Kind: "ordered", Number: 14 }, "Undo must restore the empty continued list item after leaving it.");
        var card = new CardDocument();
        NoteText.SetContent(card, continued.Capture());
        var reloaded = Editor();
        reloaded.Load(card, true);
        Assert(reloaded.Capture().Paragraphs.Select(paragraph => (paragraph.Kind, paragraph.Number)).SequenceEqual(continued.Capture().Paragraphs.Select(paragraph => (paragraph.Kind, paragraph.Number))), "Typed numbering and continuation must survive storage and reload.");
    }

    private static void HeadingLevelsAndUndo()
    {
        var note = new NoteContent
        {
            Paragraphs = Enumerable.Range(1, 6).Select(level => new NoteParagraph
            {
                Kind = "heading", HeadingLevel = level, Runs = [new NoteRun { Text = "Heading " + level }]
            }).ToList()
        };
        var editor = Editor();
        editor.Load(Card(note), true);
        Assert(editor.Capture().Paragraphs.Select(paragraph => paragraph.HeadingLevel).SequenceEqual(Enumerable.Range(1, 6)), "All six heading levels must survive Build/Capture.");
        editor = Editor("Body");
        editor.TextBox.SelectAll();
        editor.SetHeading(6);
        Assert(First(editor).Kind == "heading" && First(editor).HeadingLevel == 6, "H6 must be a heading even when its size equals body size.");
        editor.Undo();
        Assert(First(editor).Kind == "body", "Heading Undo must restore persisted body kind.");
        editor.Redo();
        Assert(First(editor).HeadingLevel == 6 && First(editor).Kind == "heading", "Heading Redo must restore persisted heading level.");
        editor.ToggleQuote();
        Assert(First(editor).Kind == "quote", "Quote must replace heading style.");
        editor.Undo();
        Assert(First(editor).Kind == "heading" && First(editor).HeadingLevel == 6, "Quote Undo must restore the previous heading.");
    }

    private static void PrefixTyping()
    {
        for (var level = 1; level <= 6; level++)
        {
            var editor = Editor(new string('#', level));
            Assert(PreviewText(editor, " "), "Heading prefix space must be consumed.");
            Assert(First(editor).Kind == "heading" && First(editor).HeadingLevel == level && editor.PlainText == "", "Heading prefix must become native heading style.");
            editor.Undo();
            Assert(editor.PlainText == new string('#', level) && First(editor).Kind == "body", "One Undo must restore the heading prefix.");
            editor.Redo();
            Assert(First(editor).Kind == "heading" && First(editor).HeadingLevel == level, "Redo must restore typed heading style.");
        }
        foreach (var prefix in new[] { "-", "*", "+" })
        {
            var editor = Editor(prefix);
            Assert(PreviewText(editor, " ") && First(editor).Kind == "bullet", "Each supported bullet prefix must become a native list.");
        }
        foreach (var prefix in new[] { "3.", "3)" })
        {
            var editor = Editor(prefix);
            Assert(PreviewText(editor, " ") && First(editor).Kind == "ordered" && First(editor).Number == 3, "Both ordered prefixes must preserve the starting number.");
            editor.Undo();
            Assert(editor.PlainText == prefix && First(editor).Kind == "body", "Ordered prefix conversion must be one undo transaction.");
        }
        var quote = Editor(">");
        Assert(PreviewText(quote, " ") && First(quote).Kind == "quote", "Quote prefix must become native quote styling.");
        var literal = Editor("```");
        Assert(!PreviewText(literal, " ") && literal.PlainText == "```", "Fences must stay literal while typing.");
        foreach (var prefix in new[] { "1\uFF10.", "1000001.", "999999999." })
        {
            var editor = Editor(prefix);
            Assert(!PreviewText(editor, " ") && editor.PlainText == prefix && First(editor).Kind == "body", "Invalid or non-ASCII numeric prefixes must remain literal without throwing.");
        }
    }

    private static void InlineTyping()
    {
        foreach (var (prefix, final, expected, bold) in new[]
        {
            ("**bold*", "*", "bold", true), ("*italic", "*", "italic", false), ("_italic", "_", "italic", false)
        })
        {
            var editor = Editor(prefix);
            Assert(PreviewText(editor, final), "A completed emphasis delimiter must be consumed.");
            Assert(editor.PlainText == expected && First(editor).Runs.All(run => bold ? run.Bold : run.Italic), "Inline delimiters must become native emphasis.");
            Assert(editor.TextBox.Selection.IsEmpty && new TextRange(editor.TextBox.CaretPosition.Paragraph!.ContentStart, editor.TextBox.CaretPosition).Text == expected, "Emphasis conversion must leave the caret after the formatted text.");
            editor.Undo();
            Assert(editor.PlainText == prefix && First(editor).Runs.All(run => !run.Bold && !run.Italic), "One Undo must restore the preconversion typing state.");
            editor.Redo();
            Assert(editor.PlainText == expected && First(editor).Runs.All(run => bold ? run.Bold : run.Italic), "Redo must restore native emphasis and text.");
        }
        var incomplete = Editor("**bold");
        Assert(!PreviewText(incomplete, "*"), "The first closing star of bold must not become italic.");
        var escaped = Editor(@"\*literal");
        Assert(!PreviewText(escaped, "*"), "An escaped opening delimiter must stay literal.");
        foreach (var numbered in new[] { false, true })
        {
            var list = Editor("**bold*");
            if (numbered) list.ToggleNumbering(); else list.ToggleBullets();
            End(list);
            Assert(PreviewText(list, "*") && list.PlainText == "bold" && First(list).Runs.All(run => run.Bold), "Inline conversion inside a native list must exclude marker text from pointer offsets.");
            Assert(First(list).Kind == (numbered ? "ordered" : "bullet"), "Inline Markdown must preserve its native list kind.");
            list.Undo();
            Assert(list.PlainText == "**bold*" && First(list).Kind == (numbered ? "ordered" : "bullet"), "Undo of inline Markdown in a list must preserve the list and delimiters.");
        }
    }

    private static void NumberingAndEnter()
    {
        var editor = Editor("first\nsecond");
        editor.TextBox.SelectAll();
        editor.SetHeading(3);
        EditingCommands.ToggleNumbering.Execute(null, editor.TextBox);
        Assert(editor.Capture().Paragraphs.All(paragraph => paragraph.Kind == "ordered"), "Native numbering must use the supported normalized method.");
        Assert(AllParagraphs(editor.TextBox.Document.Blocks).All(paragraph => paragraph.FontSize == 16 && paragraph.FontWeight == FontWeights.Normal), "Numbering must reset former heading styling.");
        editor.Undo();
        Assert(editor.Capture().Paragraphs.All(paragraph => paragraph.Kind == "heading" && paragraph.HeadingLevel == 3), "One Undo must restore heading styles before native numbering.");
        editor.Redo();
        End(editor);
        EditingCommands.EnterParagraphBreak.Execute(null, editor.TextBox);
        Assert(editor.Capture().Paragraphs.Last().Kind == "ordered" && editor.Capture().Paragraphs.Last().Number == 3, "Native Enter must continue a nonempty ordered list.");
        Assert(editor.TextBox.CaretPosition.Paragraph == AllParagraphs(editor.TextBox.Document.Blocks).Last() && editor.Capture().Paragraphs.Last().Runs.All(run => string.IsNullOrWhiteSpace(run.Text)), "Enter must move the caret into the newly created empty item.");
        Assert(PreviewEnter(editor), "Enter on a blank list item must be consumed.");
        Assert(editor.Capture().Paragraphs.Last().Kind == "body", "Blank Enter must exit the list. Actual: " + JsonSerializer.Serialize(editor.Capture()));
        editor.Undo();
        Assert(editor.Capture().Paragraphs.Last().Kind == "ordered", "Undo must restore the blank list item.");
        var bullets = Editor("item");
        bullets.ToggleBullets();
        End(bullets);
        EditingCommands.EnterParagraphBreak.Execute(null, bullets.TextBox);
        Assert(bullets.Capture().Paragraphs.Last().Kind == "bullet", "Native Enter must continue a nonempty bullet list.");
        var limit = Editor();
        limit.InsertMarkdown("1000000. final item");
        End(limit);
        EditingCommands.EnterParagraphBreak.Execute(null, limit.TextBox);
        Assert(limit.Capture().Paragraphs.Last().Kind == "body" && limit.Capture().Paragraphs.First().Number == 1000000, "Enter at the maximum ordinal must create a saveable body paragraph.");
        NoteText.SetContent(new CardDocument(), limit.Capture());
        limit.Undo();
        Assert(limit.Capture().Paragraphs.Count == 1 && First(limit).Number == 1000000, "Ordinal-limit continuation must undo in one action.");
    }

    private static void MarkdownPaste()
    {
        var single = Editor("replace me");
        single.TextBox.SelectAll();
        single.InsertMarkdown("# Heading");
        Assert(First(single).Kind == "heading", "A heading replacing selected text must have native heading style: " + string.Join("/", AllParagraphs(single.TextBox.Document.Blocks).Select(paragraph => paragraph.FontSize + "-" + paragraph.FontWeight)));
        var editor = Editor("replace me");
        editor.TextBox.SelectAll();
        editor.InsertMarkdown("# Heading\n3. first **bold**\n4. second _italic_\n> quote\n```\n# literal\n- literal\n```\n![image](ignored.png)");
        var content = editor.Capture();
        Assert(content.Paragraphs.Count == 9 && content.Paragraphs[0].Kind == "heading" && content.Paragraphs[0].HeadingLevel == 1, "Markdown paste must create native paragraphs without extra blocks. Actual: " + JsonSerializer.Serialize(content));
        Assert(content.Paragraphs[1].Kind == "ordered" && content.Paragraphs[1].Number == 3 && content.Paragraphs[2].Number == 4, "Markdown paste must preserve ordered starting numbers.");
        Assert(content.Paragraphs[1].Runs.Any(run => run.Text == "bold" && run.Bold) && content.Paragraphs[2].Runs.Any(run => run.Text == "italic" && run.Italic), "Markdown paste must create native inline emphasis.");
        Assert(content.Paragraphs[3].Kind == "quote" && content.Paragraphs.Skip(4).All(paragraph => paragraph.Kind == "body"), "Fenced text and image syntax must stay text-only body content.");
        Assert(editor.PlainText.Contains("```\n# literal\n- literal\n```", StringComparison.Ordinal) && editor.PlainText.EndsWith("![image](ignored.png)", StringComparison.Ordinal), "Fences and image syntax must remain literal.");
        editor.Undo();
        Assert(editor.PlainText == "replace me", "A complete Markdown paste must undo in one action.");
        editor.Redo();
        Assert(editor.Capture().Paragraphs[1].Number == 3, "Redo must restore Markdown list numbering.");
        var before = editor.PlainText;
        var paste = typeof(NoteEditor).GetMethod("PasteTextData", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert(!(bool)paste.Invoke(editor, [new DataObject(DataFormats.Html, "<b>never imported</b>")])!, "HTML-only data must never be imported.");
        Assert(editor.PlainText == before, "Rejected rich paste must not change content.");
    }

    private static void TextColors()
    {
        var editor = Editor("alpha beta");
        var paragraph = AllParagraphs(editor.TextBox.Document.Blocks).First();
        var run = paragraph.Inlines.OfType<Run>().First();
        editor.TextBox.Selection.Select(run.ContentStart, run.ContentStart.GetPositionAtOffset(5)!);
        editor.SetForeground("#aabbcc");
        editor.SetBackground("#FFF2A8");
        var captured = First(editor);
        Assert(captured.Runs.First().Text == "alpha" && captured.Runs.First().Foreground == "#AABBCC" && captured.Runs.First().Background == "#FFF2A8", "Selected text must preserve foreground and highlight colors.");
        Assert(captured.Runs.Skip(1).All(item => item.Foreground is null && item.Background is null), "Text colors must not change the rest of the note.");
        Assert(editor.TextBox.Document.Background is null || editor.TextBox.Document.Background == Brushes.Transparent, "Highlight must not color the page background.");
        editor.Undo();
        Assert(First(editor).Runs.First().Background is null && First(editor).Runs.First().Foreground == "#AABBCC", "Highlight Undo must preserve the preceding text color change.");
        editor.Undo();
        Assert(First(editor).Runs.All(item => item.Foreground is null && item.Background is null), "Text color Undo must restore automatic text.");
        editor.Redo(); editor.Redo();
        var card = Card(editor.Capture());
        var reloaded = Editor();
        reloaded.Load(card, false);
        Assert(JsonSerializer.Serialize(reloaded.Capture()) == JsonSerializer.Serialize(editor.Capture()), "Native colors must round trip through storage.");
        var automatic = Editor("color");
        automatic.TextBox.SelectAll();
        automatic.SetForeground("#232323");
        Assert(First(automatic).Runs.First().Foreground == "#232323", "An explicit existing text color must remain distinct from the stronger automatic ink color.");
        automatic.SetForeground("#0F1419");
        Assert(First(automatic).Runs.First().Foreground is null or "#0F1419", "The automatic RGB color must retain its appearance when WPF canonicalizes inherited formatting.");
        automatic.SetForeground(null);
        Assert(First(automatic).Runs.First().Foreground is null, "Clearing text color must restore the automatic color.");
    }

    private static void RejectedInput()
    {
        var editor = Editor("Keep this selection intact");
        var paragraph = AllParagraphs(editor.TextBox.Document.Blocks).Single();
        var run = paragraph.Inlines.OfType<Run>().Single();
        editor.TextBox.Selection.Select(run.ContentStart.GetPositionAtOffset(5)!, run.ContentStart.GetPositionAtOffset(9)!);
        var before = JsonSerializer.Serialize(editor.Capture());
        var selection = editor.TextBox.Selection.Text;
        var offset = editor.TextBox.Document.ContentStart.GetOffsetToPosition(editor.TextBox.Selection.Start);
        var rejected = 0;
        var changes = 0;
        editor.InputRejected += _ => rejected++;
        editor.Changed += (_, _) => changes++;
        var paste = typeof(NoteEditor).GetMethod("PasteTextData", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var invalid in new[] { "1000000. x\n1. y", new string('x', NoteText.MaxTextLength + 1) })
        {
            var data = new DataObject(DataFormats.UnicodeText, invalid);
            Assert(!(bool)paste.Invoke(editor, [data])!, "Invalid plain-text Markdown paste must be rejected.");
            Assert(JsonSerializer.Serialize(editor.Capture()) == before && editor.TextBox.Selection.Text == selection && editor.TextBox.Document.ContentStart.GetOffsetToPosition(editor.TextBox.Selection.Start) == offset, "Rejected paste must preserve text and the exact selection.");
            var args = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
            editor.TextBox.RaiseEvent(args);
            Assert(args.CommandCancelled, "The actual paste boundary must cancel an invalid clipboard payload.");
        }
        editor.InsertMarkdown("1000000. x\n1. y");
        Assert(rejected == 5 && changes == 0 && JsonSerializer.Serialize(editor.Capture()) == before && editor.TextBox.Selection.Text == selection, "Rejected public and routed paste attempts must report the error without mutation.");
    }

    private static void InlineLayout()
    {
        var editor = Editor(string.Join('\n', Enumerable.Range(1, 40).Select(index => $"Paragraph {index}: complete text remains visible in the feed.")));
        editor.SetInlineLayout(true);
        var outer = new ScrollViewer { Width = 440, Height = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = editor };
        outer.Measure(new Size(440, 240));
        outer.Arrange(new Rect(0, 0, 440, 240));
        outer.UpdateLayout();
        Assert(editor.TextBox.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && editor.TextBox.Document.PagePadding == new Thickness(0), "Inline notes must have no internal scroll or canvas padding.");
        Assert(editor.ActualHeight > 800 && outer.ExtentHeight > outer.ViewportHeight, "A long inline note must use natural height inside the outer feed scroller.");
        Assert(editor.PlainText.Contains("Paragraph 40", StringComparison.Ordinal), "Natural layout must not truncate stored text.");
        outer.Content = null;
        editor.SetInlineLayout(false);
        Assert(editor.TextBox.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "Composer mode must restore internal scrolling.");
    }

    private static void ReadOnlyBoundary()
    {
        var editor = Editor();
        editor.InsertMarkdown("## Heading\n3. ordered\n> quote\nText **bold**");
        var card = Card(editor.Capture());
        editor.Load(card, false);
        var before = JsonSerializer.Serialize(editor.Capture());
        var changes = 0;
        editor.Changed += (_, _) => changes++;
        editor.TextBox.SelectAll();
        editor.ToggleBold(); editor.ToggleItalic(); editor.ToggleHeading(); editor.SetHeading(1);
        editor.ToggleBullets(); editor.ToggleNumbering(); editor.ToggleQuote();
        editor.SetForeground("#FF0000"); editor.SetBackground("#FFFF00");
        editor.PlainText = "blocked"; editor.InsertMarkdown("# blocked"); editor.Undo(); editor.Redo();
        EditingCommands.ToggleNumbering.Execute(null, editor.TextBox);
        editor.SetInlineLayout(true);
        Assert(editor.TextBox.IsReadOnly && !editor.CanUndo && !editor.CanRedo, "Read-only notes must have no writable text or history.");
        Assert(JsonSerializer.Serialize(editor.Capture()) == before && changes == 0, "Every exposed formatting/paste API must leave browsing content unchanged.");
    }

    private static bool PreviewText(NoteEditor editor, string text)
    {
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, editor.TextBox, text))
        { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
        editor.TextBox.RaiseEvent(args);
        return args.Handled;
    }

    private static void TypeText(NoteEditor editor, string text)
    {
        foreach (var character in text)
        {
            if (PreviewText(editor, character.ToString())) continue;
            editor.TextBox.Selection.Text = character.ToString();
            editor.TextBox.Selection.Select(editor.TextBox.Selection.End, editor.TextBox.Selection.End);
        }
    }

    private static bool PreviewEnter(NoteEditor editor)
    {
        var source = new OffscreenSource(editor);
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        editor.TextBox.RaiseEvent(args);
        return args.Handled;
    }

    private static IEnumerable<Paragraph> AllParagraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            else if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var child in AllParagraphs(item.Blocks)) yield return child;
        }
    }

    private sealed class OffscreenSource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
