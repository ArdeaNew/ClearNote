using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

internal static class WidthChecks
{
    public static void Run(string root)
    {
        var store = Store(root, "steps");
        for (var i = 0; i < 24; i++) store.Upsert(Card("Card " + i + "\n" + new string('a', 150)));
        var settings = Path.Combine(Path.GetDirectoryName(store.RootPath)!, "window.json");
        var window = new MainWindow(store);
        double persisted;
        try
        {
            Assert(window.Width == 678 && !File.Exists(settings), "Opening the default window must not create an optional settings file.");
            Get<TextBox>(window, "SearchBox").Text = "Card";
            var composer = Get<NoteEditor>(window, "Composer");
            composer.InsertMarkdown("Keep this unfinished draft");
            var feed = Get<FeedView>(window, "NotesFeed");
            var row = feed.RenderedCards.First();
            row.BeginEdit(); row.Body.InsertMarkdown(" and this inline edit");
            Layout(window);
            var scroll = Field<ScrollViewer>(feed, "_scroll");
            scroll.ScrollToVerticalOffset(300); scroll.UpdateLayout();
            var offset = scroll.VerticalOffset;
            var body = row.Body.PlainText;
            var history = store.Cards.Sum(card => card.Reviews.Count);
            var library = File.ReadAllBytes(Path.Combine(store.RootPath, "library.json"));
            Assert(Adjust(window, true, new Rect(0, 0, 1600, 1000)), "A full wider step must fit an ordinary desktop.");
            Near(window.Width, 678 * 1.07, "Wider must apply exactly one seven-percent step.");
            Assert(window.MinWidth == window.Width && window.MaxWidth == window.Width,
                "The native frame must keep horizontal dragging disabled after an adjustment.");
            Layout(window);
            Assert(ReferenceEquals(row, feed.RenderedCards.First()) && row.IsEditing && row.Body.PlainText == body &&
                composer.PlainText.Contains("unfinished draft") && Get<TextBox>(window, "SearchBox").Text == "Card" &&
                Math.Abs(scroll.VerticalOffset - offset) < 1 && store.Cards.Sum(card => card.Reviews.Count) == history &&
                File.ReadAllBytes(Path.Combine(store.RootPath, "library.json")).SequenceEqual(library),
                "Changing width must preserve the feed, viewport, unsaved editors, search and review history in place.");
            Assert(Adjust(window, false, new Rect(0, 0, 1600, 1000)), "A narrower step must fit.");
            Near(window.Width, 678 * 1.07 * 0.93, "Narrower must reduce the current width by exactly seven percent.");

            var blockedWidth = window.Width;
            var blockedSettings = File.ReadAllBytes(settings);
            Assert(!Adjust(window, true, new Rect(0, 0, blockedWidth * 1.07 + 23, 1000)) &&
                window.Width == blockedWidth && File.ReadAllBytes(settings).SequenceEqual(blockedSettings),
                "An out-of-bounds step must be declined completely instead of rounding or clamping it.");
            while (Adjust(window, false, new Rect(0, 0, 1600, 1000))) { }
            Assert(window.Width >= 560 && window.Width * 0.93 < 560,
                "The smallest offered width must preserve room for the complete composer toolbar.");
            var menu = (ContextMenu)Invoke(window, "CreateMoreMenu")!;
            var actions = menu.Items.OfType<MenuItem>().ToList();
            Assert(actions.Single(item => Equals(item.Header, "Wider")).Icon is FrameworkElement &&
                actions.Single(item => Equals(item.Header, "Narrower")).Icon is FrameworkElement &&
                !actions.Single(item => Equals(item.Header, "Narrower")).IsEnabled &&
                actions.All(item => !(item.Header?.ToString() ?? "").Contains('%')),
                "Width controls must use understated icons and simple names, disable unavailable steps, and omit ratios.");
            persisted = window.Width;
            row.CancelEdit();
        }
        finally { window.Close(); }

        var reopened = new MainWindow(store);
        try
        {
            Near(reopened.Width, persisted, "The selected width must reopen from Log independently of the profile.");
            var bytes = File.ReadAllBytes(settings);
            Invoke(reopened, "FitStartupToWorkArea", new Rect(0, 0, 520, 600));
            Assert(reopened.Width == 496 && File.ReadAllBytes(settings).SequenceEqual(bytes),
                "A smaller work area may fit the startup window without overwriting the user's chosen width.");
        }
        finally { reopened.Close(); }

        StartupHeightFitsWorkArea(root);
        InvalidSettingsArePreserved(root);
        FailedSettingsSavePreservesState(root);
        ReviewStateSurvives(root);
    }

