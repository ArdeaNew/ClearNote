using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Responsive image proportions, navigation and editing with synthetic local pictures.</summary>
public static class GalleryChecks
{
    public static void Run(string? testRoot = null)
    {
        var root = Path.Combine(testRoot ?? Path.GetTempPath(), "ClearNote.GalleryChecks-" + Guid.NewGuid().ToString("N"));
        var store = new LibraryStore(Path.Combine(root, "Data"));
        store.Load();
        var landscape = ImageAttachments.ImportBitmap(Picture(1200, 800), store);
        var portrait = ImageAttachments.ImportBitmap(Picture(900, 1200), store);
        CompactImagesAndNavigation(store, landscape, portrait);
        PortraitsFillTheirFrames(store, portrait);
        SingleImageProportions(store, landscape, portrait);
        HeightsDoNotDependOnImageCount(store, landscape, portrait);
        PreviewBoundsAcrossColumnSizes(store, landscape, portrait);
        EditableGallery(store, landscape, portrait);
        VerticalWheelPassesToFeed(store, landscape, portrait);
        MissingImageKeepsItsPlace(store, landscape);
    }

    private static void CompactImagesAndNavigation(LibraryStore store, NoteImage landscape, NoteImage portrait)
    {
        var panel = new AttachmentPanel(store);
        panel.Load([landscape, portrait, landscape, portrait], editable: false);
        Layout(panel, 540);
        var frames = Frames(panel);
        var scroll = Descendants(panel).OfType<ScrollViewer>().Single();
        Assert(frames.Length == 4 && Close(frames[0].ActualWidth, 462) && Close(frames[1].ActualWidth, 231) &&
               frames.All(frame => Close(frame.ActualHeight, 308)),
            "Mixed galleries must align full-height landscape and portrait photographs without cropping or letterbox bands.");
        Assert(scroll.ScrollableWidth > 800,
            "A larger collection must remain horizontally browsable at the consistent picture height.");
        Assert(frames.All(frame => frame.Child is Image { Stretch: Stretch.Uniform }),
            "Every gallery preview must preserve the entire picture without cropping its pixels.");
        var next = Button(panel, "Next image");
        var previous = Button(panel, "Previous image");
        Assert(next.Visibility == Visibility.Visible && previous.Visibility == Visibility.Collapsed,
            "Only a useful next-image arrow must appear at the start of a gallery.");
        Click(next); panel.UpdateLayout();
        Assert(scroll.HorizontalOffset > 0 && previous.Visibility == Visibility.Visible && next.Visibility == Visibility.Visible,
            "Next must advance through compact pictures and offer both useful directions in the middle of a gallery.");
        PressKey(panel, Key.End); panel.UpdateLayout();
        Assert(Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < 1 && next.Visibility == Visibility.Collapsed,
            "The last photograph must be reachable without leaving a redundant next-image control.");
        Click(previous); panel.UpdateLayout();
        Assert(scroll.HorizontalOffset < scroll.ScrollableWidth, "Previous must leave the final image and move back through the gallery.");
        PressKey(panel, Key.Home); panel.UpdateLayout();
        Assert(scroll.HorizontalOffset < 1, "Home must return to the first image.");

        Layout(panel, 602);
        frames = Frames(panel);
        Assert(frames.All(frame => Close(frame.ActualHeight, 308)) && Close(frames[0].ActualWidth, 462),
            "Widening the reading column must retain the same photograph sizes and aligned height.");
        PressKey(panel, Key.Right); panel.UpdateLayout();
        Assert(scroll.HorizontalOffset > 0, "The focused gallery must support right-arrow navigation.");
        PressKey(panel, Key.Home); panel.UpdateLayout();
        Assert(scroll.HorizontalOffset < 1, "Home must return to the start without changing the note.");
        Assert(panel.Capture().Select(image => image.AssetName).SequenceEqual(new[]
                   { landscape.AssetName, portrait.AssetName, landscape.AssetName, portrait.AssetName }) &&
               File.Exists(store.GetImagePath(landscape)) && File.Exists(store.GetImagePath(portrait)),
            "Resizing and gallery navigation must preserve image order, references and local files.");
    }

