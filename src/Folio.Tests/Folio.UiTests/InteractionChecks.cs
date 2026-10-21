using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Committed-content events and rejected navigation run against isolated libraries.</summary>
internal static class InteractionChecks
{
    public static void Run(string root)
    {
        CompletionEvents();
        TagsFollowCommittedContent(root);
        SavedFiltersPreserveOtherEdits(root);
        SearchRollback(root);
        ReviewKeepsExplicitTag(root);
    }

    private static void CompletionEvents()
    {
        var document = Card("Original #Before");
        var feed = new FeedView { SaveNote = (_, _) => false, DeleteNote = _ => false };
        feed.Reset(_ => [document], false);
        var row = feed.RenderedCards.Single();
        var events = 0;
        feed.ContentChanged += () =>
        {
            events++;
            Assert(!row.IsEditing, "A committed-content event must follow the read-only transition or removal.");
        };
        row.BeginEdit();
        row.Body.PlainText = "Unsaved #After";
        Assert(!row.TrySave() && events == 0 && row.IsEditing,
            "Rejected persistence must not publish a completion event or close its editor.");
        row.CancelEdit();
        Assert(events == 0 && row.Body.PlainText == "Original #Before", "Cancel must not publish committed content.");
        row.BeginEdit(); row.Body.PlainText = "Committed #After";
        feed.SaveNote = (_, _) => true;
        Assert(row.TrySave() && events == 1 && row.Body.PlainText == "Committed #After",
            "A successful save must publish exactly one event after its accepted content is visible.");
        Delete(row);
        Assert(events == 1 && feed.RenderedCards.Count == 1, "A rejected deletion must not publish completion.");
        feed.DeleteNote = _ => true;
        Delete(row);
        Assert(events == 2 && feed.RenderedCards.Count == 0, "A deletion event must follow removal from the feed.");

        var old = Card("Detached row");
        feed.Reset(_ => [old], false);
        var detached = feed.RenderedCards.Single();
        feed.Reset(_ => [Card("Current row")], false);
        var afterReset = events;
        detached.BeginEdit(); detached.Body.PlainText = "A detached save";
        Assert(detached.TrySave() && events == afterReset,
            "Reset must detach a retired row's completion events from the current feed.");
    }

