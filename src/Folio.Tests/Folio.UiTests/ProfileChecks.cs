using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

internal static class ProfileChecks
{
    public static void Run(string root)
    {
        IdentityPreservesWork(root);
        FailedSavePreservesWork(root);
        DialogEditsAnIndependentCopy(root);
        SearchPopupPreservesFilter(root);
    }

    private static void IdentityPreservesWork(string root)
    {
        var store = Store(root, "identity");
        var card = Card("#Learning An original note.");
        store.Upsert(card);
        var window = new MainWindow(store);
        try
        {
            var feed = (FeedView)window.FindName("NotesFeed");
            var composer = (NoteEditor)window.FindName("Composer");
            var item = feed.RenderedCards.Single();
            Assert(feed.Profile.DisplayName == "You" && !File.Exists(Path.Combine(store.RootPath, "profile.json")),
                "A default profile must work immediately without a modal or a first-run write.");
            composer.PlainText = "An unsent private thought";
            item.BeginEdit();
            item.Body.PlainText = "An inline edit that has not been saved";
            var avatar = Picture(store);
            var candidate = new UserProfile { DisplayName = "  Astrid  ", Avatar = avatar };
            Assert(Apply(window, candidate), "A valid local profile must save.");
            candidate.DisplayName = "Changed by caller";
            candidate.Avatar!.PixelWidth = 99;
            Assert(ReferenceEquals(feed.RenderedCards.Single(), item) && item.IsEditing &&
                item.Body.PlainText.Trim() == "An inline edit that has not been saved" &&
                NoteText.PlainText(store.Cards.Single()).Contains("original"),
                "A profile update must preserve the exact inline editor and its unsaved buffer without writing the note.");
            Assert(composer.PlainText.Trim() == "An unsent private thought" && feed.Profile.DisplayName == "Astrid" &&
                feed.Profile.Avatar!.PixelWidth == 2 && Author(item) == "Astrid",
                "Saving identity must update the visible author while preserving the independent composer and copied profile.");
            var saved = store.LoadProfile();
            Assert(saved.DisplayName == "Astrid" && saved.Avatar?.AssetName == avatar.AssetName,
                "The accepted name and picture must survive an independent profile read.");

            item.CancelEdit();
            Invoke(window, "SelectReviewTag", "Learning");
            var reviewItem = feed.RenderedCards.Single();
            Invoke(window, "RecordView", reviewItem.Card);
            var reviews = JsonSerializer.Serialize(store.Cards.Single().Reviews);
            var memory = JsonSerializer.Serialize(store.Cards.Single().Memory);
            reviewItem.BeginEdit();
            reviewItem.Body.PlainText = "#Learning A pending review edit";
            Assert(Apply(window, new UserProfile { DisplayName = "Nova", Avatar = saved.Avatar }), "Identity can change during Review.");
            Assert(ReferenceEquals(feed.RenderedCards.Single(), reviewItem) && reviewItem.IsEditing &&
                Field<bool>(window, "_reviewMode") && Field<string?>(window, "_reviewTag") == "Learning" &&
                JsonSerializer.Serialize(store.Cards.Single().Reviews) == reviews &&
                JsonSerializer.Serialize(store.Cards.Single().Memory) == memory,
                "Changing identity must preserve the Review selection, editor, memory state, and review history.");
            Invoke(window, "RecordView", reviewItem.Card);
            Assert(JsonSerializer.Serialize(store.Cards.Single().Reviews) == reviews,
                "Changing identity must not start a new review round or allow a repeat view.");
            Invoke(window, "PostNote");
            var posted = feed.RenderedCards.Single(row => NoteText.PlainText(row.Card).Contains("private thought"));
            Assert(store.Cards.Count == 2 && Author(posted) == "Nova" && posted.Card.Note!.Images.Count == 0,
                "A newly posted note must display the local identity without treating the avatar as a note attachment.");
        }
        finally { window.Close(); }
    }

    private static void FailedSavePreservesWork(string root)
    {
        var store = Store(root, "failure");
        store.Upsert(Card("Original persisted content"));
        store.SaveProfile(new UserProfile { DisplayName = "Original author" });
        var window = new MainWindow(store);
        try
        {
            var feed = (FeedView)window.FindName("NotesFeed");
            var composer = (NoteEditor)window.FindName("Composer");
            composer.PlainText = "Keep this composer buffer";
            var item = feed.RenderedCards.Single();
            item.BeginEdit();
            item.Body.PlainText = "Keep this inline buffer";
            var external = new LibraryStore(store.RootPath);
            external.Load();
            external.LoadProfile();
            external.SaveProfile(new UserProfile { DisplayName = "External author" });
            var path = Path.Combine(store.RootPath, "profile.json");
            var before = File.ReadAllBytes(path);
            Assert(!Apply(window, new UserProfile { DisplayName = "Rejected author" }),
                "A profile changed outside this window must reject an overwrite.");
            Assert(File.ReadAllBytes(path).SequenceEqual(before) && feed.Profile.DisplayName == "Original author" &&
                Author(item) == "Original author" && ReferenceEquals(feed.RenderedCards.Single(), item) &&
                item.IsEditing && item.Body.PlainText.Trim() == "Keep this inline buffer" &&
                composer.PlainText.Trim() == "Keep this composer buffer",
                "A failed profile save must preserve the outside file, current identity, editor, and composer.");
            Assert(((TextBlock)window.FindName("ErrorStatus")).Visibility == Visibility.Visible,
                "A failed save must provide an English recovery message.");
        }
        finally { window.Close(); }
    }

