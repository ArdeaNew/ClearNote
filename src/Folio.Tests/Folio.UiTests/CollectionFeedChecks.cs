using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

internal static class CollectionFeedChecks
{
    private static readonly (NoteCollection Collection, string Button, string Tag)[] Streams =
    [
        (NoteCollection.Notes, "NotesButton", "learningtag"),
        (NoteCollection.Memo, "MemoButton", "memotag"),
        (NoteCollection.Opinions, "OpinionsButton", "opiniontag"),
        (NoteCollection.EconomicAnalysis, "EconomicAnalysisButton", "analysistag")
    ];

    public static void Run(string root)
    {
        NavigationLayout(root);
        IndependentFeeds(root);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            foreach (var stream in Streams.Skip(2))
            {
                PendingImport(root, stream.Collection, stream.Button, inline: false);
                PendingImport(root, stream.Collection, stream.Button, inline: true);
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void NavigationLayout(string root)
    {
        var window = new MainWindow(Store(root, "navigation"));
        try
        {
            var names = new[] { "NotesButton", "ReviewButton", "MemoButton", "OpinionsButton", "EconomicAnalysisButton" };
            var labels = new[] { "Notes", "Review", "Memo", "Opinions", "Analysis" };
            Assert(names.Select(name => Get<Button>(window, name).Content?.ToString()).SequenceEqual(labels),
                "The navigation must name the two new streams and retain Notes, Review and Memo in order.");
            foreach (var width in new[] { 662d, 544d })
            {
                Layout(window, width);
                var bounds = names.Select(name => Bounds(window, Get<Button>(window, name))).ToArray();
                var search = Bounds(window, Get<Button>(window, "SearchButton"));
                var more = Bounds(window, Get<Button>(window, "MoreButton"));
                Assert(bounds.All(rect => rect.Width > 0 && rect.Left >= 0 && rect.Right <= width) &&
                    bounds.Zip(bounds.Skip(1)).All(pair => pair.First.Right <= pair.Second.Left + 0.5 && Math.Abs(pair.First.Top - pair.Second.Top) < 0.5) &&
                    bounds[^1].Right <= search.Left + 0.5 && search.Right <= more.Left + 0.5 && more.Right <= width,
                    "Default and minimum window widths must show all five full tabs before Search and More without overlap.");
                Assert(Get<ScrollViewer>(window, "CollectionNavigation").ScrollableWidth < 1,
                    "Ordinary supported widths must not need horizontal navigation scrolling.");
                foreach (var name in names)
                {
                    var button = Get<Button>(window, name);
                    var shell = (Border)button.Template.FindName("Shell", button);
                    var content = (ContentPresenter)shell.Child;
                    var origin = content.TranslatePoint(new Point(), shell);
                    Assert(Math.Abs(origin.X + content.ActualWidth / 2 - shell.ActualWidth / 2) <= 1 &&
                        Math.Abs(origin.Y + content.ActualHeight / 2 - shell.ActualHeight / 2) <= 1,
                        "Navigation text must remain centered within its hover background on both axes.");
                }
            }
            Layout(window, 360);
            var navigation = Get<ScrollViewer>(window, "CollectionNavigation");
            Assert(navigation.ScrollableWidth > 0 && Bounds(window, Get<Button>(window, "MoreButton")).Right <= 360,
                "An unusually narrow startup work area must retain reachable overflow and pinned header actions.");
            navigation.ScrollToHorizontalOffset(navigation.ScrollableWidth);
            Layout(window, 360);
            var analysis = Bounds(window, Get<Button>(window, "EconomicAnalysisButton"));
            Assert(analysis.Left >= 0 && analysis.Right <= Bounds(window, Get<Button>(window, "SearchButton")).Left + 0.5,
                "Scrolling the narrow tab strip must reveal the complete Analysis label without covering Search.");
        }
        finally { window.Close(); }
    }

    private static void IndependentFeeds(string root)
    {
        var store = Store(root, "independent");
        var seeds = Streams.ToDictionary(stream => stream.Collection,
            stream => Card("Seed " + stream.Collection + " #" + stream.Tag, stream.Collection));
        foreach (var card in seeds.Values) store.Upsert(card);
        var image = Picture(store);
        var window = new MainWindow(store);
        var draftText = Streams.ToDictionary(stream => stream.Collection, stream => "Unsent " + stream.Collection);
        try
        {
            var composer = Get<NoteEditor>(window, "Composer");
            var media = Field<AttachmentPanel>(window, "_composerMedia");
            var feed = Get<FeedView>(window, "NotesFeed");
            foreach (var stream in Streams)
            {
                Click(window, stream.Button);
                Assert(Collection(window) == stream.Collection && feed.RenderedCards.Single().Card.Id == seeds[stream.Collection].Id,
                    "Each stream must display only its own saved posts.");
                composer.PlainText = draftText[stream.Collection];
                if (IsNew(stream.Collection)) media.AddImages([image]);
            }
            var draftIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stream in Streams)
            {
                Click(window, stream.Button);
                Assert(composer.PlainText.Trim() == draftText[stream.Collection] && media.Count == (IsNew(stream.Collection) ? 1 : 0),
                    "Switching streams must restore the matching text and image draft without leaking another composer.");
                Assert((bool)Invoke(window, "SaveDraft")!, "Every independent composer must use the normal durable draft path.");
                Assert(draftIds.Add(store.LoadDraft(stream.Collection)!.CardId), "All four unsent drafts must have distinct identities.");
            }
            foreach (var stream in Streams.Skip(2))
            {
                Click(window, stream.Button);
                Invoke(window, "PostNote");
                Assert(store.Cards.Count(card => card.Collection == stream.Collection) == 2 &&
                    store.LoadDraft(stream.Collection) is null && feed.RenderedCards.All(row => row.Card.Collection == stream.Collection),
                    "Posting in a new stream must commit only that stream and clear only its own draft.");
                Assert(store.LoadDraft(NoteCollection.Notes) is not null && store.LoadDraft(NoteCollection.Memo) is not null,
                    "Posting Opinions or Analysis must retain the Notes and Memo drafts.");
                draftText[stream.Collection] = "After post " + stream.Collection;
                composer.PlainText = draftText[stream.Collection];
                media.AddImages([image]);
            }
            Assert(store.Cards.Count == 6, "The two new posts must not duplicate or move an existing record.");

            Click(window, "OpinionsButton");
            var row = feed.RenderedCards.Single(card => card.Card.Id == seeds[NoteCollection.Opinions].Id);
            row.BeginEdit();
            row.Body.PlainText = "Edited viewpoint #opiniontag";
            Click(window, "EconomicAnalysisButton");
            Assert(NoteText.PlainText(store.Cards.Single(card => card.Id == seeds[NoteCollection.Opinions].Id)).Contains("Edited viewpoint"),
                "Leaving Opinions must finish its accepted inline edit through the shared save path.");
            row = feed.RenderedCards.Single(card => card.Card.Id == seeds[NoteCollection.EconomicAnalysis].Id);
            row.BeginEdit();
            row.Body.PlainText = "Uncommitted analysis";
            var save = row.SaveNote;
            row.SaveNote = (_, _) => false;
            Click(window, "OpinionsButton");
            Assert(Collection(window) == NoteCollection.EconomicAnalysis && row.IsEditing && row.Body.PlainText.Contains("Uncommitted analysis"),
                "A rejected Analysis inline save must stop navigation and preserve its editor.");
            row.SaveNote = save;
            row.CancelEdit();

            var search = Get<TextBox>(window, "SearchBox");
            search.Text = "opiniontag";
            Assert(feed.RenderedCards.Count == 0, "Analysis search must never return an Opinions post.");
            search.Text = "analysistag";
            Assert(feed.RenderedCards.Single().Card.Id == seeds[NoteCollection.EconomicAnalysis].Id,
                "Analysis search must find its own content.");
            search.Clear();
            var tags = Field<IReadOnlyList<string>>(window, "_knownTags");
            Assert(tags.Contains("analysistag") && !tags.Contains("opiniontag") && !tags.Contains("learningtag"),
                "Analysis tag suggestions must be scoped to its independent feed.");
            Click(window, "OpinionsButton");
            search.Text = "analysistag";
            Assert(feed.RenderedCards.Count == 0, "Opinions search must exclude Analysis content.");
            search.Clear();
            tags = Field<IReadOnlyList<string>>(window, "_knownTags");
            Assert(tags.Contains("opiniontag") && !tags.Contains("analysistag"), "Opinions must retain its own tag vocabulary.");

            Invoke(window, "Review_Click", window, new RoutedEventArgs());
            var menu = Get<Button>(window, "ReviewButton").ContextMenu!;
            Assert(menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).SequenceEqual(["All tags", "#learningtag"]),
                "Review opened from a new stream must offer only learning-note tags.");
            menu.IsOpen = false;
            Invoke(window, "SelectReviewTag", new object?[] { null });
            Layout(window, 662);
            feed.CheckVisibleCards();
            Assert(Collection(window) == NoteCollection.Notes && feed.RenderedCards.Single().Card.Id == seeds[NoteCollection.Notes].Id &&
                store.Cards.Where(card => card.Collection != NoteCollection.Notes).All(card => card.Reviews.Count == 0),
                "Review must remain confined to Notes without writing history for the three reference streams.");
            Assert((bool)Invoke(window, "TryApplyProfile", new UserProfile { DisplayName = "Avery" })!, "The shared local profile must remain editable.");
            foreach (var stream in Streams.Skip(1))
            {
                Click(window, stream.Button);
                Layout(window, 662);
                feed.CheckVisibleCards();
                Assert(feed.Profile.DisplayName == "Avery" && store.Cards.Where(card => card.Collection == stream.Collection).All(card => card.Reviews.Count == 0),
                    "All reference streams must share the author while ordinary browsing leaves learning history unchanged.");
            }
        }
        finally { window.Close(); }

        var reopened = new LibraryStore(store.RootPath);
        reopened.Load();
        window = new MainWindow(reopened);
        try
        {
            foreach (var stream in Streams)
            {
                Click(window, stream.Button);
                Assert(Get<NoteEditor>(window, "Composer").PlainText.Trim() == draftText[stream.Collection] &&
                    Field<AttachmentPanel>(window, "_composerMedia").Count == (IsNew(stream.Collection) ? 1 : 0) &&
                    Get<FeedView>(window, "NotesFeed").RenderedCards.All(row => row.Card.Collection == stream.Collection),
                    "Restart must reopen all four text/image drafts and their own records independently.");
            }
        }
        finally { window.Close(); }
    }

