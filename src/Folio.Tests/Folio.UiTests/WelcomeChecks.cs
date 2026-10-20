using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

internal static class WelcomeChecks
{
    public static void Run(string root)
    {
        OrdinaryLocalNotes(root);
        DeletionDoesNotReseed(root);
        InitializationPreservesDraft(root);
        ExistingLibraryIsUntouched(root);
    }

    private static void OrdinaryLocalNotes(string root)
    {
        var store = Store(root, "first-run");
        Assert(store.IsNewLibrary && EnsureCreated(store), "A new library must receive its welcome notes once.");
        var cards = store.Cards;
        Assert(cards.Count == 2 && !store.IsNewLibrary && cards.Select(card => card.Id).Distinct().Count() == 2,
            "Initialization must durably save exactly two independent ordinary notes.");
        var introduction = cards.Single(card => card.Note!.Images.Count == 0);
        var scenery = cards.Single(card => card.Note!.Images.Count == 1);
        Assert(introduction.Title.Length == 0 && NoteText.PlainText(introduction).Contains("spaced repetition", StringComparison.Ordinal) &&
            NoteText.PlainText(introduction).Contains("offline", StringComparison.Ordinal) &&
            introduction.Note!.Paragraphs[0].Runs.Single().Bold,
            "The text-only welcome must explain offline capture and review, with one restrained emphasis.");
        Assert(NoteText.PlainText(scenery) == "Leave a little room for wonder." && scenery.Title.Length == 0,
            "The landscape note must use a concise caption and ordinary note content.");
        var image = scenery.Note!.Images.Single();
        var assetPath = store.GetImagePath(image);
        Assert(Path.GetDirectoryName(assetPath) == store.AssetDirectory && File.Exists(assetPath) &&
            ImageAttachments.LoadPreview(assetPath).PixelWidth > 0,
            "The welcome picture must be an independent, usable asset under Log/Data rather than an application-file reference.");
        Assert(cards.All(card => card.Blocks.Count == 0 && card.Reviews.Count == 0 && card.Memory.Repetitions == 0),
            "Welcome notes must begin without legacy blocks or synthetic memory history.");
        var original = JsonSerializer.Serialize(cards);
        Assert(!EnsureCreated(store) && JsonSerializer.Serialize(store.Cards) == original,
            "A second initialization in the same process must not rewrite the welcome notes.");

        var window = new MainWindow(store);
        try
        {
            var feed = (FeedView)window.FindName("NotesFeed");
            Assert(feed.Profile.DisplayName == "You" && feed.Profile.Avatar is null && feed.RenderedCards.Count == 2,
                "The welcome notes must use the normal default identity and appear in the normal feed.");
            foreach (var row in feed.RenderedCards)
            {
                row.Measure(new Size(548, double.PositiveInfinity));
                row.Arrange(new Rect(0, 0, 548, row.DesiredSize.Height));
                row.UpdateLayout();
                var descendants = Descendants(row).ToArray();
                Assert(descendants.OfType<TextBlock>().Single(block =>
                    AutomationProperties.GetAutomationId(block) == "NoteAuthor-" + row.Card.Id).Text == "You" &&
                    descendants.OfType<Border>().Single(border => AutomationProperties.GetName(border) == "Author avatar").Child is FrameworkElement,
                    "Every welcome post must show the default name and designed default avatar.");
            }
            Assert(!File.Exists(Path.Combine(store.RootPath, "profile.json")),
                "A usable default identity must not manufacture a custom profile file.");
        }
        finally { window.Close(); }

        var reopened = Reload(store);
        Assert(!reopened.IsNewLibrary && !EnsureCreated(reopened) && JsonSerializer.Serialize(reopened.Cards) == original,
            "Restarting must read the same persisted welcome notes without new IDs, dates or duplicate posts.");
    }