    private static void TagsFollowCommittedContent(string root)
    {
        var store = Store(root, "interaction-tags");
        var first = Card("Learning #Old");
        var second = Card("Another idea #Keep");
        store.Upsert(first); store.Upsert(second);
        var window = new MainWindow(store);
        try
        {
            var feed = Get<FeedView>(window, "NotesFeed");
            var changed = feed.RenderedCards.Single(row => row.Card.Id == first.Id);
            var pending = feed.RenderedCards.Single(row => row.Card.Id == second.Id);
            pending.BeginEdit(); pending.Body.PlainText = "Keep this unsaved editor #Keep";
            changed.BeginEdit(); changed.Body.PlainText = "Committed idea #Fresh";
            Assert(changed.TrySave(), "The tag edit must save.");
            Assert(Tags(window).Contains("Fresh") && !Tags(window).Contains("Old") &&
                pending.IsEditing && pending.Body.PlainText.Contains("unsaved") &&
                feed.RenderedCards.Contains(pending),
                "A successful inline save must update tags without rebuilding or saving another editor.");
            var composer = Get<NoteEditor>(window, "Composer");
            composer.PlainText = "#Fr";
            composer.TextBox.CaretPosition = composer.TextBox.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)!;
            Assert(Field<TagSuggestions>(window, "_tagSuggestions").Complete("Fresh"),
                "A newly committed tag must be immediately available to the real composer completion controller.");
            changed.DeleteNote = card => { store.Delete(card.Id); return true; };
            Delete(changed);
            Assert(!Tags(window).Contains("Fresh") && pending.IsEditing && pending.Body.PlainText.Contains("unsaved"),
                "Deleting the final saved owner of a tag must remove it without discarding another editor.");
            pending.CancelEdit();
        }
        finally { window.Close(); }
    }

    private static void SavedFiltersPreserveOtherEdits(string root)
    {
        var store = Store(root, "interaction-filter");
        var cards = Enumerable.Range(0, 30).Select(index => Card("needle entry " + index)).ToArray();
        foreach (var card in cards) store.Upsert(card);
        var window = new MainWindow(store);
        try
        {
            Get<TextBox>(window, "SearchBox").Text = "needle";
            var feed = Get<FeedView>(window, "NotesFeed");
            Layout(window);
            var scroll = Descendants(feed).OfType<ScrollViewer>().Single(item => AutomationProperties.GetAutomationId(item) == "NotesFeed");
            scroll.ScrollToVerticalOffset(180);
            Layout(window);
            Assert(scroll.VerticalOffset > 0, "The filter regression needs a scrolled feed.");
            var changed = feed.RenderedCards[0];
            var pending = feed.RenderedCards[1];
            pending.BeginEdit(); pending.Body.PlainText = "needle pending content";
            changed.BeginEdit(); changed.Body.PlainText = "No longer matching";
            Assert(changed.TrySave(), "The filtered edit must save.");
            Layout(window);
            Assert(!feed.RenderedCards.Contains(changed) && feed.RenderedCards.Contains(pending) && pending.IsEditing &&
                pending.Body.PlainText == "needle pending content" && scroll.VerticalOffset > 0,
                "Saving content outside the active search must remove only that saved row and retain other editors and scroll.");
            pending.CancelEdit();
        }
        finally { window.Close(); }
    }

    private static void SearchRollback(string root)
    {
        var store = Store(root, "interaction-search-rollback");
        store.Upsert(Card("needle original"));
        var other = Card("other result"); store.Upsert(other);
        var window = new MainWindow(store);
        try
        {
            var search = Get<TextBox>(window, "SearchBox");
            search.Text = "needle";
            var feed = Get<FeedView>(window, "NotesFeed");
            var row = feed.RenderedCards.Single();
            row.BeginEdit(); row.Body.PlainText = "needle pending revision";
            using (var locked = new FileStream(Path.Combine(store.RootPath, "library.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                search.Text = "other";
                Assert(search.Text == "needle" && Equals(Get<Button>(window, "SearchButton").ToolTip, "Search: needle") &&
                    Field<string>(window, "_appliedSearchText") == "needle" && ReferenceEquals(feed.RenderedCards.Single(), row) &&
                    row.IsEditing && row.Body.PlainText == "needle pending revision" &&
                    Get<TextBlock>(window, "ErrorStatus").Visibility == Visibility.Visible,
                    "A failed inline save must restore the applied query, search indicator and feed while keeping the edit and error.");
            }
            search.Text = "other";
            Assert(search.Text == "other" && feed.RenderedCards.Single().Card.Id == other.Id &&
                NoteText.PlainText(store.Cards.Single(card => card.Id == row.Card.Id)) == "needle pending revision",
                "After releasing the file, retrying search must save the edit and apply the requested filter.");
        }
        finally { window.Close(); }
    }

    private static void ReviewKeepsExplicitTag(string root)
    {
        var store = Store(root, "interaction-review-tag");
        var tagged = Card("Target #Focus");
        store.Upsert(tagged); store.Upsert(Card("Unrelated #Other"));
        var window = new MainWindow(store);
        try
        {
            Invoke(window, "SelectReviewTag", "Focus");
            var feed = Get<FeedView>(window, "NotesFeed");
            var row = feed.RenderedCards.Single();
            row.BeginEdit(); row.Body.PlainText = "Target #Fresh";
            Assert(row.TrySave() && feed.RenderedCards.Count == 0 && Field<string?>(window, "_reviewTag") == "Focus",
                "Removing the final matching tag must keep the explicit Review filter and leave an empty result.");
            Invoke(window, "RefreshFeed");
            Assert(feed.RenderedCards.Count == 0 && Field<string?>(window, "_reviewTag") == "Focus",
                "Refreshing an absent explicit tag must not silently show all other notes.");
            Invoke(window, "SelectReviewTag", "Fresh");
            row = feed.RenderedCards.Single();
            row.DeleteNote = card => { store.Delete(card.Id); return true; };
            Delete(row);
            Invoke(window, "RefreshFeed");
            Assert(feed.RenderedCards.Count == 0 && Field<string?>(window, "_reviewTag") == "Fresh" && !Tags(window).Contains("Fresh"),
                "Deletion must update tags while retaining the selected Review filter.");
        }
        finally { window.Close(); }
    }

    private static LibraryStore Store(string root, string name)
    {
        var store = new LibraryStore(Path.Combine(root, name)); store.Load(); return store;
    }
    private static CardDocument Card(string text) => new()
    {
        Note = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = text }] }] }
    };
    private static IReadOnlyList<string> Tags(MainWindow window) => Field<IReadOnlyList<string>>(window, "_knownTags");
    private static void Delete(FeedNoteCard row)
    {
        // Offscreen callers may not have measured the UserControl yet. Materialize its
        // real template before locating and invoking the same menu used in the app.
        if (VisualTreeHelper.GetChildrenCount(row) == 0)
        {
            row.Measure(new Size(600, double.PositiveInfinity));
            row.Arrange(new Rect(new Point(), row.DesiredSize));
            row.UpdateLayout();
        }
        var options = Descendants(row).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "NoteOptions-" + row.Card.Id);
        options.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Delete note"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
    private static void Layout(MainWindow window)
    {
        var root = Get<FrameworkElement>(window, "Root");
        root.Width = 662; root.Height = 754;
        root.Measure(new Size(662, 754)); root.Arrange(new Rect(0, 0, 662, 754)); root.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
    private static T Get<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
