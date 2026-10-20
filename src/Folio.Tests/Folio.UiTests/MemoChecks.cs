using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

internal static class MemoChecks
{
    public static void Run(string root)
    {
        var store = new LibraryStore(Path.Combine(root, "memo-ui")); store.Load();
        var note = Card("Learning #learn");
        var memo = Card("Address #address", NoteCollection.Memo);
        store.Upsert(note); store.Upsert(memo);
        var window = new MainWindow(store);
        try
        {
            var editor = Get<NoteEditor>(window, "Composer");
            var feed = Get<FeedView>(window, "NotesFeed");
            Assert(feed.RenderedCards.Single().Card.Id == note.Id, "Notes must exclude Memo posts.");
            editor.InsertMarkdown("Unsent learning idea");
            Click(window, "MemoButton");
            Assert(Collection(window) == NoteCollection.Memo && feed.RenderedCards.Single().Card.Id == memo.Id &&
                string.IsNullOrWhiteSpace(editor.PlainText) && store.LoadDraft() is not null,
                "Switching to Memo must save the Notes draft and restore an independent composer.");
            editor.InsertMarkdown("A useful place #places");
            var image = store.ImportImage(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC"), 1, 1);
            Field<AttachmentPanel>(window, "_composerMedia").AddImages([image]);
            Click(window, "NotesButton");
            Assert(editor.PlainText.Contains("Unsent learning") && !Field<AttachmentPanel>(window, "_composerMedia").HasImages,
                "Memo pictures cannot leak into the Notes draft.");
            Click(window, "MemoButton");
            Assert(editor.PlainText.Contains("useful place") && Field<AttachmentPanel>(window, "_composerMedia").Count == 1,
                "Memo text and attachments must return together.");
            Invoke(window, "PostNote");
            Assert(store.Cards.Count == 3 && store.Cards.Count(card => card.Collection == NoteCollection.Memo) == 2 &&
                store.LoadDraft(NoteCollection.Memo) is null && store.LoadDraft() is not null &&
                feed.RenderedCards.Count == 2 && feed.RenderedCards.All(row => row.Card.Collection == NoteCollection.Memo),
                "Posting Memo must affect only its stream and draft.");

            Get<TextBox>(window, "SearchBox").Text = "Learning";
            Assert(feed.RenderedCards.Count == 0, "Memo search must not return matching learning notes.");
            Get<TextBox>(window, "SearchBox").Text = "Address";
            Assert(feed.RenderedCards.Single().Card.Id == memo.Id, "Memo search must find its own posts.");
            Get<TextBox>(window, "SearchBox").Clear();
            Assert(Field<IReadOnlyList<string>>(window, "_knownTags").Contains("address") &&
                !Field<IReadOnlyList<string>>(window, "_knownTags").Contains("learn"),
                "Memo tag suggestions must be scoped to Memo.");

            Invoke(window, "Review_Click", window, new RoutedEventArgs());
            var menu = Get<Button>(window, "ReviewButton").ContextMenu!;
            Assert(menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).SequenceEqual(["All tags", "#learn"]),
                "Review tags must never include Memo-only tags.");
            menu.IsOpen = false;
            Invoke(window, "SelectReviewTag", new object?[] { null });
            Layout(window); feed.CheckVisibleCards();
            Assert(Collection(window) == NoteCollection.Notes && feed.RenderedCards.Single().Card.Id == note.Id &&
                store.Cards.Where(card => card.Collection == NoteCollection.Memo).All(card => card.Reviews.Count == 0),
                "Review opened from Memo must browse Notes exclusively without changing Memo memory.");
            Click(window, "MemoButton");
            Layout(window); feed.CheckVisibleCards();
            Assert(store.Cards.Where(card => card.Collection == NoteCollection.Memo).All(card => card.Reviews.Count == 0),
                "Ordinary Memo browsing never counts as learning.");

            var row = feed.RenderedCards.Single(row => row.Card.Id == memo.Id);
            row.BeginEdit(); row.Body.InsertMarkdown("Edited reference");
            Click(window, "NotesButton");
            Assert(NoteText.PlainText(store.Cards.Single(card => card.Id == memo.Id)).Contains("Edited reference"),
                "Switching feeds must persist an active Memo inline edit.");
            Click(window, "MemoButton");
            row = feed.RenderedCards.First(); row.BeginEdit();
            var save = row.SaveNote; row.SaveNote = (_, _) => false;
            Click(window, "NotesButton");
            Assert(Collection(window) == NoteCollection.Memo && row.IsEditing,
                "A failed inline save must abort navigation without discarding its editor.");
            row.SaveNote = save; row.CancelEdit();

            editor.InsertMarkdown("Keep this unsent reference");
            Assert((bool)Invoke(window, "SaveDraft")!, "The Memo draft must save before conflict testing.");
            editor.InsertMarkdown(" and its revision");
            using (var locked = new FileStream(Path.Combine(store.RootPath, "memo-draft.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click(window, "NotesButton");
                Assert(Collection(window) == NoteCollection.Memo && editor.PlainText.Contains("its revision"),
                    "A locked Memo draft must abort navigation with all current text intact.");
            }
            Click(window, "NotesButton");
            Assert(Collection(window) == NoteCollection.Notes && editor.PlainText.Contains("Unsent learning"),
                "Resolving the write failure must allow a safe retry and restore the other draft.");
            Assert((bool)Invoke(window, "TryApplyProfile", new UserProfile { DisplayName = "Avery" })!, "Profile changes must remain available.");
            Click(window, "MemoButton");
            Assert(feed.Profile.DisplayName == "Avery" && editor.PlainText.Contains("its revision"),
                "Both streams must share the author identity without losing their independent drafts.");
        }
        finally { window.Close(); }

        var reopened = new LibraryStore(store.RootPath); reopened.Load();
        var next = new MainWindow(reopened);
        try
        {
            Assert(Get<NoteEditor>(next, "Composer").PlainText.Contains("Unsent learning"), "Restart must retain the Notes draft.");
            Click(next, "MemoButton");
            Assert(Get<NoteEditor>(next, "Composer").PlainText.Contains("its revision") &&
                Get<FeedView>(next, "NotesFeed").RenderedCards.All(row => row.Card.Collection == NoteCollection.Memo),
                "Restart and Memo selection must restore its saved draft and own feed.");
        }
        finally { next.Close(); }
    }

    private static NoteCollection Collection(MainWindow window) => Field<NoteCollection>(window, "_collection");
    private static CardDocument Card(string text, NoteCollection collection = NoteCollection.Notes) => new()
    {
        Collection = collection, Note = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = text }] }] }
    };
    private static void Layout(MainWindow window)
    {
        var root = Get<FrameworkElement>(window, "Root");
        root.Width = 662; root.Height = 754;
        root.Measure(new Size(662, 754)); root.Arrange(new Rect(0, 0, 662, 754)); root.UpdateLayout();
    }
    private static T Get<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Click(MainWindow window, string name)
    {
        var button = Get<Button>(window, name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
