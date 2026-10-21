using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

internal static class Program
{
    private static string _root = "";
    private static int _count, _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        var dataRoot = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : Environment.GetEnvironmentVariable("FOLIO_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "Folio.Tests");
        _root = Path.Combine(Path.GetFullPath(dataRoot), "ui-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_root);
        var application = new App();
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (args.Contains("--render") || args.Contains("--render-home") || args.Contains("--render-features") || args.Contains("--render-guide") || args.Contains("--render-showcase") || args.Contains("--prepare-welcome"))
        {
            var outputIndex = Array.IndexOf(args, "--output");
            var output = outputIndex >= 0 && outputIndex + 1 < args.Length ? args[outputIndex + 1] : Path.Combine(Environment.CurrentDirectory, "outputs");
            try
            {
                if (args.Contains("--prepare-welcome"))
                {
                    var libraryIndex = Array.IndexOf(args, "--library");
                    if (libraryIndex < 0 || libraryIndex + 1 >= args.Length) throw new ArgumentException("Choose a new library folder.");
                    PreviewRenderer.PrepareWelcome(Path.GetFullPath(args[libraryIndex + 1]), Path.GetFullPath(output));
                }
                else if (args.Contains("--render-showcase"))
                {
                    var photosIndex = Array.IndexOf(args, "--photos-dir");
                    if (photosIndex < 0 || photosIndex + 1 >= args.Length) throw new ArgumentException("Choose a folder of example pictures.");
                    ShowcaseRenderer.Render(_root, Path.GetFullPath(args[photosIndex + 1]), Path.GetFullPath(output));
                }
                else if (args.Contains("--render-guide")) GuideRenderer.Render(_root, Path.GetFullPath(output));
                else if (args.Contains("--render-features")) PreviewRenderer.RenderFeatures(_root, Path.GetFullPath(output));
                else if (args.Contains("--render-home")) PreviewRenderer.RenderEmptyHome(_root, Path.GetFullPath(output));
                else PreviewRenderer.Render(_root, Path.GetFullPath(output));
                return 0;
            }
            finally { application.Shutdown(); }
        }
        Check("Continuous paragraphs preserve Unicode text and natural wrapping", ContinuousText);
        Check("Cross-paragraph bold, italic, heading and list formatting survives capture", ParagraphStyles);
        Check("Text editing supports undo and redo", UndoAndRedo);
        Check("Unsupported native formatting is blocked and native bullets are normalized", NativeCommandBoundary);
        Check("Long notes scroll without truncating their text", LongNotes);
        Check("Paste accepts plain text only and drag/drop is rejected", PasteAndDrop);
        Check("Review notes are read-only", ReadOnlyNote);
        Check("ClearNote uses a fixed reading column with English controls", ComposerLayout);
        Check("Fixed width and resizable height fit smaller work areas", A5StartupFit);
        Check("Window width changes in whole steps and preserves preferences and active content", () => WidthChecks.Run(_root));
        Check("Native window menus expose working state-specific commands and follow window lifetime", () => WindowMenuChecks.Run(_root));
        Check("Drafts survive restart and only posting enters the library", DraftAndPost);
        Check("Notes search finds full content without recording reviews", SearchNotes);
        Check("Review counts visible cards once per round and preserves later edits", ReviewFlow);
        Check("Markdown, colors and ordered list numbering survive reload", MarkdownAndColors);
        Check("Composite font maps CJK to FangSong and Latin to monospace", Typography);
        Check("Feed batch loading, viewport tracking and inline editing", FeedChecks.Run);
        Check("Committed content, tag filters and rejected search remain synchronized", () => InteractionChecks.Run(_root));
        Check("Markdown entry, native continuation and inline layout", EditorChecks.Run);
        Check("Images import, paste, persist and survive independent editing", () => MediaChecks.Run(_root));
        Check("Compact image galleries preserve proportions and navigate without trapping feed scroll", () => GalleryChecks.Run(_root));
        Check("Memo keeps its own feed and drafts while sharing editing, search and identity", () => MemoChecks.Run(_root));
        Check("Hashtags complete natively and filter Review without repeat counts", TagFiltering);
        Check("Image-only composer drafts reopen and post into the feed", ComposerImages);
        Check("Attachment failures explain recovery and log safely without losing drafts", () => ErrorHandlingChecks.Run(_root));
        Check("Image codec compatibility, EXIF and safe large-photo conversion", () => ImageCompatibilityChecks.Run(_root));
        Check("Readable JPEG and PNG source containers normalize into strict durable assets", () => ImageDecodeRobustnessChecks.Run(_root));
        Check("Clipboard alternatives and detached bitmap snapshots survive provider failures", () => ImagePasteRobustnessChecks.Run(_root));
        Check("Image failures identify their stage, preserve content and support retry", () => ImageFailureChecks.Run(_root));
        Check("Background image imports preserve sources and reuse bounded frozen previews", () => ImageImportChecks.Run(_root));
        Check("Async image imports preserve draft ownership, navigation and deferred close", () => ImageImportLifecycleChecks.Run(_root));
        Check("Local profiles, avatars and search preserve edits, history and source pictures", () => ProfileChecks.Run(_root));
        Check("Profile image imports preserve candidate ownership, responsive controls and deferred close", () => ProfileImportChecks.Run(_root));
        Check("Welcome notes are ordinary durable content and never return after deletion", () => WelcomeChecks.Run(_root));
        Console.WriteLine($"\n{_count - _failed}/{_count} passed. Synthetic UI data: {_root}");
        application.Shutdown();
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Action run)
    {
        _count++;
        try { run(); Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + "\n" + (error is TargetInvocationException { InnerException: { } inner } ? inner : error));
        }
    }