    private static void PortraitsFillTheirFrames(LibraryStore store, NoteImage portrait)
    {
        var panel = new AttachmentPanel(store);
        panel.Load([portrait, portrait], editable: false);
        Layout(panel, 602);
        var frames = Frames(panel);
        var scroll = Descendants(panel).OfType<ScrollViewer>().Single();
        Assert(frames.All(frame => Math.Abs(frame.ActualWidth / frame.ActualHeight - 0.75) < 0.01 &&
                                   Close(frame.ActualHeight, 308) && Close(frame.ActualWidth, 231)),
            "Portraits must grow by ten percent from 210 by 280 to 231 by 308, without letterbox sidebars.");
        Assert(scroll.ScrollableWidth < 1 && Descendants(panel).OfType<Button>().All(button => button.Visibility == Visibility.Collapsed),
            "Two compact portraits that fit the column must appear together without unnecessary gallery arrows.");
        Assert(frames.All(frame => frame.Child is Image { Stretch: Stretch.Uniform }),
            "Portrait previews must preserve every source pixel while filling their natural-ratio frames.");
    }

    private static void SingleImageProportions(LibraryStore store, NoteImage landscape, NoteImage portrait)
    {
        var panel = new AttachmentPanel(store);
        panel.Load([landscape], editable: false);
        Layout(panel, 602);
        var frame = Frames(panel).Single();
        Assert(Math.Abs(frame.ActualWidth / frame.ActualHeight - 1.5) < 0.01 && Close(frame.ActualWidth, 462) && Close(frame.ActualHeight, 308),
            "A single landscape must use the same full image height as a portrait and preserve its natural aspect.");
        Assert(Descendants(panel).OfType<Button>().All(button => button.Visibility == Visibility.Collapsed),
            "A single picture must not display unnecessary navigation.");
        panel.Load([portrait], editable: false);
        Layout(panel, 602);
        frame = Frames(panel).Single();
        Assert(Close(frame.ActualHeight, 308) && Math.Abs(frame.ActualWidth / frame.ActualHeight - 0.75) < 0.01,
            "A single portrait must retain its full aspect and match the shared feed image height.");
    }

    private static void HeightsDoNotDependOnImageCount(LibraryStore store, NoteImage landscape, NoteImage portrait)
    {
        foreach (var editable in new[] { false, true })
        {
            var height = editable ? 184.8 : 308;
            var panel = new AttachmentPanel(store);
            for (var count = 1; count <= 4; count++)
            {
                panel.Load(Enumerable.Range(0, count).Select(index => index % 2 == 0 ? landscape : portrait), editable);
                Layout(panel, 540);
                Assert(Frames(panel).Length == count && Frames(panel).All(frame => Close(frame.ActualHeight, height)) &&
                       Close(Frames(panel)[0].ActualWidth, height * 1.5),
                    "Adding a second, third or fourth image must not resize an existing photograph or change the shared row height.");
            }
        }

        var square = ImageAttachments.ImportBitmap(Picture(800, 800), store);
        var squarePanel = new AttachmentPanel(store);
        squarePanel.Load([square], editable: false);
        Layout(squarePanel, 540);
        Assert(Close(Frames(squarePanel).Single().ActualWidth, 308),
            "A square preview must grow ten percent from its former height-limited 280-pixel side.");
    }

    private static void PreviewBoundsAcrossColumnSizes(LibraryStore store, NoteImage landscape, NoteImage portrait)
    {
        var wide = ImageAttachments.ImportBitmap(Picture(1024, 32), store);
        var tall = ImageAttachments.ImportBitmap(Picture(32, 1024), store);
        var small = ImageAttachments.ImportBitmap(Picture(32, 24), store);
        foreach (var image in new[] { landscape, portrait, wide, tall, small })
        {
            var panel = new AttachmentPanel(store);
            panel.Load([image], editable: false);
            foreach (var width in new[] { 160d, 480, 602, 2048 })
            {
                Layout(panel, width);
                var frame = Frames(panel).Single();
                var ratio = image.PixelWidth / (double)image.PixelHeight;
                Assert(frame.ActualWidth > 0 && Close(frame.ActualHeight, 308) &&
                       Math.Abs(frame.ActualWidth / frame.ActualHeight - ratio) < 0.01,
                    "Narrow and wide columns must retain the same image height and whole natural aspect, even for small or extreme pictures.");
                var picture = (Image)frame.Child;
                Assert(picture.Stretch == Stretch.Uniform,
                    "A shared row height may enlarge a small source but must never stretch or crop its aspect ratio.");
            }
        }

        var gallery = new AttachmentPanel(store);
        gallery.Load([landscape, portrait, wide, tall], editable: false);
        Layout(gallery, 160);
        var scroll = Descendants(gallery).OfType<ScrollViewer>().Single();
        Assert(Frames(gallery).All(frame => Close(frame.ActualHeight, 308)) && scroll.ScrollableWidth > 1000,
            "A narrow gallery must scroll horizontally rather than shrink the consistent picture height.");
        PressKey(gallery, Key.End); gallery.UpdateLayout();
        Assert(scroll.HorizontalOffset > 0 && Button(gallery, "Next image").Visibility == Visibility.Collapsed,
            "The last picture must remain reachable after shrinking the gallery.");
        gallery.Load([landscape, portrait, landscape, portrait], editable: false);
        Layout(gallery, 2048);
        Assert(scroll.ScrollableWidth < 1 && scroll.HorizontalOffset < 1 &&
               Descendants(gallery).OfType<Button>().All(button => button.Visibility == Visibility.Collapsed),
            "Growing a column until every picture fits must clear obsolete scroll offsets and navigation.");

        gallery.Load([wide], editable: false);
        Layout(gallery, 480);
        Assert(Button(gallery, "Next image").Visibility == Visibility.Visible,
            "A single panorama must expose useful navigation instead of leaving its overflow inaccessible.");
        Click(Button(gallery, "Next image")); gallery.UpdateLayout();
        Assert(scroll.HorizontalOffset > 0 && scroll.HorizontalOffset < scroll.ScrollableWidth,
            "A panorama must advance one overlapping viewport at a time so its middle can be viewed too.");
        PressKey(gallery, Key.End); gallery.UpdateLayout();
        Assert(Close(scroll.HorizontalOffset, scroll.ScrollableWidth) && Button(gallery, "Next image").Visibility == Visibility.Collapsed,
            "A single panorama must support keyboard navigation all the way to its end.");
    }

