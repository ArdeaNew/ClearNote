using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Documentation scenes and scroll frames rendered from real controls into an isolated example library.</summary>
internal static class ShowcaseRenderer
{
    private const int ColumnWidth = 662;
    private const int ReadingHeight = 820;

    public static void Render(string dataRoot, string photosDirectory, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var store = Prepare(Path.Combine(dataRoot, "showcase", "Log", "Data"), photosDirectory);
        WithWindow(store, window =>
        {
            var image = ThroughLast(window, 1850);
            Save(image, Path.Combine(outputDirectory, "feed-long.png"));
            Save(CaptureRoot(window, ReadingHeight, scale: 2), Path.Combine(outputDirectory, "feed-top.png"));
            RenderScrollFrames(window, outputDirectory);
        });
        WithWindow(store, window =>
        {
            Get<Button>(window, "MemoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Save(ThroughLast(window, 900), Path.Combine(outputDirectory, "memo.png"));
        });
        WithWindow(store, window =>
        {
            Get<Button>(window, "OpinionsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Save(ThroughLast(window, 900), Path.Combine(outputDirectory, "opinions.png"));
        });
        WithWindow(store, window =>
        {
            Get<Button>(window, "EconomicAnalysisButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Save(ThroughLast(window, 900), Path.Combine(outputDirectory, "economic-analysis.png"));
        });
        WithWindow(store, window =>
        {
            Invoke(window, "SelectReviewTag", "Learning");
            Save(ThroughLast(window, 900), Path.Combine(outputDirectory, "review.png"));
        });
        WithWindow(store, window =>
        {
            var row = Get<FeedView>(window, "NotesFeed").RenderedCards.First();
            row.BeginEdit();
            row.Body.TextBox.CaretPosition = row.Body.TextBox.Document.ContentEnd;
            row.Body.InsertMarkdown("\nKeep the useful part. #Ideas");
            Save(ThroughRow(window, row, 800), Path.Combine(outputDirectory, "edit.png"));
            row.CancelEdit();
        });
        Console.WriteLine("Rendered application-control showcase with three supplied pictures: " + outputDirectory);
    }

    private static LibraryStore Prepare(string libraryPath, string photosDirectory)
    {
        if (Directory.Exists(libraryPath) && Directory.EnumerateFileSystemEntries(libraryPath).Any())
            throw new IOException("The showcase requires a new example library.");
        var files = Directory.EnumerateFiles(photosDirectory)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png")
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        if (files.Length != 3) throw new ArgumentException("Supply a folder containing at least three pictures.");
        var store = new LibraryStore(libraryPath);
        store.Load();
        // The ordinary importer validates the supplied source photos and copies them into the
        // isolated library. Rendered documentation carries pixels, not their EXIF metadata.
        var photos = ImageAttachments.ImportPaths(files, store);
        var time = new DateTimeOffset(2026, 10, 21, 14, 30, 0, TimeSpan.FromHours(8));
        Add("**A notebook that reads like a feed.**\nSmall thoughts, kept close. #Ideas", minutes: 0);
        Add("Some thoughts can wait. #Cats", minutes: -7, images: [photos[0]]);
        Add("**Notice, then name.**\n1. Look without rushing.\n2. Keep one detail.\n3. Return to it. #Learning", minutes: -18);
        Add("Two frames. Same kind of quiet. #Cats", minutes: -32, images: [photos[1], photos[2]]);
        Add("What did you notice today? #Learning", minutes: -46);
        Add("Keep what matters.\nLeave a little room around it. #Ideas", minutes: -58);
        Add("A good note is easy to come back to. #Learning", minutes: -72);
        Add("The little cafe by the park.\nTry the window seat. #Places", minutes: 0, collection: NoteCollection.Memo);
        Add("Library hours\n10:00–18:00, Monday to Saturday. #Everyday", minutes: -15, collection: NoteCollection.Memo);
        Add("Bring a notebook, water and headphones. #Everyday", minutes: -35, collection: NoteCollection.Memo);
        Add("A gift idea\nA small print of a favourite photograph. #Later", minutes: -50, collection: NoteCollection.Memo);
        Add("**A point of view to sit with.**\nWhat evidence would change this conclusion? #Reading", minutes: 0, collection: NoteCollection.Opinions);
        Add("A useful sentence is a place to begin thinking. #Excerpts", minutes: -20, collection: NoteCollection.Opinions);
        Add("Keep the words and the source together. #Reading", minutes: -45, collection: NoteCollection.Opinions);
        Add("**The weekly notebook**\n1. Prices\n2. Employment\n3. Demand\n#Macro", minutes: 0, collection: NoteCollection.EconomicAnalysis);
        Add("Compare the trend with the headline. #Data", minutes: -25, collection: NoteCollection.EconomicAnalysis);
        Add("Keep the observation separate from the forecast. #Research", minutes: -50, collection: NoteCollection.EconomicAnalysis);
        return store;

        void Add(string text, int minutes, List<NoteImage>? images = null, NoteCollection collection = NoteCollection.Notes)
        {
            var note = MarkdownText.Parse(text);
            note.Images = images ?? [];
            var created = time.AddMinutes(minutes);
            store.Upsert(new CardDocument { CreatedAt = created, UpdatedAt = created, Collection = collection, Note = note });
        }
    }

    private static void RenderScrollFrames(MainWindow window, string directory)
    {
        var frames = Path.Combine(directory, "frames");
        Directory.CreateDirectory(frames);
        CaptureRoot(window, ReadingHeight, scale: 1);
        var feed = Get<FeedView>(window, "NotesFeed");
        var scroll = Descendants(feed).OfType<ScrollViewer>().Single(view => AutomationProperties.GetAutomationId(view) == "NotesFeed");
        var maximum = scroll.ScrollableHeight;
        if (maximum < 500) throw new InvalidOperationException("The showcase must demonstrate an actual overflowing feed.");
        var manifest = new List<object>();
        const int count = 30;
        for (var index = 0; index < count; index++)
        {
            var position = index / (double)(count - 1);
            var eased = (1 - Math.Cos(Math.PI * position)) / 2;
            scroll.ScrollToVerticalOffset(maximum * eased);
            window.UpdateLayout();
            var name = $"feed-{index:000}.png";
            Save(CaptureRoot(window, ReadingHeight, scale: 2), Path.Combine(frames, name));
            manifest.Add(new { file = name, durationMs = index == 0 ? 1400 : index == count - 1 ? 1700 : 130,
                offset = scroll.VerticalOffset });
        }
        File.WriteAllText(Path.Combine(directory, "animation.json"), JsonSerializer.Serialize(new
        {
            width = ColumnWidth, height = ReadingHeight, sourceScale = 2, repeat = 0, frames = manifest
        }, new JsonSerializerOptions { WriteIndented = true }));
        scroll.ScrollToTop();
    }

    private static BitmapSource ThroughLast(MainWindow window, int maximumHeight)
    {
        var feed = Get<FeedView>(window, "NotesFeed");
        return ThroughRow(window, feed.RenderedCards.Last(), maximumHeight);
    }

    private static BitmapSource ThroughRow(MainWindow window, FeedNoteCard row, int maximumHeight)
    {
        CaptureRoot(window, maximumHeight, scale: 2);
        var root = Get<Grid>(window, "Root");
        var bottom = row.TransformToAncestor(root).Transform(new Point(0, row.ActualHeight)).Y;
        var height = Math.Min(maximumHeight, (int)Math.Ceiling(bottom + 12));
        return CaptureRoot(window, height, scale: 2);
    }

    private static BitmapSource CaptureRoot(MainWindow window, int height, int scale)
    {
        var root = Get<Grid>(window, "Root");
        root.Width = ColumnWidth;
        root.Height = height;
        root.Measure(new Size(ColumnWidth, height));
        root.Arrange(new Rect(0, 0, ColumnWidth, height));
        root.UpdateLayout();
        var image = new RenderTargetBitmap(ColumnWidth * scale, height * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(root);
        image.Freeze();
        return image;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WithWindow(LibraryStore store, Action<MainWindow> render)
    {
        var window = new MainWindow(store);
        try { render(window); }
        finally { window.Close(); }
    }
    private static T Get<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static object? Invoke(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