    private static LibraryStore Store(string name)
    {
        var store = new LibraryStore(Path.Combine(_root, name, "Data"));
        store.Load();
        return store;
    }

    private static CardDocument Card(string title = "Synthetic interface test", string text = "A synthetic learning note")
    {
        var card = new CardDocument { Title = title, CreatedAt = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero) };
        NoteText.SetContent(card, new NoteContent { Paragraphs = text.Split('\n').Select(line => new NoteParagraph { Runs = [new NoteRun { Text = line }] }).ToList() });
        return card;
    }

    private static NoteEditor Editor(string text = "")
    {
        var editor = new NoteEditor();
        editor.Load(Card(text: text), true);
        Layout(editor, 520, 360);
        return editor;
    }

    private static void ContinuousText()
    {
        var editor = Editor();
        var changes = 0;
        editor.Changed += (_, _) => changes++;
        const string unicode = "\u95f4\u9694\u590d\u4e60 supports Unicode notes.";
        editor.PlainText = "First paragraph\nSecond paragraph\n" + unicode;
        var captured = editor.Capture();
        Assert(captured.Paragraphs.Count >= 3, "Entering line breaks must create continuous paragraphs.");
        Assert(editor.PlainText.Contains("First paragraph") && editor.PlainText.Contains(unicode), "All entered text, including CJK content, must survive capture.");
        Assert(Descendants(editor).OfType<RichTextBox>().Count() == 1, "There must be one continuous editor, not independent text blocks.");
        Assert(editor.TextBox.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && editor.TextBox.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "Notes must wrap to the available width and scroll vertically.");
        Assert(changes > 0, "Typing must notify the save layer.");
        Assert(editor.Background is SolidColorBrush background && background.Color.A == 0,
            "The text surface must let the containing white or hovered region show through.");
    }

    private static void ParagraphStyles()
    {
        var editor = Editor("Alpha memory\nBeta practice\nGamma recall");
        var formattingChanges = 0;
        editor.Changed += (_, _) => formattingChanges++;
        SelectParagraphs(editor, 0, 1);
        editor.ToggleBold();
        editor.ToggleItalic();
        var captured = editor.Capture();
        Assert(captured.Paragraphs.Take(2).All(p => p.Runs.Where(r => r.Text.Length > 0).All(r => r.Bold && r.Italic)), "Bold and italic must apply across both selected paragraphs.");
        Assert(captured.Paragraphs[2].Runs.All(r => !r.Bold && !r.Italic), "Formatting must not leak into an unselected paragraph.");
        editor.ToggleHeading();
        Assert(editor.Capture().Paragraphs.Take(2).All(p => p.Kind == "heading"), "Heading must apply across the selected paragraphs.");
        editor.ToggleBullets();
        Assert(editor.Capture().Paragraphs.Take(2).All(p => p.Kind == "bullet"), "Bullets must apply across the selected paragraphs.");
        editor.ToggleHeading();
        captured = editor.Capture();
        Assert(captured.Paragraphs.Take(2).All(p => p.Kind == "heading"), "Heading must safely convert selected list paragraphs.");
        var reloaded = Editor();
        var card = Card();
        NoteText.SetContent(card, captured);
        reloaded.Load(card, true);
        Assert(JsonSerializer.Serialize(reloaded.Capture()) == JsonSerializer.Serialize(captured), "Structured paragraph and run styles must survive editor reload.");
        Assert(formattingChanges > 0, "Formatting-only changes must notify autosave.");
    }

    private static void UndoAndRedo()
    {
        var editor = Editor("Original note");
        editor.PlainText = "Changed note\nAnother paragraph";
        Assert(editor.CanUndo, "Text edits must enter the undo history.");
        editor.Undo();
        Assert(editor.PlainText.Trim() == "Original note" && editor.CanRedo, "Undo must restore the previous text.");
        editor.Redo();
        Assert(editor.PlainText.Contains("Changed note") && editor.PlainText.Contains("Another paragraph"), "Redo must restore the continuous edit.");
        SelectParagraphs(editor, 0, 0);
        editor.ToggleBold();
        Assert(editor.Capture().Paragraphs[0].Runs.All(r => r.Bold), "Formatting must modify the selected text.");
        editor.Undo();
        Assert(editor.Capture().Paragraphs[0].Runs.All(r => !r.Bold), "Formatting must also support undo.");
        var heading = Editor("First paragraph\nSecond paragraph");
        SelectParagraphs(heading, 0, 0);
        heading.ToggleHeading();
        Assert(heading.Capture().Paragraphs[0].Kind == "heading", "Heading must be captured after applying the style.");
        heading.Undo();
        Assert(heading.Capture().Paragraphs[0].Kind == "body", "Undoing a heading must restore body kind in saved content.");
        heading.Redo();
        Assert(heading.Capture().Paragraphs[0].Kind == "heading", "Redoing a heading must restore heading kind in saved content.");
    }

    private static void LongNotes()
    {
        var text = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"Paragraph {i:000}: a complete thought remains available while the note grows."));
        var editor = Editor(text);
        Layout(editor, 420, 280);
        Assert(editor.Capture().Paragraphs.Count == 300 && editor.PlainText.Contains("Paragraph 300"), "Long notes must retain their final paragraph.");
        Assert(editor.TextBox.ExtentHeight > editor.TextBox.ViewportHeight && editor.TextBox.ViewportHeight > 0, "A long note must have a scrollable vertical extent.");
        editor.TextBox.ScrollToEnd();
        editor.UpdateLayout();
        Assert(editor.TextBox.VerticalOffset > 0, "The final paragraphs must be reachable by scrolling.");
    }

    private static void NativeCommandBoundary()
    {
        var editor = Editor("A plain paragraph");
        editor.TextBox.SelectAll();
        var before = JsonSerializer.Serialize(editor.Capture());
        var size = editor.TextBox.Selection.GetPropertyValue(TextElement.FontSizeProperty);
        var paragraph = Paragraphs(editor.TextBox.Document.Blocks).Single();
        var margin = paragraph.Margin;
        var commands = new[] { EditingCommands.ToggleUnderline, EditingCommands.AlignCenter, EditingCommands.AlignRight,
            EditingCommands.AlignJustify, EditingCommands.IncreaseFontSize, EditingCommands.IncreaseIndentation };
        foreach (var command in commands)
        {
            Assert(!command.CanExecute(null, editor.TextBox), command.Name + " must not be offered.");
            command.Execute(null, editor.TextBox);
            var decorations = editor.TextBox.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            Assert(decorations is null || decorations.Count == 0, "Unsupported commands must not introduce underline decoration.");
            Assert(Equals(editor.TextBox.Selection.GetPropertyValue(TextElement.FontSizeProperty), size), "Unsupported commands must not change native run font sizes.");
            Assert(paragraph.TextAlignment == TextAlignment.Left && paragraph.Margin == margin && paragraph.Parent is FlowDocument, "Unsupported alignment, numbering or indentation must not change the native document.");
            Assert(JsonSerializer.Serialize(editor.Capture()) == before, "Unsupported commands must leave persisted content unchanged.");
        }

        var bullets = Editor("First heading\nSecond heading");
        SelectParagraphs(bullets, 0, 1);
        bullets.ToggleHeading();
        EditingCommands.ToggleBullets.Execute(null, bullets.TextBox);
        Assert(bullets.Capture().Paragraphs.All(p => p.Kind == "bullet"), "Native bullet commands must use the supported list representation.");
        Assert(Paragraphs(bullets.TextBox.Document.Blocks).All(p => p.FontSize == 16 && p.FontWeight == FontWeights.Normal), "Native bullets must normalize former heading paragraphs to body styling.");
        bullets.Undo();
        Assert(bullets.Capture().Paragraphs.All(p => p.Kind == "heading"), "Undoing native bullets must restore heading paragraphs.");
        bullets.Redo();
        Assert(bullets.Capture().Paragraphs.All(p => p.Kind == "bullet"), "Redoing native bullets must restore supported list paragraphs.");
        EditingCommands.ToggleBullets.Execute(null, bullets.TextBox);
        Assert(bullets.Capture().Paragraphs.All(p => p.Kind == "body"), "Toggling native bullets off must restore body paragraphs.");
    }

    private static void PasteAndDrop()
    {
        var editor = Editor("Keep this text");
        var before = editor.PlainText;
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new byte[] { 255 }, 1);
        var rejected = new[]
        {
            new DataObject(DataFormats.Bitmap, bitmap),
            new DataObject(DataFormats.FileDrop, new[] { Path.Combine(_root, "not-an-import.png") }),
            new DataObject(DataFormats.Html, "<p>Rich text</p><img src='file:///not-an-import.png'>"),
            new DataObject(DataFormats.Rtf, "{\\rtf1 Rich text {\\pict\\pngblip 00}}")
        };
        foreach (var data in rejected)
        {
            Assert(!(bool)Invoke(editor, "PasteTextData", data)!, "Rich or file-only data must be rejected.");
            Assert(editor.PlainText == before, "Rejected paste data must not change the note.");
        }
        var mixed = new DataObject();
        mixed.SetData(DataFormats.UnicodeText, "Plain content\nA second line");
        mixed.SetData(DataFormats.Html, "<b>Do not import formatting</b><img src='blocked'>");
        editor.TextBox.SelectAll();
        Assert((bool)Invoke(editor, "PasteTextData", mixed)!, "An explicit plain-text format must be accepted.");
        Assert(editor.PlainText.Contains("Plain content") && !editor.PlainText.Contains("<img"), "Only the explicit plain-text representation must be inserted.");
        Assert(editor.Capture().Paragraphs.SelectMany(p => p.Runs).All(r => !r.Bold && !r.Italic), "Pasted text must not import rich formatting.");
        Assert(!editor.TextBox.AllowDrop, "The editor must disable drag/drop.");
        var drag = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [rejected[0], DragDropKeyStates.None, DragDropEffects.Copy, editor.TextBox, new Point(1, 1)], CultureInfo.InvariantCulture)!;
        drag.RoutedEvent = DragDrop.PreviewDropEvent;
        editor.TextBox.RaiseEvent(drag);
        Assert(drag.Handled && drag.Effects == DragDropEffects.None, "The drop boundary must explicitly consume and reject payloads.");
    }

    private static void ReadOnlyNote()
    {
        var editor = Editor();
        editor.Load(Card(text: "Review this text"), false);
        var before = JsonSerializer.Serialize(editor.Capture());
        editor.TextBox.SelectAll();
        editor.ToggleBold(); editor.ToggleItalic(); editor.ToggleHeading(); editor.ToggleBullets();
        editor.PlainText = "Must not replace review text";
        Assert(editor.TextBox.IsReadOnly && !editor.CanUndo && !editor.CanRedo, "Review must expose no editing history or writable body.");
        Assert(!(bool)Invoke(editor, "PasteTextData", new DataObject(DataFormats.UnicodeText, "Blocked"))!, "Review must reject plain-text paste too.");
        Assert(JsonSerializer.Serialize(editor.Capture()) == before, "Review formatting and paste attempts must not modify content.");
    }

    private static void SelectParagraphs(NoteEditor editor, int first, int last)
    {
        var paragraphs = Paragraphs(editor.TextBox.Document.Blocks).ToArray();
        editor.TextBox.Selection.Select(paragraphs[first].ContentStart, paragraphs[last].ContentEnd);
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            else if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var child in Paragraphs(item.Blocks)) yield return child;
        }
    }

    private static void ComposerLayout()
    {
        var store = Store("composer-layout");
        var window = new MainWindow(store);
        try
        {
            Layout(window);
            Assert(window.Title == "ClearNote" && window.Width == 678 && window.MinWidth == window.MaxWidth && window.MinWidth == window.Width, "ClearNote must hold a fixed reading column enlarged by ten percent.");
            Assert(Get<NoteEditor>(window, "Composer").TextBox.FontSize == 16, "Body text must shrink by two DIPs.");
            Assert(Equals(Get<Button>(window, "PostButton").Content, "Post"), "Posting must use a quiet text button.");
            Assert(Get<Grid>(window, "Root").RowDefinitions.Last().Height == new GridLength(12) &&
                Get<FeedView>(window, "NotesFeed").Margin.Left == 12 && Get<FeedView>(window, "NotesFeed").Margin.Right == 12,
                "The bottom gutter must match the twelve-DIP side gutters.");
            var edges = Get<Border>(window, "ColumnEdges");
            var root = Get<Grid>(window, "Root");
            Assert(Grid.GetRowSpan(edges) == root.RowDefinitions.Count &&
                Math.Abs(edges.ActualHeight - root.ActualHeight) < 0.01,
                "Reading-column borders must continue through the bottom gutter.");
            Assert(Get<Grid>(window, "Root").Background is SolidColorBrush { Color: var white } && white == Colors.White &&
                Get<FeedView>(window, "NotesFeed").Background is SolidColorBrush { Color: var feedWhite } && feedWhite == Colors.White,
                "The page and feed backgrounds must be pure white.");
            Assert(Get<Button>(window, "ReviewButton").IsEnabled, "Empty review must remain clickable.");
            Assert(Get<NoteEditor>(window, "Composer").TransformToAncestor(Get<Grid>(window, "Root")).Transform(new Point()).Y < Get<FeedView>(window, "NotesFeed").TransformToAncestor(Get<Grid>(window, "Root")).Transform(new Point()).Y, "Composer must stay above feed.");
            var menu = OpenReviewMenu(window);
            Assert(menu.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(new[] { "All tags" }), "Empty Review must still offer All tags in a popup.");
            Assert(!Descendants(Get<Grid>(window, "Root")).OfType<ComboBox>().Any(), "The reading column must not contain a permanent tag dropdown.");
            menu.IsOpen = false;
            SelectReview(window, "All tags");
            Assert(Get<FeedView>(window, "NotesFeed").RenderedCards.Count == 0 && store.Cards.Count == 0, "Empty review must not create content.");
            Assert(!Get<Button>(window, "PostButton").IsEnabled && Get<TextBlock>(window, "ErrorStatus").Visibility == Visibility.Collapsed, "Empty composer is silent.");
            var labels = Descendants(Get<Grid>(window, "Root")).OfType<Button>().Select(b => b.Content?.ToString()).ToList();
            Assert(!labels.Any(label => label is "Hide note" or "Skip" or "Again" or "Hard" or "Good"), "Obsolete review controls must be removed.");
            Assert(Contrast(Get<NoteEditor>(window, "Composer").TextBox.Foreground) >= 4.5 &&
                Contrast(Get<Button>(window, "NotesButton").Foreground) >= 4.5 &&
                Contrast(Get<Button>(window, "ReviewButton").Foreground) >= 4.5 &&
                Contrast(menu.Items.OfType<MenuItem>().First().Foreground) >= 4.5,
                "Default note, navigation, and Review menu text must have at least 4.5:1 contrast on white.");
            Assert(Get<Button>(window, "SearchButton").Content is Canvas && !Get<System.Windows.Controls.Primitives.Popup>(window, "SearchPopup").IsOpen,
                "Search must use an accessible icon and a closed popup without a permanent text field.");
            AssertEnglish(Get<Grid>(window, "Root"));
        }
        finally { window.Close(); }
    }

    private static void A5StartupFit()
    {
        var window = new MainWindow(Store("fit"));
        try
        {
            Invoke(window, "FitStartupToWorkArea", new Rect(0, 0, 800, 600));
            Assert(window.Width <= 776 && window.Height <= 576, "Window must fit a smaller screen.");
            Assert(window.Width == 678 && window.MinWidth == window.MaxWidth, "Fitting a shorter screen must retain the wider fixed reading column.");
            Invoke(window, "FitStartupToWorkArea", new Rect(0, 0, 520, 500));
            Assert(window.Width == 496 && window.MinWidth == 496 && window.MaxWidth == 496, "A screen narrower than the column must get a fitting fixed width.");
        }
        finally { window.Close(); }
    }

    private static void DraftAndPost()
    {
        var store = Store("draft-post");
        var first = new MainWindow(store);
        Get<NoteEditor>(first, "Composer").InsertMarkdown("## A draft\n3. Recall\n4. Revisit");
        Assert((bool)Invoke(first, "SaveDraft")!, "Draft must save.");
        Assert(store.Cards.Count == 0 && store.LoadDraft() != null, "Draft must not become a card before posting.");
        first.Close();
        var reopened = new LibraryStore(store.RootPath);
        reopened.Load();
        var window = new MainWindow(reopened);
        try
        {
            var composer = Get<NoteEditor>(window, "Composer");
            Assert(composer.PlainText.Contains("A draft"), "Restart must recover unsent content.");
            Invoke(window, "PostNote");
            Assert(reopened.Cards.Count == 1 && reopened.LoadDraft() == null && string.IsNullOrWhiteSpace(composer.PlainText), "Posting must save once and clear the draft/composer.");
            Assert(reopened.Cards[0].Title == "" && reopened.Cards[0].Note!.Paragraphs.Any(p => p.Kind == "ordered"), "New notes use their body directly, with structured numbering.");
            Invoke(window, "PostNote");
            Assert(reopened.Cards.Count == 1, "Empty repeat submission must not create a duplicate.");
            Assert(Get<FeedView>(window, "NotesFeed").RenderedCards.Count == 1, "Posted note must immediately enter Notes feed.");
        }
        finally { window.Close(); }
    }

    private static void SearchNotes()
    {
        var store = Store("search-notes");
        store.Upsert(Card("", "A spaced retrieval example"));
        store.Upsert(Card("Old title", "Unrelated supporting content"));
        var window = new MainWindow(store);
        try
        {
            Get<TextBox>(window, "SearchBox").Text = "RETRIEVAL";
            Assert(Get<FeedView>(window, "NotesFeed").RenderedCards.Count == 1, "Search must find body text ignoring case.");
            Get<TextBox>(window, "SearchBox").Text = "old title";
            Assert(Get<FeedView>(window, "NotesFeed").RenderedCards.Count == 1, "Legacy titles remain searchable.");
            Get<TextBox>(window, "SearchBox").Clear();
            Layout(window);
            Get<FeedView>(window, "NotesFeed").CheckVisibleCards();
            Assert(store.Cards.All(card => card.Reviews.Count == 0), "Ordinary Notes browsing must not modify memory history.");
        }
        finally { window.Close(); }
    }

    private static void ReviewFlow()
    {
        var store = Store("review-flow");
        foreach (var index in Enumerable.Range(1, 24)) store.Upsert(Card("", $"Note {index}\nA deliberate review.\nA second paragraph."));
        var window = new MainWindow(store);
        try
        {
            var notesBefore = Get<FeedView>(window, "NotesFeed").RenderedCards.ToArray();
            var menu = OpenReviewMenu(window);
            Layout(window);
            Get<FeedView>(window, "NotesFeed").CheckVisibleCards();
            Assert(store.Cards.All(card => card.Reviews.Count == 0) && Get<FeedView>(window, "NotesFeed").RenderedCards.SequenceEqual(notesBefore),
                "Opening Review alone must not enter review, change history, or rebuild the Notes feed.");
            menu.IsOpen = false;
            SelectReview(window, "All tags");
            Layout(window);
            var feed = Get<FeedView>(window, "NotesFeed");
            feed.CheckVisibleCards();
            var viewed = store.Cards.Where(card => card.Reviews.Count > 0).ToList();
            Assert(viewed.Count > 0 && viewed.Count < feed.RenderedCards.Count, "Only visible cards count; preloaded cards do not.");
            Assert(viewed.All(card => card.Reviews.Count == 1 && card.Reviews[0].Automatic), "A visible card counts as one automatic review.");
            var total = store.Cards.Sum(card => card.Reviews.Count);
            feed.CheckVisibleCards();
            Invoke(window, "RefreshFeed");
            Layout(window);
            feed.CheckVisibleCards();
            Assert(store.Cards.All(card => card.Reviews.Count <= 1), "Refreshing and returning within a round must not count a card twice.");
            var first = feed.RenderedCards[0];
            var memory = JsonSerializer.Serialize(store.Cards.Single(card => card.Id == first.Card.Id).Memory);
            first.BeginEdit();
            first.Body.PlainText = "Edited after review";
            Assert(first.TrySave(), "Inline edit must save.");
            Assert(JsonSerializer.Serialize(store.Cards.Single(card => card.Id == first.Card.Id).Memory) == memory, "An edit must preserve the latest stored review state.");
            Invoke(window, "Notes_Click", window, new RoutedEventArgs());
            SelectReview(window, "All tags");
            Layout(window);
            feed.CheckVisibleCards();
            Assert(store.Cards.Sum(card => card.Reviews.Count) > total, "Returning from Notes starts another review round.");
        }
        finally { window.Close(); }
    }

    private static void MarkdownAndColors()
    {
        var editor = Editor();
        editor.InsertMarkdown("# One idea\n3. **Recall** first\n9. *Check* the gap\n> A useful question");
        var captured = editor.Capture();
        Assert(captured.Paragraphs.Any(p => p.Kind == "heading" && p.HeadingLevel == 1), "Markdown heading level must survive.");
        var ordered = captured.Paragraphs.Where(p => p.Kind == "ordered").ToList();
        Assert(ordered.Count == 2 && ordered[0].Number == 3 && ordered[1].Number == 4, "Ordered list continues from its first number.");
        editor.TextBox.SelectAll();
        editor.SetForeground("#326ba6");
        editor.SetBackground("#fff2b3");
        captured = editor.Capture();
        Assert(captured.Paragraphs.SelectMany(p => p.Runs).Where(r => r.Text.Length > 0).All(r => r.Foreground == "#326BA6" && r.Background == "#FFF2B3"), "Colors apply to selected text and normalize hex.");
        var card = Card();
        NoteText.SetContent(card, captured);
        var reloaded = Editor();
        reloaded.Load(card, true);
        Assert(JsonSerializer.Serialize(reloaded.Capture()) == JsonSerializer.Serialize(captured), "All list and color styles must roundtrip.");
        reloaded.TextBox.SelectAll();
        reloaded.SetBackground(null);
        Assert(reloaded.Capture().Paragraphs.SelectMany(p => p.Runs).All(r => r.Background == null), "Highlight removal must restore no background.");
    }

    private static void Typography()
    {
        Assert(AppTypography.Text.FamilyMaps[0].Target == "FangSong" && AppTypography.Text.FamilyMaps[^1].Target == "Consolas", "CJK and Latin font mappings must be explicit.");
        var editor = Editor("English and 中文");
        Assert(ReferenceEquals(editor.TextBox.FontFamily, AppTypography.Text), "Editor must use the same composite font.");
    }

    private static void TagFiltering()
    {
        var editor = Editor("A thought #lea");
        editor.TextBox.CaretPosition = editor.TextBox.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        using var suggestions = new TagSuggestions(editor, () => ["Learning", "Design"]);
        Assert(suggestions.Complete("Learning") && editor.PlainText.Contains("#Learning "), "A tag completion must preserve the typed sentence and insert a single tag.");
        editor.Undo();
        Assert(editor.PlainText.Contains("#lea"), "Tag completion must undo in a single native edit.");
        var store = Store("tags-review");
        store.Upsert(Card("", "#Learning Retrieval matters."));
        store.Upsert(Card("", "#Design Give the thought room."));
        store.Upsert(Card("", "A note without a tag."));
        var window = new MainWindow(store);
        try
        {
            var feed = Get<FeedView>(window, "NotesFeed");
            var pending = feed.RenderedCards.Single(card => NoteTags.Matches(card.Card, "Design"));
            pending.BeginEdit();
            pending.Body.PlainText = "#Design Edited without leaving Notes.";
            var save = feed.SaveNote;
            feed.SaveNote = (_, _) => false;
            var menu = OpenReviewMenu(window);
            Assert(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "#Learning")), "Review must offer the current library tags in its popup.");
            Assert(pending.IsEditing && store.Cards.Single(card => card.Id == pending.Card.Id).Note!.Paragraphs.SelectMany(paragraph => paragraph.Runs).All(run => !run.Text.Contains("Edited")),
                "Opening Review must not save an active edit.");
            menu.IsOpen = false;
            SelectReview(window, "#Learning");
            Assert(pending.IsEditing && !Field<bool>(window, "_reviewMode") && Field<string?>(window, "_reviewTag") == null && feed.RenderedCards.Count == 3,
                "A failed pending edit must preserve the Notes mode, filter and editor when a Review tag is selected.");
            feed.SaveNote = save;
            SelectReview(window, "#Learning");
            Assert(!pending.IsEditing && NoteText.PlainText(store.Cards.Single(card => card.Id == pending.Card.Id)).Contains("Edited"), "A successful Review selection must save the pending edit first.");
            Assert(feed.RenderedCards.Count == 1 && NoteTags.Matches(feed.RenderedCards[0].Card, "learning"), "Review must include only the selected tag.");
            Layout(window);
            feed.CheckVisibleCards();
            Assert(store.Cards.Single(card => NoteTags.Matches(card, "Learning")).Reviews.Count == 1, "Only the visible tagged note should count.");
            Assert(store.Cards.Where(card => !NoteTags.Matches(card, "Learning")).All(card => card.Reviews.Count == 0), "Filtered-out notes must not count as seen.");
            var reviewEdit = feed.RenderedCards[0];
            reviewEdit.BeginEdit();
            reviewEdit.Body.PlainText = "#Learning A pending review edit.";
            feed.SaveNote = (_, _) => false;
            SelectReview(window, "#Design");
            Assert(reviewEdit.IsEditing && ReferenceEquals(feed.RenderedCards.Single(), reviewEdit) && Field<bool>(window, "_reviewMode") && Field<string?>(window, "_reviewTag") == "Learning",
                "A failed edit must keep the selected Review tag, note and editor intact.");
            feed.SaveNote = save;
            SelectReview(window, "#Design");
            SelectReview(window, "#Learning");
            Layout(window);
            feed.CheckVisibleCards();
            Assert(store.Cards.Single(card => NoteTags.Matches(card, "Learning")).Reviews.Count == 1, "Changing tags must not restart the current review round.");
            var reviewCards = feed.RenderedCards.ToArray();
            var reviewCount = store.Cards.Sum(card => card.Reviews.Count);
            menu = OpenReviewMenu(window);
            menu.IsOpen = false;
            Assert(feed.RenderedCards.SequenceEqual(reviewCards) && store.Cards.Sum(card => card.Reviews.Count) == reviewCount,
                "Cancelling the tag popup must preserve the current review and its history.");
            Invoke(window, "Notes_Click", window, new RoutedEventArgs());
            Assert(feed.RenderedCards.Count == 3 && !Descendants(Get<Grid>(window, "Root")).OfType<ComboBox>().Any(), "Notes remains the complete library without an inline filter.");
        }
        finally { window.Close(); }
    }

    private static void ComposerImages()
    {
        var store = Store("composer-images");
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 80, 120, 140, 255, 80, 120, 140, 255, 80, 120, 140, 255, 80, 120, 140, 255 }, 8);
        var image = ImageAttachments.ImportBitmap(bitmap, store);
        var first = new MainWindow(store);
        Field<AttachmentPanel>(first, "_composerMedia").AddImages([image]);
        Assert(Get<Button>(first, "PostButton").IsEnabled, "An image-only note must be postable.");
        Assert((bool)Invoke(first, "SaveDraft")!, "Image-only draft must save.");
        first.Close();
        var reopened = new LibraryStore(store.RootPath);
        reopened.Load();
        var window = new MainWindow(reopened);
        try
        {
            Assert(Field<AttachmentPanel>(window, "_composerMedia").Count == 1, "Image draft must survive restarting the window.");
            Invoke(window, "PostNote");
            Assert(reopened.Cards.Count == 1 && reopened.Cards[0].Note!.Images.Count == 1 && reopened.LoadDraft() == null, "Posting must preserve the image and clear the draft.");
            Assert(Get<FeedView>(window, "NotesFeed").RenderedCards[0].Attachments?.Count == 1, "Image must appear in the feed.");
            Assert(!Field<AttachmentPanel>(window, "_composerMedia").HasImages, "Posted images must leave the composer.");
        }
        finally { window.Close(); }
    }

    private static T Get<T>(MainWindow window, string name) where T : class => (T)(window.FindName(name) ?? throw new Exception("Missing UI field " + name));
    private static ContextMenu OpenReviewMenu(MainWindow window)
    {
        var button = Get<Button>(window, "ReviewButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        var menu = button.ContextMenu ?? throw new Exception("Review must create a popup menu.");
        Assert(menu.IsOpen && ReferenceEquals(menu.PlacementTarget, button), "Review must open its menu beside the clicked button.");
        return menu;
    }
    private static void SelectReview(MainWindow window, string label)
    {
        var menu = OpenReviewMenu(window);
        try
        {
            var item = menu.Items.OfType<MenuItem>().Single(value => Equals(value.Header, label));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        }
        finally { menu.IsOpen = false; }
    }
    private static double Contrast(Brush brush)
    {
        Assert(brush is SolidColorBrush { Color.A: 255 }, "Readability checks require opaque foreground colors.");
        var color = ((SolidColorBrush)brush).Color;
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 1.05 / (0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B) + 0.05);
    }
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("Missing field " + name)).GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] parameters) =>
        (target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("Missing method " + name)).Invoke(target, parameters);

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void Layout(MainWindow window)
    {
        var root = Get<FrameworkElement>(window, "Root");
        root.Width = 662; root.Height = 754;
        root.Measure(new Size(662, 754));
        root.Arrange(new Rect(0, 0, 662, 754));
        root.UpdateLayout();
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Width = width; element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static bool ContainsCjk(string text) => text.Any(c => c is >= '\u2e80' and <= '\u9fff' or >= '\uf900' and <= '\ufaff');
    private static void AssertEnglish(DependencyObject root)
    {
        foreach (var item in Descendants(root).OfType<FrameworkElement>().Where(x => x.Visibility == Visibility.Visible))
        {
            var text = item switch { TextBlock block => block.Text, TextBox box => box.Text, _ => "" };
            Assert(!ContainsCjk(text), "Visible interface text must not contain CJK characters: " + text);
            Assert(!ContainsCjk(System.Windows.Automation.AutomationProperties.GetName(item)), "Accessible interface labels must be English.");
            if (item.ToolTip is string tip) Assert(!ContainsCjk(tip), "Tooltips must be English.");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Escape(MainWindow window)
    {
        var source = new InMemoryPresentationSource(Get<FrameworkElement>(window, "Root"));
        Invoke(window, "Window_KeyDown", window, new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private static bool VisibleInContent(FrameworkElement element, FrameworkElement root)
    {
        for (DependencyObject? item = element; item is not null; item = VisualTreeHelper.GetParent(item))
        {
            if (item is UIElement visual && visual.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(item, root)) return true;
        }
        return false;
    }

    private sealed class InMemoryPresentationSource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}


