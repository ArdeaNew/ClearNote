using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Documentation figures made from application controls; no desktop capture or user library is used.</summary>
internal static class GuideRenderer
{
    private const double ColumnWidth = 662;
    private const int Scale = 2;

    public static void Render(string dataRoot, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var store = Prepare(Path.Combine(dataRoot, "usage-guide", "Log", "Data"));
        var overview = new MainWindow(store);
        try { Save(ThroughNote(overview, 2, 930), outputDirectory, "overview"); }
        finally { overview.Close(); }

        WithWindow(store, window =>
        {
            Get<NoteEditor>(window, "Composer").InsertMarkdown("Keep one clear thought.\nReturn to it when it matters. #Learning");
            Save(Focus(window, Get<Border>(window, "ComposerRegion")), outputDirectory, "compose");
        });

        WithWindow(store, window =>
        {
            var row = First(window);
            row.BeginEdit();
            row.Body.Load(new CardDocument { Note = Formatting() }, editing: true);
            Save(Focus(window, row), outputDirectory, "formatting");
            row.CancelEdit();
        });

        WithWindow(store, window =>
        {
            var row = First(window);
            row.BeginEdit();
            row.Body.TextBox.CaretPosition = row.Body.TextBox.Document.ContentEnd;
            row.Body.InsertMarkdown("\nOne extra detail, without leaving the feed.");
            Save(Focus(window, row), outputDirectory, "edit");
            row.CancelEdit();
        });

        WithWindow(store, window =>
        {
            var editor = Get<NoteEditor>(window, "Composer");
            editor.InsertMarkdown("#L");
            var suggestions = Field<object>(window, "_tagSuggestions");
            Invoke(suggestions, "Update", editor.TextBox, new RoutedEventArgs());
            var popup = Field<Popup>(suggestions, "_popup");
            popup.IsOpen = false;
            var list = Field<ListBox>(suggestions, "_list");
            if (list.Items.Count == 0) throw new InvalidOperationException("The tag example must use actual matching suggestions.");
            var region = Get<Border>(window, "ComposerRegion");
            var composer = Focus(window, region);
            var caret = editor.TextBox.CaretPosition.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
            var origin = editor.TextBox.TransformToAncestor(region).Transform(new Point());
            var x = origin.X + Math.Clamp(caret.Left, 0, Math.Max(0, editor.TextBox.ActualWidth - 200));
            Save(ClipWidth(Overlay(composer, CaptureNatural((FrameworkElement)popup.Child, 190), x, origin.Y + caret.Bottom + 5), 320), outputDirectory, "tag-suggestions");
        });

        WithWindow(store, window =>
        {
            // AddReviewTag supplies the real application menu rows without opening a native Popup.
            var menu = new ContextMenu { FontFamily = AppTypography.Text, MinWidth = 180 };
            Invoke(window, "AddReviewTag", menu, "All tags", null);
            foreach (var tag in store.Cards.Where(card => card.Collection == NoteCollection.Notes)
                .SelectMany(NoteTags.Read).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag))
                Invoke(window, "AddReviewTag", menu, "#" + tag, tag);
            var root = CaptureRoot(window, ColumnWidth, 600);
            var header = Crop(root, new Rect(0, 0, ColumnWidth, 52));
            var dropdown = CaptureNatural(menu, 206);
            Save(ClipWidth(Overlay(header, dropdown, 88, 55), 320), outputDirectory, "review-menu");
        });

        WithWindow(store, window =>
        {
            Invoke(window, "SelectReviewTag", "Learning");
            Save(ThroughNote(window, 1, 620), outputDirectory, "review");
        });

        WithWindow(store, window =>
        {
            Get<Button>(window, "MemoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Save(ThroughNote(window, 1, 570), outputDirectory, "memo");
        });

        WithWindow(store, window =>
        {
            Get<TextBox>(window, "SearchBox").Text = "recall";
            var root = CaptureRoot(window, ColumnWidth, 490);
            var popup = Get<Popup>(window, "SearchPopup");
            popup.IsOpen = false;
            var field = CaptureNatural((FrameworkElement)popup.Child, 260);
            Save(Overlay(root, field, ColumnWidth - 290, 51), outputDirectory, "search");
        });

        WithWindow(store, window =>
        {
            Get<TextBox>(window, "SearchBox").Text = "#Places";
            var row = First(window);
            Save(Focus(window, row), outputDirectory, "gallery");
        });

        WithWindow(store, window =>
        {
            var row = First(window);
            var note = Focus(window, row);
            var header = Crop(note, new Rect(0, 0, note.PixelWidth / (double)Scale, 62));
            var avatar = Descendants(row).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Edit profile");
            var menu = avatar.ContextMenu ?? throw new InvalidOperationException("The real avatar menu is missing.");
            Save(ClipWidth(Overlay(header, CaptureNatural(menu, 204), 20, 65), 320), outputDirectory, "avatar-menu");
        });

        var dialog = (Window)Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("Folio.App.ProfileDialog", true)!,
            store, store.LoadProfile(), (Func<UserProfile, bool>)(_ => false))!;
        try { Save(CaptureNatural((FrameworkElement)dialog.Content, 380), outputDirectory, "profile"); }
        finally { dialog.Close(); }

