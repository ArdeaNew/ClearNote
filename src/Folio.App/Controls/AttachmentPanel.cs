using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Folio.Core;

namespace Folio.App.Controls;

/// <summary>Owned image references displayed as a responsive, uncropped horizontal gallery.</summary>
public sealed class AttachmentPanel : UserControl
{
    private const double Gap = 8;
    private const double PreviewHeight = 308;
    private const double EditablePreviewHeight = 184.8;
    private readonly LibraryStore _store;
    private readonly ScrollViewer _viewport;
    private readonly StackPanel _items;
    private readonly Button _previous;
    private readonly Button _next;
    private List<NoteImage> _images = [];
    private List<Border> _frames = [];
    private bool _editable;

    public event EventHandler? Changed;
    public event EventHandler? ImportingChanged;
    public event Action<Exception>? ImportFailed;
    public bool IsImporting { get; private set; }
    public bool HasImages => _images.Count > 0;
    public int Count => _images.Count;

    public AttachmentPanel(LibraryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        Focusable = true;
        _items = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _viewport = new ScrollViewer
        {
            Content = _items, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false, PanningMode = PanningMode.HorizontalOnly,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top, Focusable = false
        };
        AutomationProperties.SetName(_viewport, "Image gallery");
        var gallery = new Grid { ClipToBounds = true };
        gallery.Children.Add(_viewport);
        _previous = NavigationButton(false);
        _next = NavigationButton(true);
        gallery.Children.Add(_previous);
        gallery.Children.Add(_next);
        Content = gallery;
        Visibility = Visibility.Collapsed;
        _viewport.ScrollChanged += (_, _) => UpdateNavigation();
        SizeChanged += (_, _) => UpdateNavigation();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        AutomationProperties.SetName(this, "Attached images");
        AutomationProperties.SetHelpText(this, "Use Left and Right arrows or Shift and the mouse wheel to browse images.");
        UpdateNavigation();
    }

    public void Load(IEnumerable<NoteImage> images, bool editable)
    {
        ArgumentNullException.ThrowIfNull(images);
        var copy = images.Select(Copy).ToList();
        if (copy.Count > ImageAttachments.MaxImages) throw new InvalidDataException("A note can contain up to four images.");
        CommitView(copy, editable);
    }

    public List<NoteImage> Capture() => _images.Select(Copy).ToList();