    private static void DialogEditsAnIndependentCopy(string root)
    {
        var store = Store(root, "dialog");
        var picture = Picture(store);
        var initial = new UserProfile();
        var accepted = 0;
        Func<UserProfile, bool> save = profile => { store.SaveProfile(profile); accepted++; return true; };
        var dialog = Dialog(store, initial, save);
        try
        {
            Name(dialog).Text = "Uncommitted author";
            Invoke(dialog, "SetAvatar", picture);
            Assert(initial.DisplayName == "You" && initial.Avatar is null && accepted == 0 &&
                !File.Exists(Path.Combine(store.RootPath, "profile.json")),
                "Editing a profile or its picture must not mutate the original profile or commit on Cancel.");
        }
        finally { dialog.Close(); }

        dialog = Dialog(store, initial, _ => false);
        try
        {
            Name(dialog).Text = "Retry this name";
            Invoke(dialog, "SetAvatar", picture);
            Assert(!(bool)Invoke(dialog, "TrySave")! && Name(dialog).Text == "Retry this name" &&
                Field<NoteImage?>(dialog, "_avatarImage")?.AssetName == picture.AssetName &&
                Field<TextBlock>(dialog, "_status").Visibility == Visibility.Visible,
                "A rejected profile save must keep both proposed values in the dialog for retry.");
        }
        finally { dialog.Close(); }

        dialog = Dialog(store, initial, save);
        try
        {
            Name(dialog).Text = new string('a', 81);
            Assert(!(bool)Invoke(dialog, "TrySave")! && accepted == 0,
                "An invalid display name must be explained before the save callback runs.");
            Name(dialog).Text = "  Katherine  ";
            Invoke(dialog, "SetAvatar", picture);
            Assert((bool)Invoke(dialog, "TrySave")! && store.LoadProfile().DisplayName == "Katherine" &&
                store.LoadProfile().Avatar?.AssetName == picture.AssetName,
                "Saving the dialog must persist a trimmed name and the chosen local picture.");
        }
        finally { dialog.Close(); }

        dialog = Dialog(store, store.LoadProfile(), save);
        try
        {
            Name(dialog).Text = "   ";
            Invoke(dialog, "SetAvatar", new object?[] { null });
            Assert((bool)Invoke(dialog, "TrySave")! && store.LoadProfile().DisplayName == "You" && store.LoadProfile().Avatar is null,
                "A user can remove the picture and return an empty name to the usable default.");
        }
        finally { dialog.Close(); }
    }

    private static void SearchPopupPreservesFilter(string root)
    {
        var store = Store(root, "search-popup");
        store.Upsert(Card("Keep a clear thought"));
        store.Upsert(Card("Another thought"));
        var window = new MainWindow(store);
        try
        {
            Layout((FrameworkElement)window.FindName("Root"));
            var popup = (Popup)window.FindName("SearchPopup");
            var search = (TextBox)window.FindName("SearchBox");
            var button = (Button)window.FindName("SearchButton");
            var feed = (FeedView)window.FindName("NotesFeed");
            Assert(button.Content is FrameworkElement && !popup.IsOpen,
                "Search must use a compact icon and start with its input tucked away.");
            search.Text = "Keep";
            Invoke(window, "Search_Click", button, new RoutedEventArgs());
            // The synthetic window is never shown: WPF defers a Popup's effective IsOpen until Loaded.
            Assert(Equals(popup.ReadLocalValue(Popup.IsOpenProperty), true),
                "The Search icon must request its input popup without opening a test window.");
            var source = new InMemorySource((FrameworkElement)window.FindName("Root"));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke(window, "Search_KeyDown", search, escape);
            Assert(escape.Handled && Equals(popup.ReadLocalValue(Popup.IsOpenProperty), false) && search.Text == "Keep" && feed.RenderedCards.Count == 1,
                "Escape must dismiss the search popup while preserving the current query and filtered feed.");
            Invoke(window, "Search_Click", button, new RoutedEventArgs());
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke(window, "Search_KeyDown", search, enter);
            Assert(enter.Handled && Equals(popup.ReadLocalValue(Popup.IsOpenProperty), false) && search.Text == "Keep" && feed.RenderedCards.Count == 1 &&
                button.ToolTip is string tip && tip.Contains("Keep", StringComparison.Ordinal),
                "Enter must keep the query active, with the icon tooltip exposing the current filter.");
        }
        finally
        {
            ((Popup)window.FindName("SearchPopup")).IsOpen = false;
            window.Close();
        }
    }

    private static LibraryStore Store(string root, string name)
    {
        var store = new LibraryStore(Path.Combine(root, "profile-" + name, "Log", "Data"));
        store.Load();
        return store;
    }
    private static CardDocument Card(string text)
    {
        var card = new CardDocument();
        NoteText.SetContent(card, new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = text }] }] });
        return card;
    }
    private static NoteImage Picture(LibraryStore store) => ImageAttachments.ImportBitmap(
        BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 100, 120, 140, 255, 160, 180, 200, 128 }, 8), store);
    private static Window Dialog(LibraryStore store, UserProfile profile, Func<UserProfile, bool> save) =>
        (Window)Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("Folio.App.ProfileDialog")!, store, profile, save)!;
    private static TextBox Name(Window dialog) => (TextBox)dialog.GetType().GetProperty("NameInput")!.GetValue(dialog)!;
    private static bool Apply(MainWindow window, UserProfile profile) => (bool)Invoke(window, "TryApplyProfile", profile)!;
    private static string? Author(FeedNoteCard item)
    {
        Layout(item);
        return Descendants(item).OfType<TextBlock>().Single(block =>
            AutomationProperties.GetAutomationId(block) == "NoteAuthor-" + item.Card.Id).Text;
    }
    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(560, 754));
        element.Arrange(new Rect(0, 0, 560, Math.Max(1, element.DesiredSize.Height)));
        element.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] values) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, values);
    private sealed class InMemorySource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
