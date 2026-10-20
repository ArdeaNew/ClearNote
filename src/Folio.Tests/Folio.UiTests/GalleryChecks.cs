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
        LargeImagesAndNavigation(store, landscape, portrait);
        PortraitsFillTheirFrames(store, portrait);
        SingleImageProportions(store, landscape, portrait);
        EditableGallery(store, landscape, portrait);
        VerticalWheelPassesToFeed(store, landscape, portrait);
        MissingImageKeepsItsPlace(store, landscape);
    }

    private static void LargeImagesAndNavigation(LibraryStore store, NoteImage landscape, NoteImage portrait)
    {
        var panel = new AttachmentPanel(store);
        panel.Load([landscape, portrait], editable: false);
        Layout(panel, 540);
        var frames = Frames(panel);
        var scroll = Descendants(panel).OfType<ScrollViewer>().Single();
        Assert(frames.Length == 2 && frames[0].ActualWidth > 540 * 0.8 && frames[1].ActualWidth > 540 * 0.5,
            "Landscapes must use most of the reading width while a portrait keeps a substantial natural-width preview.");
        Assert(frames.All(frame => frame.ActualHeight <= 440) && scroll.ScrollableWidth > 200,
            "A large multi-image gallery must have a bounded height and real horizontal overflow.");
        Assert(frames.All(frame => frame.Child is Image { Stretch: Stretch.Uniform }),
            "Every gallery preview must preserve the entire picture without cropping its pixels.");
        var next = Button(panel, "Next image");
        var previous = Button(panel, "Previous image");
        Assert(next.Visibility == Visibility.Visible && previous.Visibility == Visibility.Collapsed,
            "Only a useful next-image arrow must appear at the start of a gallery.");
        Click(next); panel.UpdateLayout();
        Assert(scroll.HorizontalOffset > 0 && previous.Visibility == Visibility.Visible && next.Visibility == Visibility.Collapsed,
            "Next must reveal the complete second image and offer a route back.");
        Click(previous); panel.UpdateLayout();
        Assert(scroll.HorizontalOffset < 1, "Previous must return to the first image.");

        Layout(panel, 602);
        frames = Frames(panel);
        Assert(Math.Abs(frames[0].ActualWidth - 602 * 0.86) < 1 && Math.Abs(frames[1].ActualWidth - 330) < 1,
            "A wider column must expand landscapes while retaining the full natural width of height-capped portraits.");
        PressKey(panel, Key.Right); panel.UpdateLayout();
        Assert(scroll.HorizontalOffset > 0, "The focused gallery must support right-arrow navigation.");
        PressKey(panel, Key.Home); panel.UpdateLayout();
        Assert(scroll.HorizontalOffset < 1, "Home must return to the start without changing the note.");
        Assert(panel.Capture().Select(image => image.AssetName).SequenceEqual(new[] { landscape.AssetName, portrait.AssetName }) &&
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
                                   frame.ActualHeight > 430 && frame.ActualWidth > 320),
            "Portraits must fill large, naturally proportioned frames without broad letterbox sidebars.");
        var secondStart = frames[1].TranslatePoint(new Point(), scroll).X;
        Assert(secondStart > 0 && secondStart < scroll.ViewportWidth - 100 && scroll.ScrollableWidth > 1,
            "The initial portrait view must reveal a meaningful part of the next photograph, rather than an empty frame.");
        Assert(frames.All(frame => frame.Child is Image { Stretch: Stretch.Uniform }),
            "Portrait previews must preserve every source pixel while filling their natural-ratio frames.");
    }

    private static void SingleImageProportions(LibraryStore store, NoteImage landscape, NoteImage portrait)
    {
        var panel = new AttachmentPanel(store);
        panel.Load([landscape], editable: false);
        Layout(panel, 602);
        var frame = Frames(panel).Single();
        Assert(Math.Abs(frame.ActualWidth / frame.ActualHeight - 1.5) < 0.01 && frame.ActualWidth > 590,
            "A single landscape photograph must retain its natural aspect while using the reading width.");
        Assert(Descendants(panel).OfType<Button>().All(button => button.Visibility == Visibility.Collapsed),
            "A single picture must not display unnecessary navigation.");
        panel.Load([portrait], editable: false);
        Layout(panel, 602);
        frame = Frames(panel).Single();
        Assert(frame.ActualHeight <= 440 && Math.Abs(frame.ActualWidth / frame.ActualHeight - 0.75) < 0.01,
            "A portrait must retain its full aspect and stay within a sensible feed height.");
    }

    private static void EditableGallery(LibraryStore store, NoteImage landscape, NoteImage portrait)
    {
        var panel = new AttachmentPanel(store);
        panel.Load([landscape, portrait, landscape, portrait], editable: true);
        var changes = 0;
        panel.Changed += (_, _) => changes++;
        Layout(panel, 540);
        Assert(Frames(panel).All(frame => frame.ActualHeight <= 168) && panel.ActualHeight <= 178,
            "Editing must keep image previews compact instead of creating a tall blank composer.");
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
    private sealed class MemorySource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