    private static void EditableGallery(LibraryStore store, NoteImage landscape, NoteImage portrait)
    {
        var panel = new AttachmentPanel(store);
        panel.Load([landscape, portrait, landscape, portrait], editable: true);
        var changes = 0;
        panel.Changed += (_, _) => changes++;
        Layout(panel, 540);
        Assert(Frames(panel).All(frame => Close(frame.ActualHeight, 184.8)) && panel.ActualHeight <= 195,
            "Editing must align all previews at a shared height ten percent above the former 168-pixel draft row.");
        Click(Button(panel, "Remove image 2"));
        Layout(panel, 540);
        Assert(panel.Count == 3 && changes == 1 && panel.Capture()[1].AssetName == landscape.AssetName,
            "A removal control must remove its own image, preserve order and notify draft persistence once.");
        Assert(File.Exists(store.GetImagePath(portrait)),
            "Removing an unsaved attachment must leave physical asset ownership to the persistence layer.");
    }

    private static void VerticalWheelPassesToFeed(LibraryStore store, NoteImage landscape, NoteImage portrait)
    {
        var panel = new AttachmentPanel(store);
        panel.Load([landscape, portrait], editable: true);
        var content = new StackPanel();
        content.Children.Add(new Border { Height = 200 });
        content.Children.Add(panel);
        content.Children.Add(new Border { Height = 1200 });
        var feed = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        feed.Measure(new Size(600, 500));
        feed.Arrange(new Rect(0, 0, 600, 500));
        feed.UpdateLayout();
        feed.ScrollToVerticalOffset(150); feed.UpdateLayout();
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        panel.RaiseEvent(wheel); feed.UpdateLayout();
        Assert(wheel.Handled && feed.VerticalOffset > 150,
            "An ordinary wheel over attachments must scroll the surrounding feed, including while editing.");
    }

    private static void MissingImageKeepsItsPlace(LibraryStore store, NoteImage landscape)
    {
        var missing = new NoteImage { AssetName = new string('b', 64) + ".png", PixelWidth = 1200, PixelHeight = 800 };
        var panel = new AttachmentPanel(store);
        panel.Load([landscape, missing], editable: false);
        Layout(panel, 540);
        Assert(Frames(panel).Length == 2 && Descendants(panel).OfType<TextBlock>().Any(text => text.Text == "Image unavailable") &&
               panel.Capture()[1].AssetName == missing.AssetName,
            "A missing picture must retain its reference and gallery position with a readable placeholder.");
    }

    private static Border[] Frames(DependencyObject root) => Descendants(root).OfType<Border>()
        .Where(frame => AutomationProperties.GetAutomationId(frame).StartsWith("AttachmentFrame-", StringComparison.Ordinal)).ToArray();
    private static Button Button(DependencyObject root, string name) => Descendants(root).OfType<Button>()
        .Single(button => AutomationProperties.GetName(button) == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, button));
    private static void PressKey(AttachmentPanel panel, Key key) => panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        new MemorySource(panel), Environment.TickCount, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });
    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
    }
    private static BitmapSource Picture(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        { pixels[index] = 190; pixels[index + 1] = 160; pixels[index + 2] = 110; pixels[index + 3] = 255; }
        var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        picture.Freeze();
        return picture;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) < 0.1;
    private sealed class MemorySource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