    private static void StartupHeightFitsWorkArea(string root)
    {
        var areas = new[]
        {
            new Rect(0, 0, 1920, 1040),
            new Rect(-1600, -900, 1600, 860),
            new Rect(1280, 40, 853.333333333333, 440),
            new Rect(0, 0, 520, 240)
        };
        for (var i = 0; i < areas.Length; i++)
        {
            var area = areas[i];
            var store = Store(root, "startup-height-" + i);
            var window = new MainWindow(store);
            try
            {
                Invoke(window, "FitStartupToWorkArea", area);
                Near(window.Height, area.Height - 24,
                    "Startup must use the monitor's available height instead of keeping the shorter paper-sized default.");
                Near(window.Top, area.Top + 12,
                    "Startup must leave equal space above and below the window, including on negative-coordinate monitors.");
                Assert(window.Top >= area.Top && window.Top + window.Height <= area.Bottom &&
                    window.Left >= area.Left && window.Left + window.Width <= area.Right &&
                    window.MinHeight <= window.Height && window.ResizeMode == ResizeMode.CanResize &&
                    window.MinWidth == window.Width && window.MaxWidth == window.Width,
                    "Startup must stay inside small or scaled work areas while preserving vertical resizing and the fixed width.");
                Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(store.RootPath)!, "window.json")),
                    "Fitting the startup height must not create or change optional width preferences.");
            }
            finally { window.Close(); }
        }
    }

    private static void InvalidSettingsArePreserved(string root)
    {
        foreach (var (name, content) in new[]
        {
            ("broken", "{not complete"), ("unsupported", "{\"Version\":2,\"Width\":800}"),
            ("too-small", "{\"Version\":1,\"Width\":1}"), ("too-large", "{\"Version\":1,\"Width\":999999}")
        })
        {
            var store = Store(root, name);
            var path = Path.Combine(Path.GetDirectoryName(store.RootPath)!, "window.json");
            File.WriteAllText(path, content);
            var window = new MainWindow(store);
            try
            {
                Assert(window.Width == 678 && !Adjust(window, true, new Rect(0, 0, 1600, 1000)) &&
                    File.ReadAllText(path) == content && window.Width == 678,
                    "Invalid optional settings must fall back safely and remain intact when an adjustment is attempted.");
            }
            finally { window.Close(); }
        }
    }

    private static void FailedSettingsSavePreservesState(string root)
    {
        var store = Store(root, "blocked-save");
        var window = new MainWindow(store);
        try
        {
            Assert(Adjust(window, true, new Rect(0, 0, 1600, 1000)), "The first width change must save.");
            var path = Path.Combine(Path.GetDirectoryName(store.RootPath)!, "window.json");
            var width = window.Width;
            Get<NoteEditor>(window, "Composer").InsertMarkdown("Still here");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert(!Adjust(window, true, new Rect(0, 0, 1600, 1000)) && window.Width == width &&
                    Get<NoteEditor>(window, "Composer").PlainText.Contains("Still here"),
                    "A locked preference must leave window size and unsaved content unchanged.");
            Assert(Adjust(window, true, new Rect(0, 0, 1600, 1000)), "Unlocking preferences must allow a safe retry.");
            File.WriteAllText(path, "{\"Version\":1,\"Width\":900}");
            width = window.Width;
            Assert(!Adjust(window, false, new Rect(0, 0, 1600, 1000)) && window.Width == width && File.ReadAllText(path).Contains("900"),
                "Concurrent external settings changes must be preserved instead of silently replaced.");
            Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, ".window-*.tmp").Any(),
                "Failed saves must not leave temporary preference files behind.");
        }
        finally { window.Close(); }
    }

    private static void ReviewStateSurvives(string root)
    {
        var store = Store(root, "review");
        for (var i = 0; i < 4; i++) store.Upsert(Card("Review " + i));
        var window = new MainWindow(store);
        try
        {
            Invoke(window, "SelectReviewTag", new object?[] { null });
            Layout(window);
            var feed = Get<FeedView>(window, "NotesFeed"); feed.CheckVisibleCards();
            var recorded = Field<HashSet<string>>(window, "_reviewRecorded");
            var before = recorded.ToHashSet();
            var firstRow = feed.RenderedCards.First();
            Assert(Adjust(window, true, new Rect(0, 0, 1600, 1000)), "Review width must be adjustable.");
            Layout(window); feed.CheckVisibleCards();
            Assert(ReferenceEquals(firstRow, feed.RenderedCards.First()) && ReferenceEquals(recorded, Field<HashSet<string>>(window, "_reviewRecorded")) &&
                before.All(id => store.Cards.Single(card => card.Id == id).Reviews.Count == 1),
                "A width change must preserve the active Review round without recounting previously viewed posts.");
        }
        finally { window.Close(); }
    }

    private static LibraryStore Store(string root, string name)
    {
        var store = new LibraryStore(Path.Combine(root, "width-" + name, "Log", "Data")); store.Load(); return store;
    }
    private static CardDocument Card(string text)
    {
        var card = new CardDocument();
        NoteText.SetContent(card, new NoteContent { Paragraphs = text.Split('\n').Select(line => new NoteParagraph { Runs = [new NoteRun { Text = line }] }).ToList() });
        return card;
    }
    private static bool Adjust(MainWindow window, bool wider, Rect area) => (bool)Invoke(window, "TryAdjustWidth", wider, area)!;
    private static void Layout(MainWindow window)
    {
        var root = Get<FrameworkElement>(window, "Root");
        var width = window.Width - 16;
        root.Width = width; root.Height = 754;
        root.Measure(new Size(width, 754)); root.Arrange(new Rect(0, 0, width, 754)); root.UpdateLayout();
    }
    private static T Get<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Near(double actual, double expected, string message) => Assert(Math.Abs(actual - expected) < 0.000001, message);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