        WithWindow(store, window =>
        {
            var row = First(window);
            var normal = Focus(window, row);
            // Reproduce the existing hover paint offscreen; no pointer input is synthesized.
            var shell = (Border)row.Content;
            shell.SetCurrentValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(247, 247, 247)));
            var hovered = Focus(window, row);
            Save(Stack(normal, hovered, "At rest", "On hover"), outputDirectory, "hover");
            shell.ClearValue(Border.BackgroundProperty);
        });

        WithWindow(store, window =>
        {
            var area = new Rect(0, 0, 1800, 1100);
            while (window.Width > 600 && (bool)Invoke(window, "TryAdjustWidth", false, area)!) { }
            var narrower = Focus(window, First(window), window.Width - 16);
            for (var i = 0; i < 5; i++) Invoke(window, "TryAdjustWidth", true, area);
            var wider = Focus(window, First(window), window.Width - 16);
            Save(Stack(narrower, wider, "Narrower", "Wider"), outputDirectory, "width");
        });
        Console.WriteLine("Rendered 14 usage-guide figures from application controls: " + outputDirectory);
    }

    private static LibraryStore Prepare(string path)
    {
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("The documentation library must be a new empty directory.");
        var store = new LibraryStore(path);
        store.Load();
        var time = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(8));
        store.Upsert(Card(time, MarkdownText.Parse("**Recall before rereading.**\nClose the book. Explain the idea in your own words. #Learning")));
        store.Upsert(Card(time.AddMinutes(-1), MarkdownText.Parse("1. Recall the main idea.\n2. Check the missing detail.\n3. Return a little later. #Learning #Lists")));
        using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("Folio.App.Assets.WelcomeLandscape")!;
        var landscape = BitmapDecoder.Create(resource, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        var photos = new[]
        {
            ImageAttachments.ImportBitmap(landscape, store),
            ImageAttachments.ImportBitmap(new CroppedBitmap(landscape, new Int32Rect(80, 0, 800, 1024)), store),
            ImageAttachments.ImportBitmap(new CroppedBitmap(landscape, new Int32Rect(700, 0, 800, 1024)), store)
        };
        var gallery = MarkdownText.Parse("A little room to look around. #Places");
        gallery.Images = photos.ToList();
        store.Upsert(Card(time.AddMinutes(-2), gallery));
        store.Upsert(Card(time.AddMinutes(-3), MarkdownText.Parse("Leave room for what matters.\nA clean page, a quiet place to think. #Ideas")));
        store.Upsert(Card(time, MarkdownText.Parse("Library hours\nMonday to Saturday, 10:00-18:00. #Everyday"), NoteCollection.Memo));
        store.Upsert(Card(time.AddMinutes(-1), MarkdownText.Parse("Things to bring\nNotebook, water, library card. #Everyday"), NoteCollection.Memo));
        return store;
    }

    private static NoteContent Formatting() => new()
    {
        Paragraphs =
        [
            new NoteParagraph { Kind = "heading", HeadingLevel = 2, Runs = [new NoteRun { Text = "Keep the useful part." }] },
            new NoteParagraph { Runs = [new NoteRun { Text = "A clear idea", Bold = true }, new NoteRun { Text = ", in your own words." }] },
            new NoteParagraph { Runs = [new NoteRun { Text = "One detail worth keeping.", Background = "#EEEEEE" }] },
            new NoteParagraph { Kind = "ordered", Number = 1, Runs = [new NoteRun { Text = "Recall the idea." }] },
            new NoteParagraph { Kind = "ordered", Number = 2, Runs = [new NoteRun { Text = "Check the gap." }] },
            new NoteParagraph { Runs = [new NoteRun { Text = "A quiet accent. #Learning", Foreground = "#356A7A", Italic = true }] }
        ]
    };

    private static CardDocument Card(DateTimeOffset created, NoteContent content, NoteCollection collection = NoteCollection.Notes) =>
        new() { CreatedAt = created, UpdatedAt = created, Collection = collection, Note = content };

    private static void WithWindow(LibraryStore store, Action<MainWindow> render)
    {
        var window = new MainWindow(store);
        try
        {
            Get<NoteEditor>(window, "Composer").Load(new CardDocument(), editing: true);
            render(window);
        }
        finally { window.Close(); }
    }

    private static FeedNoteCard First(MainWindow window) => Get<FeedView>(window, "NotesFeed").RenderedCards.First();
    private static T Get<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static T Field<T>(object instance, string name) => (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new MissingFieldException(instance.GetType().Name, name));
    private static object? Invoke(object instance, string name, params object?[] arguments) =>
        (instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().Name, name)).Invoke(instance, arguments);

    private static BitmapSource CaptureRoot(MainWindow window, double width, double height) => Capture(Get<Grid>(window, "Root"), width, height);

    private static BitmapSource ThroughNote(MainWindow window, int index, double height)
    {
        var root = Get<Grid>(window, "Root");
        var bitmap = CaptureRoot(window, ColumnWidth, height);
        var row = Get<FeedView>(window, "NotesFeed").RenderedCards[index];
        var end = row.TransformToAncestor(root).Transform(new Point(0, row.ActualHeight)).Y;
        return Crop(bitmap, new Rect(0, 0, ColumnWidth, end + 12));
    }

    private static BitmapSource ClipWidth(BitmapSource bitmap, double width) =>
        Crop(bitmap, new Rect(0, 0, Math.Min(width, bitmap.PixelWidth / (double)Scale), bitmap.PixelHeight / (double)Scale));

    private static BitmapSource Focus(MainWindow window, FrameworkElement element, double width = ColumnWidth)
    {
        var root = Get<Grid>(window, "Root");
        var bitmap = Capture(root, width, 1100);
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return Crop(bitmap, bounds);
    }

    private static BitmapSource CaptureNatural(FrameworkElement element, double width)
    {
        var margin = element.Margin;
        element.SetCurrentValue(FrameworkElement.MarginProperty, new Thickness(0));
        try
        {
            element.Width = width;
            element.Height = double.NaN;
            element.Measure(new Size(width, double.PositiveInfinity));
            return Capture(element, width, Math.Ceiling(element.DesiredSize.Height));
        }
        finally { element.SetCurrentValue(FrameworkElement.MarginProperty, margin); }
    }

    private static BitmapSource Capture(FrameworkElement element, double width, double height)
    {
        width = Math.Max(1, Math.Ceiling(width));
        height = Math.Max(1, Math.Ceiling(height));
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width * Scale, (int)height * Scale, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Crop(BitmapSource bitmap, Rect bounds)
    {
        var x = Math.Clamp((int)Math.Floor(bounds.X * Scale), 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor(bounds.Y * Scale), 0, bitmap.PixelHeight - 1);
        var width = Math.Clamp((int)Math.Ceiling(bounds.Width * Scale), 1, bitmap.PixelWidth - x);
        var height = Math.Clamp((int)Math.Ceiling(bounds.Height * Scale), 1, bitmap.PixelHeight - y);
        var crop = new CroppedBitmap(bitmap, new Int32Rect(x, y, width, height));
        crop.Freeze();
        return crop;
    }

    private static BitmapSource Overlay(BitmapSource background, BitmapSource popup, double x, double y)
    {
        var width = Math.Max(background.PixelWidth / (double)Scale, x + popup.PixelWidth / (double)Scale + 12);
        var height = Math.Max(background.PixelHeight / (double)Scale, y + popup.PixelHeight / (double)Scale + 12);
        return Draw(width, height, drawing =>
        {
            drawing.DrawImage(background, new Rect(0, 0, background.PixelWidth / (double)Scale, background.PixelHeight / (double)Scale));
            drawing.DrawImage(popup, new Rect(x, y, popup.PixelWidth / (double)Scale, popup.PixelHeight / (double)Scale));
        });
    }

    private static BitmapSource Stack(BitmapSource first, BitmapSource second, string firstLabel, string secondLabel)
    {
        var width = Math.Max(first.PixelWidth, second.PixelWidth) / (double)Scale;
        var firstHeight = first.PixelHeight / (double)Scale;
        var secondHeight = second.PixelHeight / (double)Scale;
        return Draw(width, firstHeight + secondHeight + 72, drawing =>
        {
            Label(drawing, firstLabel, 10, 0);
            drawing.DrawImage(first, new Rect(0, 24, first.PixelWidth / (double)Scale, firstHeight));
            Label(drawing, secondLabel, 10, firstHeight + 36);
            drawing.DrawImage(second, new Rect(0, firstHeight + 60, second.PixelWidth / (double)Scale, secondHeight));
        });
    }

    private static void Label(DrawingContext drawing, string label, double x, double y) => drawing.DrawText(
        new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(AppTypography.Text, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11,
            new SolidColorBrush(Color.FromRgb(83, 100, 113)), 1), new Point(x, y));

    private static BitmapSource Draw(double width, double height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            draw(drawing);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string directory, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
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
}
