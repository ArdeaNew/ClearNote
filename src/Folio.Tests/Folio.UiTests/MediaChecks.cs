using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Offscreen controls and synthetic image data; never opens a picker or reads the system clipboard.</summary>
public static class MediaChecks
{
    public static void Run(string? testRoot = null)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var root = Path.Combine(testRoot ?? Path.GetTempPath(), "FolioNote.MediaChecks-" + Guid.NewGuid().ToString("N"));
            var store = new LibraryStore(Path.Combine(root, "Data"));
            store.Load();
            var first = ImportAndReload(store);
            PanelAndPaste(store, first);
            InlineSaveAndCancel(store, first);
            MissingImageKeepsReference(store, first);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static NoteImage ImportAndReload(LibraryStore store)
    {
        var source = Picture(96, 64);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var jpeg = new MemoryStream();
        encoder.Save(jpeg);
        var image = ImageAttachments.ImportBytes(jpeg.ToArray(), store);
        var path = store.GetImagePath(image);
        Assert(image.PixelWidth == 96 && image.PixelHeight == 64 && path.EndsWith(".jpg", StringComparison.Ordinal) &&
               File.ReadAllBytes(path).SequenceEqual(jpeg.ToArray()),
            "Portable JPEG images must retain their original encoded copy and full dimensions without needless PNG conversion.");
        var preview = ImageAttachments.LoadPreview(path, 40);
        Assert(preview.IsFrozen && preview.PixelWidth <= 40 && preview.PixelHeight > 0,
            "Display images must decode to a bounded frozen thumbnail.");
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(exclusive.Length > 0, "A loaded preview must release its asset file.");
        var note = new CardDocument { Note = new NoteContent { Images = [image] } };
        Assert(NoteText.HasContent(note), "An image-only note is valid postable content.");
        store.Upsert(note);
        var draft = new ComposerDraft { Content = new NoteContent { Images = [image] } };
        store.SaveDraft(draft);
        var reopened = new LibraryStore(store.RootPath);
        reopened.Load();
        Assert(reopened.Cards.Single().Note!.Images.Single().AssetName == image.AssetName &&
               reopened.LoadDraft()!.Content.Images.Single().AssetName == image.AssetName,
            "Saved note images and an independent image-only draft must survive reopening.");
        return image;
    }