    private static void PendingImport(string root, NoteCollection collection, string button, bool inline)
    {
        var store = Store(root, "import-" + collection + (inline ? "-inline" : "-composer"));
        if (inline) store.Upsert(Card("Original " + collection, collection));
        var image = Picture(store);
        var window = new MainWindow(store);
        try
        {
            Click(window, button);
            var feed = Get<FeedView>(window, "NotesFeed");
            var row = inline ? feed.RenderedCards.Single() : null;
            row?.BeginEdit();
            var editor = row?.Body ?? Get<NoteEditor>(window, "Composer");
            editor.PlainText = "While importing " + collection;
            var panel = row?.Attachments ?? Field<AttachmentPanel>(window, "_composerMedia");
            var completion = new TaskCompletionSource<IReadOnlyList<NoteImage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var import = (Task)Invoke(panel, "ImportAsync", new Func<Task<IReadOnlyList<NoteImage>>>(() => completion.Task))!;
            Assert(panel.IsImporting && Navigation(window).All(control => !control.IsEnabled) && editor.TextBox.IsEnabled,
                "An image pending in either new stream must disable every navigation action while leaving text editable.");
            foreach (var target in Streams)
                Assert(!(bool)Invoke(window, "SwitchCollection", target.Collection, true)! && Collection(window) == collection,
                    "Direct navigation handlers must reject every stream switch while a new-stream image is pending.");
            Assert(!inline || !feed.SavePendingEdits(), "An inline image must prevent an incomplete save in either new stream.");
            completion.SetResult([image]);
            Pump(import);
            Assert(!panel.IsImporting && Navigation(window).All(control => control.IsEnabled) && panel.Count == 1,
                "Finishing a new-stream import must attach once and restore all header controls.");
            if (inline)
            {
                Assert(feed.SavePendingEdits() && store.Cards.Single().Collection == collection && store.Cards.Single().Note!.Images.Count == 1,
                    "The accepted inline edit must persist its completed image in the original reference stream.");
            }
            else
            {
                Assert((bool)Invoke(window, "SaveDraft")! && store.LoadDraft(collection)!.Content.Images.Count == 1,
                    "The completed composer import must save to the selected reference draft.");
                Invoke(window, "PostNote");
                Assert(store.Cards.Single().Collection == collection && store.Cards.Single().Note!.Images.Count == 1,
                    "Posting a completed image from either new stream must retain its collection identity.");
            }
        }
        finally { window.Close(); }
    }