    private static void DeletionDoesNotReseed(string root)
    {
        var store = Store(root, "delete");
        EnsureCreated(store);
        var scenery = store.Cards.Single(card => card.Note!.Images.Count == 1);
        var imagePath = store.GetImagePath(scenery.Note!.Images.Single());
        store.Delete(scenery.Id);
        Assert(store.Cards.Count == 1 && !File.Exists(imagePath),
            "Deleting the landscape welcome must delete its unshared local picture as for any other post.");
        var primary = File.ReadAllText(Path.Combine(store.RootPath, "library.json"));
        var previous = File.ReadAllText(Path.Combine(store.RootPath, "library.previous.json"));
        Assert(!primary.Contains(scenery.Id, StringComparison.Ordinal) && !previous.Contains(scenery.Id, StringComparison.Ordinal),
            "Deleting a welcome post must remove it from both live and local recovery data.");
        store = Reload(store);
        Assert(!EnsureCreated(store) && store.Cards.Count == 1, "Deleting one welcome must remain effective after restart.");
        store.Delete(store.Cards.Single().Id);
        store = Reload(store);
        Assert(!EnsureCreated(store) && store.Cards.Count == 0 && Directory.GetFiles(store.AssetDirectory).Length == 0,
            "An intentionally empty library must stay empty after restart, without recreating the welcome picture.");

        var backup = Path.Combine(Path.GetDirectoryName(store.RootPath)!, "empty.zip");
        store.Backup(backup);
        var restored = Store(root, "restored-empty");
        restored.Restore(backup);
        Assert(!restored.IsNewLibrary && !EnsureCreated(restored) && restored.Cards.Count == 0,
            "Restoring an empty saved library must not turn it into a new library or recreate welcome posts.");
    }

    private static void InitializationPreservesDraft(string root)
    {
        var store = Store(root, "draft");
        var draft = new ComposerDraft
        {
            Content = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "A thought still in progress" }] }] }
        };
        store.SaveDraft(draft);
        var draftPath = Path.Combine(store.RootPath, "draft.json");
        var bytes = File.ReadAllBytes(draftPath);
        Assert(EnsureCreated(store), "An unsent draft must not prevent ordinary welcome initialization.");
        var window = new MainWindow(store);
        try
        {
            Assert(((NoteEditor)window.FindName("Composer")).PlainText.Trim() == "A thought still in progress" &&
                store.Cards.Count == 2 && store.Cards.All(card => card.Id != draft.CardId),
                "Welcome posts must preserve the unsent composer without converting it to a published note.");
        }
        finally { window.Close(); }
        Assert(File.ReadAllBytes(draftPath).SequenceEqual(bytes) && store.LoadDraft()?.CardId == draft.CardId,
            "Closing after initialization must not rewrite or discard an unchanged draft.");
    }

    private static void ExistingLibraryIsUntouched(string root)
    {
        var store = Store(root, "existing");
        var card = new CardDocument { Note = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "An existing thought" }] }] } };
        store.Upsert(card);
        var primary = Path.Combine(store.RootPath, "library.json");
        var before = File.ReadAllBytes(primary);
        store = Reload(store);
        Assert(!EnsureCreated(store) && File.ReadAllBytes(primary).SequenceEqual(before) && store.Cards.Single().Id == card.Id &&
            !Directory.Exists(store.AssetDirectory),
            "An existing library must receive neither welcome posts nor imported welcome assets.");
    }

    private static LibraryStore Store(string root, string name)
    {
        var store = new LibraryStore(Path.Combine(root, "welcome-" + name, "Log", "Data"));
        store.Load();
        return store;
    }

    private static LibraryStore Reload(LibraryStore store)
    {
        var reopened = new LibraryStore(store.RootPath);
        reopened.Load();
        return reopened;
    }

    private static bool EnsureCreated(LibraryStore store) => (bool)typeof(App).Assembly.GetType("Folio.App.WelcomeNotes")!
        .GetMethod("EnsureCreated", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [store])!;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