    public void AddImages(IEnumerable<NoteImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (!_editable) throw new InvalidOperationException("Open the note for editing to add images.");
        var additions = images.Select(Copy).ToList();
        if (_images.Count + additions.Count > ImageAttachments.MaxImages)
            throw new InvalidDataException("A note can contain up to four images. Remove an image to add another.");
        if (additions.Count == 0) return;
        CommitView(_images.Concat(additions).ToList(), _editable);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PickImages(Window? owner = null) => AddImages(ImageAttachments.ImportFiles(owner, _store, ImageAttachments.MaxImages - Count));

    public Task PickImagesAsync(Window? owner = null) =>
        ImportAsync(() => ImageAttachments.ImportFilesAsync(owner, _store, ImageAttachments.MaxImages - Count));

    public bool TryPasteImages(IDataObject data)
    {
        if (!_editable) return false;
        if (IsImporting)
            return data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent("PNG", false) || data.GetDataPresent(DataFormats.Bitmap);
        var import = ImageAttachments.ImportPasteAsync(data, _store, ImageAttachments.MaxImages - Count);
        if (import is null) return false;
        _ = CompletePasteAsync(import);
        return true;
    }

    private async Task CompletePasteAsync(Task<IReadOnlyList<NoteImage>> import)
    {
        try { await ImportAsync(() => import); }
        catch (Exception error) { ImportFailed?.Invoke(error); }
    }

    private async Task ImportAsync(Func<Task<IReadOnlyList<NoteImage>>> import)
    {
        if (IsImporting || !_editable) return;
        IsImporting = true;
        IsEnabled = false;
        ImportingChanged?.Invoke(this, EventArgs.Empty);
        try { AddImages(await import()); }
        finally
        {
            IsImporting = false;
            IsEnabled = true;
            ImportingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsFinite(constraint.Width) ? constraint.Width : ActualWidth > 0 ? ActualWidth : 540;
        return base.MeasureOverride(new Size(width, constraint.Height));
    }

    private void CommitView(List<NoteImage> images, bool editable)
    {
        // Build the complete replacement before committing either the draft or visible references.
        var children = new List<Grid>();
        var frames = new List<Border>();
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var imageIndex = index;
            // Keep one row height for any image count; natural widths avoid cropping and letterboxing.
            var imageHeight = editable ? EditablePreviewHeight : PreviewHeight;
            var ratio = Math.Max(1, image.PixelWidth) / (double)Math.Max(1, image.PixelHeight);
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, index == images.Count - 1 ? 0 : Gap, 0)
            };
            var frame = new Border
            {
                CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(248, 249, 250)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(239, 240, 241)), BorderThickness = new Thickness(1),
                ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(1, imageHeight * ratio), Height = imageHeight
            };
            AutomationProperties.SetAutomationId(frame, "AttachmentFrame-" + (index + 1));
            frame.SizeChanged += (_, _) => frame.Clip = new RectangleGeometry(new Rect(frame.RenderSize), 10, 10);
            grid.Children.Add(frame);
            try
            {
                frame.Child = new Image
                {
                    Source = ImageAttachments.LoadPreview(_store.GetImagePath(image), 1000),
                    Stretch = Stretch.Uniform
                };
                AutomationProperties.SetName(frame.Child, $"Attached image {index + 1}");
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException)
            {
                OperationDiagnostics.TryWrite(_store, "image-preview", error);
                frame.Child = new TextBlock
                {
                    Text = "Image unavailable", FontFamily = AppTypography.Text, FontSize = 11,
                    ToolTip = ImageFailureMessages.Describe(error),
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(83, 100, 113)),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8)
                };
            }
            if (editable)
            {
                var remove = FeedNoteCard.MakeButton("", "Remove image " + (index + 1), new Thickness(0));
                remove.Content = AppIcons.Create("close", 12);
                remove.Width = 24; remove.Height = 24; remove.MinHeight = 24;
                remove.Background = Brushes.White;
                remove.HorizontalAlignment = HorizontalAlignment.Right;
                remove.VerticalAlignment = VerticalAlignment.Top;
                remove.Margin = new Thickness(0, 5, 5, 0);
                remove.Click += (_, _) =>
                {
                    var remaining = _images.ToList();
                    remaining.RemoveAt(imageIndex);
                    CommitView(remaining, _editable);
                    Changed?.Invoke(this, EventArgs.Empty);
                };
                grid.Children.Add(remove);
            }
            frames.Add(frame);
            children.Add(grid);
        }
        _images = images;
        _frames = frames;
        _editable = editable;
        _items.Children.Clear();
        foreach (var child in children) _items.Children.Add(child);
        _viewport.ScrollToHorizontalOffset(0);
        Visibility = HasImages ? Visibility.Visible : Visibility.Collapsed;
        Margin = HasImages ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        UpdateNavigation();
        InvalidateMeasure();
    }

    private Button NavigationButton(bool forward)
    {
        var button = FeedNoteCard.MakeButton("", forward ? "Next image" : "Previous image", new Thickness(0), cornerRadius: 15);
        button.Content = Arrow(forward);
        button.Width = 30; button.Height = 30; button.MinHeight = 30;
        button.Background = new SolidColorBrush(Color.FromArgb(242, 255, 255, 255));
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(228, 231, 233));
        button.BorderThickness = new Thickness(1);
        button.HorizontalAlignment = forward ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(7, 0, 7, 0);
        button.Click += (_, _) => Move(forward ? 1 : -1);
        return button;
    }

    private static FrameworkElement Arrow(bool forward)
    {
        var canvas = new Canvas { Width = 16, Height = 16, IsHitTestVisible = false };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(forward ? "M6,3 L11,8 L6,13" : "M10,3 L5,8 L10,13"),
            Stroke = new SolidColorBrush(Color.FromRgb(83, 100, 113)), StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        });
        return canvas;
    }

    private void Move(int direction)
    {
        var starts = new List<double>();
        var offset = 0d;
        foreach (var frame in _frames) { starts.Add(offset); offset += frame.Width + Gap; }
        var page = Math.Max(1, _viewport.ViewportWidth * 0.86);
        var target = direction > 0
            ? Math.Min(starts.FirstOrDefault(start => start > _viewport.HorizontalOffset + 1, _viewport.ScrollableWidth),
                _viewport.HorizontalOffset + page)
            : Math.Max(starts.LastOrDefault(start => start < _viewport.HorizontalOffset - 1, 0),
                _viewport.HorizontalOffset - page);
        _viewport.ScrollToHorizontalOffset(Math.Clamp(target, 0, _viewport.ScrollableWidth));
    }

    private void UpdateNavigation()
    {
        var overflow = HasImages && _viewport.ScrollableWidth > 1;
        _previous.Visibility = overflow && _viewport.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = overflow && _viewport.HorizontalOffset < _viewport.ScrollableWidth - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!HasImages || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Left: Move(-1); break;
            case Key.Right: Move(1); break;
            case Key.Home: _viewport.ScrollToHorizontalOffset(0); break;
            case Key.End: _viewport.ScrollToHorizontalOffset(_viewport.ScrollableWidth); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (HasImages && _viewport.ScrollableWidth > 1 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Move(e.Delta < 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
        // A horizontal ScrollViewer otherwise consumes the ordinary wheel without moving
        // the surrounding feed, especially while an inline editor is open.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroll) continue;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta);
            e.Handled = true;
            return;
        }
    }

    private static NoteImage Copy(NoteImage image) => new()
    {
        AssetName = image.AssetName, PixelWidth = image.PixelWidth, PixelHeight = image.PixelHeight
    };
}