    private static bool IsNew(NoteCollection collection) => collection is NoteCollection.Opinions or NoteCollection.EconomicAnalysis;
    private static LibraryStore Store(string root, string name) { var store = new LibraryStore(Path.Combine(root, "collection-feeds", name, "Log", "Data")); store.Load(); return store; }
    private static CardDocument Card(string text, NoteCollection collection) => new() { Collection = collection, Note = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = text }] }] } };
    private static NoteImage Picture(LibraryStore store) => store.ImportImage(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC"), 1, 1);
    private static IEnumerable<Button> Navigation(MainWindow window) => new[] { "NotesButton", "ReviewButton", "MemoButton", "OpinionsButton", "EconomicAnalysisButton", "SearchButton", "MoreButton", "ImageButton" }.Select(name => Get<Button>(window, name));
    private static NoteCollection Collection(MainWindow window) => Field<NoteCollection>(window, "_collection");
    private static T Get<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Click(MainWindow window, string name) { var button = Get<Button>(window, name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static Rect Bounds(MainWindow window, FrameworkElement element) => element.TransformToAncestor(Get<FrameworkElement>(window, "Root")).TransformBounds(new Rect(element.RenderSize));
    private static void Layout(MainWindow window, double width) { var root = Get<FrameworkElement>(window, "Root"); root.Width = width; root.Height = 900; root.Measure(new Size(width, 900)); root.Arrange(new Rect(0, 0, width, 900)); root.UpdateLayout(); }
    private static void Pump(Task operation)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var expired = false;
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) => { expired = true; frame.Continue = false; };
        timeout.Start();
        _ = operation.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)), TaskScheduler.Default);
        try { if (!operation.IsCompleted) Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Assert(!expired && operation.IsCompleted, "A controlled reference-stream image import must complete on the dispatcher.");
        operation.GetAwaiter().GetResult();
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