    private static void PanelAndPaste(LibraryStore store, NoteImage image)
    {
        var panel = new AttachmentPanel(store);
        var changes = 0;
        panel.Changed += (_, _) => changes++;
        panel.Load([image], editable: false);
        Layout(panel, 540);
        Assert(panel.HasImages && panel.ActualHeight <= 318 && Descendants(panel).OfType<Image>().Any(),
            "Read-only attachments must show a compact picture beneath the text.");
        Assert(!Descendants(panel).OfType<Button>().Any(button => AutomationProperties.GetName(button).StartsWith("Remove image", StringComparison.Ordinal)),
            "Saved attachments must not show removal controls.");
        panel.Load([image], editable: true);
        var snapshot = panel.Capture();
        snapshot[0].AssetName = "changed only in captured copy";
        Assert(panel.Capture()[0].AssetName == image.AssetName, "Capturing attachments must not expose the live image list.");

        var bitmapData = new DataObject(DataFormats.Bitmap, Picture(48, 32));
        Assert(ImageAttachments.TryHandlePaste(bitmapData, store, out var pasted, 3) && pasted.Count == 1,
            "Synthetic bitmap paste data must import without consulting the clipboard.");
        panel.AddImages(pasted);
        Layout(panel, 540);
        Assert(panel.Count == 2 && changes == 1 && panel.ActualHeight <= 195, "Draft images must use a compact preview row and notify draft saving.");
        var remove = Descendants(panel).OfType<Button>().First(button => AutomationProperties.GetName(button) == "Remove image 2");
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(panel.Count == 1 && changes == 2, "Removing a draft attachment must notify the save layer.");
        panel.AddImages([image, image, image]);
        Throws<InvalidDataException>(() => panel.AddImages([image]), "A fifth image must be rejected without changing the existing draft.");
        Assert(panel.Count == 4, "An over-limit image must leave all existing attachments intact.");
        var editor = new NoteEditor();
        editor.Load(new CardDocument { Note = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "Preserve this selection" }] }] } }, true);
        editor.TextBox.SelectAll();
        var rejected = 0;
        editor.InputRejected += _ => rejected++;
        editor.AttachmentPasteHandler = data =>
        {
            if (!ImageAttachments.TryHandlePaste(data, store, out var images, 0)) return false;
            panel.AddImages(images);
            return true;
        };
        var fullPaste = new DataObjectPastingEventArgs(bitmapData, false, DataFormats.Bitmap) { RoutedEvent = DataObject.PastingEvent };
        editor.TextBox.RaiseEvent(fullPaste);
        Assert(fullPaste.CommandCancelled && rejected == 1 && panel.Count == 4 && editor.PlainText.Trim() == "Preserve this selection",
            "The actual fifth-image paste boundary must report a nonfatal rejection and retain the selected text and all images.");

        var bytes = new DataObject(DataFormats.FileDrop, new[] { store.GetImagePath(image) });
        Assert(ImageAttachments.TryHandlePaste(bytes, store, out var files, 1) && files.Count == 1,
            "Pasted image files must use the same safe import path as selected files.");
        Throws<InvalidDataException>(() => ImageAttachments.TryHandlePaste(bitmapData, store, out _, 0),
            "Pasting into a full image panel must fail before changing it.");
        Assert(!ImageAttachments.TryHandlePaste(new DataObject(DataFormats.UnicodeText, "ordinary text"), store, out _),
            "Ordinary text must remain available to the native text paste handler.");
    }

    private static void InlineSaveAndCancel(LibraryStore store, NoteImage original)
    {
        var document = new CardDocument
        {
            Note = new NoteContent { Images = [original], Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "Original text" }] }] }
        };
        var feed = new FeedView { Store = store };
        feed.Reset(_ => new[] { document }, false);
        Layout(feed, 600);
        var card = feed.RenderedCards.Single();
        Assert(card.Attachments is { HasImages: true }, "A feed must pass its image store to rendered note cards.");
        card.BeginEdit();
        var captured = 0;
        var importErrors = new List<Exception>();
        card.InputRejected += importErrors.Add;
        feed.SaveNote = (_, content) => { captured = content.Images.Count; return false; };
        var pasted = new DataObject(DataFormats.Bitmap, Picture(32, 24));
        var args = new DataObjectPastingEventArgs(pasted, false, DataFormats.Bitmap) { RoutedEvent = DataObject.PastingEvent };
        card.Body.TextBox.RaiseEvent(args);
        Assert(args.CommandCancelled && card.Body.PlainText.Trim() == "Original text",
            "An image paste must consume the native paste event without replacing the existing text.");
        if (card.IsImportingImages)
            Assert(!card.TrySave() && !feed.SavePendingEdits() && captured == 0,
                "An outstanding paste import must block both inline and feed-wide saving before invoking persistence.");
        WaitForImport(card.Attachments!);
        Assert(importErrors.Count == 0 && card.CaptureEdit().Images.Count == 2,
            "The dispatched paste continuation must attach its picture without an input failure.");
        Assert(!card.TrySave() && card.IsEditing && card.CaptureEdit().Images.Count == 2 && captured == 2,
            "A rejected save must preserve the image draft and pass attachments to the persistence callback.");
        Assert(document.Note!.Images.Count == 1, "Rejected inline persistence must retain the saved images.");
        card.CancelEdit();
        Assert(!card.IsEditing && card.Attachments!.Count == 1 && document.Note.Images.Count == 1,
            "Cancel must restore saved images together with the saved text.");
        card.BeginEdit();
        var acceptedPaste = new DataObjectPastingEventArgs(pasted, false, DataFormats.Bitmap) { RoutedEvent = DataObject.PastingEvent };
        card.Body.TextBox.RaiseEvent(acceptedPaste);
        WaitForImport(card.Attachments!);
        feed.SaveNote = (_, _) => true;
        Assert(acceptedPaste.CommandCancelled && importErrors.Count == 0 && card.TrySave() && !card.IsEditing && document.Note.Images.Count == 2 && card.Attachments!.Count == 2,
            "An accepted inline save must show its persisted attachments in read-only mode.");

        var storeless = new FeedNoteCard(document);
        storeless.BeginEdit();
        storeless.Body.PlainText = "Text-only caller still preserves saved pictures";
        Assert(storeless.CaptureEdit().Images.Count == 2, "A caller without an image store must retain image references when editing text.");
    }

    private static void MissingImageKeepsReference(LibraryStore store, NoteImage original)
    {
        var missing = new NoteImage { AssetName = new string('a', 64) + ".png", PixelWidth = original.PixelWidth, PixelHeight = original.PixelHeight };
        var panel = new AttachmentPanel(store);
        panel.Load([missing], editable: false);
        Layout(panel, 540);
        Assert(panel.HasImages && panel.Capture().Single().AssetName == missing.AssetName &&
               Descendants(panel).OfType<TextBlock>().Any(text => text.Text == "Image unavailable"),
            "An unavailable image must retain its reference and show a recoverable placeholder.");
    }

    private static BitmapSource Picture(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        { pixels[index] = 190; pixels[index + 1] = 160; pixels[index + 2] = 110; pixels[index + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
    }

    private static void WaitForImport(AttachmentPanel panel)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timedOut = false;
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) => { timedOut = true; frame.Continue = false; };
        EventHandler completed = (_, _) => { if (!panel.IsImporting) frame.Continue = false; };
        panel.ImportingChanged += completed;
        try
        {
            timeout.Start();
            if (panel.IsImporting) Dispatcher.PushFrame(frame);
        }
        finally { timeout.Stop(); panel.ImportingChanged -= completed; }
        Assert(!timedOut && !panel.IsImporting, "Image paste must finish its UI continuation within the bounded dispatcher wait.");
        var idle = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => idle.Continue = false));
        Dispatcher.PushFrame(idle);
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

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
