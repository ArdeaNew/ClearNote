using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Synthetic controls rendered in memory, without displaying windows or capturing a desktop.</summary>
internal static class PreviewRenderer
{
    public static void RenderFeatures(string dataRoot, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var store = new LibraryStore(Path.Combine(dataRoot, "features", "Log", "Data"));
        store.Load();
        using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("Folio.App.Assets.WelcomeLandscape")!;
        var decoder = BitmapDecoder.Create(resource, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var landscape = decoder.Frames[0];
        var left = ImageAttachments.ImportBitmap(new CroppedBitmap(landscape, new Int32Rect(80, 0, 800, 1024)), store);
        var right = ImageAttachments.ImportBitmap(new CroppedBitmap(landscape, new Int32Rect(700, 0, 800, 1024)), store);
        var created = new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.FromHours(8));
        var gallery = MarkdownText.Parse("A little room to look around. #Places");
        gallery.Images = [left, right];
        store.Upsert(MakeCard(created, gallery));
        store.Upsert(new CardDocument
        {
            Collection = NoteCollection.Memo, CreatedAt = created, UpdatedAt = created,
            Note = MarkdownText.Parse("Studio\n24 Maple Lane, Apt 3. #Home")
        });
        store.Upsert(new CardDocument
        {
            Collection = NoteCollection.Memo, CreatedAt = created.AddMinutes(-1), UpdatedAt = created.AddMinutes(-1),
            Note = MarkdownText.Parse("Library\nRemember the card on Wednesday. #Everyday")
        });
        var window = new MainWindow(store);
        try
        {
            Save(window, Path.Combine(outputDirectory, PreviewName("Gallery")));
            var memo = (Button)window.FindName("MemoButton");
            memo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, memo));
            Save(window, Path.Combine(outputDirectory, PreviewName("Memo")));
        }
        finally { window.Close(); }
        Console.WriteLine("Synthetic gallery and Memo previews rendered: " + outputDirectory);
    }

    public static void PrepareWelcome(string libraryPath, string outputDirectory)
    {
        if (Directory.Exists(libraryPath) && Directory.EnumerateFileSystemEntries(libraryPath).Any())
            throw new IOException("Preparing welcome content requires a new empty library folder.");
        Directory.CreateDirectory(outputDirectory);
        var store = new LibraryStore(libraryPath);
        store.Load();
        var seed = typeof(MainWindow).Assembly.GetType("Folio.App.WelcomeNotes", true)!.GetMethod("EnsureCreated")!;
        if (seed.Invoke(null, [store]) is not true || store.Cards.Count != 2)
            throw new IOException("The initial welcome library could not be prepared.");
        var window = new MainWindow(store);
        try { Save(window, Path.Combine(outputDirectory, PreviewName("Welcome"))); }
        finally { window.Close(); }
        Console.WriteLine("Prepared local welcome library: " + libraryPath);
    }

    public static void RenderEmptyHome(string dataRoot, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var store = new LibraryStore(Path.Combine(dataRoot, "empty-feed", "Log", "Data"));
        store.Load();
        var window = new MainWindow(store);
        try { Save(window, Path.Combine(outputDirectory, PreviewName("Empty"))); }
        finally { window.Close(); }
    }

    public static void Render(string dataRoot, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var store = new LibraryStore(Path.Combine(dataRoot, "feed-preview", "Log", "Data"));
        store.Load();
        store.SaveProfile(new UserProfile { DisplayName = "Alex" });
        var created = new DateTimeOffset(2026, 10, 20, 14, 30, 0, TimeSpan.FromHours(8));
        store.Upsert(MakeCard(created, new NoteContent { Paragraphs = [
            new NoteParagraph { Runs = [new NoteRun { Text = "Make it obvious." }] },
            new NoteParagraph { Runs = [new NoteRun { Text = "Make it easy.", Background = "#EEEEEE" }, new NoteRun { Text = " #Learning", Foreground = "#536471" }] }
        ] }));
        store.Upsert(MakeCard(created.AddDays(-1), MarkdownText.Parse("1. Recall the idea.\n2. Check the gap.\n3. Return a little later. #Learning")));
        var image = ImageAttachments.ImportBitmap(Picture(), store);
        var visualNote = MarkdownText.Parse("Light leaves room for thought. #Design");
        visualNote.Images = [image];
        store.Upsert(MakeCard(created.AddDays(-2), visualNote));
        store.Upsert(MakeCard(created.AddDays(-3), MarkdownText.Parse("雨\n摄影师 #阅读")));
        var window = new MainWindow(store);
        try
        {
            Save(window, Path.Combine(outputDirectory, PreviewName("Feed")));
            var feed = (FeedView)window.FindName("NotesFeed");
            var first = feed.RenderedCards[0];
            var firstShell = (Border)first.Content;
            // Illustrate the hover paint offscreen; no desktop pointer is moved or captured.
            firstShell.SetCurrentValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(247, 247, 247)));
            Save(window, Path.Combine(outputDirectory, PreviewName("Hover")));
            firstShell.ClearValue(Border.BackgroundProperty);
            var composerRegion = (Border)window.FindName("ComposerRegion");
            composerRegion.SetCurrentValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(247, 247, 247)));
            Save(window, Path.Combine(outputDirectory, PreviewName("ComposerHover")));
            composerRegion.ClearValue(Border.BackgroundProperty);
            first.BeginEdit();
            Save(window, Path.Combine(outputDirectory, PreviewName("Edit")));
            first.CancelEdit();
            var profileDialog = (Window)Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("Folio.App.ProfileDialog", true)!,
                store, store.LoadProfile(), (Func<UserProfile, bool>)(_ => false))!;
            try
            {
                var profileBody = (FrameworkElement)profileDialog.Content;
                profileBody.Measure(new Size(380, double.PositiveInfinity));
                RenderControl(profileBody, Path.Combine(outputDirectory, PreviewName("Profile")), 380, (int)Math.Ceiling(profileBody.DesiredSize.Height));
            }
            finally { profileDialog.Close(); }
            var menu = Descendants(feed.RenderedCards[0]).OfType<Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "More note options").ContextMenu!;
            RenderControl(menu, Path.Combine(outputDirectory, PreviewName("Menu")), 180, 80);
            var composer = (NoteEditor)window.FindName("Composer");
            composer.InsertMarkdown("One clear thought. #Learning");
            Save(window, Path.Combine(outputDirectory, PreviewName("Compose")));
            Save(window, Path.Combine(dataRoot, "ClearNote-Short.png"), 662, 420);
            var review = (Button)window.FindName("ReviewButton");
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, review));
            var reviewMenu = review.ContextMenu!;
            reviewMenu.IsOpen = false;
            RenderControl(reviewMenu, Path.Combine(outputDirectory, PreviewName("ReviewMenu")), 200, 164);
            var learning = reviewMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "#Learning"));
            learning.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, learning));
            Save(window, Path.Combine(outputDirectory, PreviewName("Review")));
        }
        finally { window.Close(); }
        Console.WriteLine("Synthetic ClearNote previews rendered: " + outputDirectory);
    }

    private static BitmapSource Picture()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Colors.White), null, new Rect(0, 0, 480, 140));
            var line = new Pen(new SolidColorBrush(Color.FromRgb(135, 158, 163)), 2);
            drawing.DrawRoundedRectangle(Brushes.White, line, new Rect(45, 25, 130, 90), 42, 42);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(231, 238, 235)), null, new Rect(195, 25, 100, 90), 42, 42);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(208, 222, 221)), null, new Rect(315, 25, 120, 90), 42, 42);
        }
        var bitmap = new RenderTargetBitmap(480, 140, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static CardDocument MakeCard(DateTimeOffset created, NoteContent content) =>
        new() { CreatedAt = created, UpdatedAt = created, Note = content };

    private static string PreviewName(string view) =>
        $"ClearNote-{view}-{typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}.png";

    private static void Save(MainWindow window, string path, int width = 662, int height = 754) =>
        RenderControl((Grid)window.FindName("Root"), path, width, height);

    private static void RenderControl(FrameworkElement control, string path, int width, int height)
    {
        control.Width = width;
        control.Height = height;
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
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

}

