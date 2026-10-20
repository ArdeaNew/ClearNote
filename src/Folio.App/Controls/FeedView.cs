using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Folio.Core;

namespace Folio.App.Controls;

/// <summary>A single scrolling note feed. Loading a batch does not count its notes as viewed.</summary>
public sealed class FeedView : UserControl
{
    private const int BatchSize = 20;
    private const double PrefetchDistance = 280;
    private readonly StackPanel _cardsPanel;
    private readonly ScrollViewer _scroll;
    private readonly List<FeedNoteCard> _renderedCards = [];
    private readonly HashSet<string> _renderedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _viewedIds = new(StringComparer.Ordinal);
    private Func<int, IReadOnlyList<CardDocument>>? _fetchBatch;
    private bool _trackViews;
    private bool _hasMore;
    private bool _fetching;
    private bool _checking;
    private bool _checkQueued;
    private int _generation;

    public Func<CardDocument, NoteContent, bool>? SaveNote { get; set; }
    public LibraryStore? Store { get; set; }
    public UserProfile Profile { get; set; } = new();
    public Func<CardDocument, bool>? DeleteNote { get; set; }
    public event Action<CardDocument>? CardViewed;
    public event Action? ProfileRequested;
    public event Action? AvatarChangeRequested;
    public event Action<Exception>? InputRejected;
    public IReadOnlyList<FeedNoteCard> RenderedCards => _renderedCards.AsReadOnly();

    public void ApplyProfile(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var card in _renderedCards) card.ApplyProfile(profile);
        Profile = profile;
    }

    public FeedView()
    {
        Background = Surround;
        FontFamily = AppTypography.Text;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid { Background = Surround };
        _cardsPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _scroll = new ScrollViewer
        {
            Content = _cardsPanel, Background = Surround, CanContentScroll = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false, Padding = new Thickness(0)
        };
        AutomationProperties.SetName(_scroll, "Notes feed");
        AutomationProperties.SetAutomationId(_scroll, "NotesFeed");
        root.Children.Add(_scroll);
        Content = root;
        _scroll.ScrollChanged += (_, _) => QueueVisibleCheck();
        _scroll.SizeChanged += (_, _) => QueueVisibleCheck();
        _cardsPanel.LayoutUpdated += (_, _) => QueueVisibleCheck();
        Loaded += (_, _) => QueueVisibleCheck();
    }

    /// <summary>The callback returns up to the requested count, advancing its own cursor.</summary>
    public void Reset(Func<int, IReadOnlyList<CardDocument>> fetchBatch, bool trackViews)
    {
        ArgumentNullException.ThrowIfNull(fetchBatch);
        _generation++;
        _fetchBatch = fetchBatch;
        _trackViews = trackViews;
        _hasMore = true;
        _renderedCards.Clear();
        _renderedIds.Clear();
        _viewedIds.Clear();
        _cardsPanel.Children.Clear();
        _scroll.ScrollToTop();
        AppendBatch();
        QueueVisibleCheck();
    }

    public void ScrollToTop()
    {
        _scroll.ScrollToTop();
        QueueVisibleCheck();
    }

    public bool SavePendingEdits()
    {
        foreach (var card in _renderedCards.ToArray())
            if (card.IsEditing && !card.TrySave()) return false;
        return true;
    }

    /// <summary>Uses arranged visual bounds, so an offscreen host can exercise the same viewport rules.</summary>
    public void CheckVisibleCards()
    {
        if (_checking) return;
        _checking = true;
        var generation = _generation;
        try
        {
            var viewport = ViewportBounds();
            if (viewport.IsEmpty || viewport.Width <= 0 || viewport.Height <= 0) return;
            if (_trackViews)
            {
                foreach (var card in _renderedCards.ToArray())
                {
                    if (generation != _generation) return;
                    if (card.IsEditing || _viewedIds.Contains(card.Card.Id) || card.ActualHeight <= 0 || card.ActualWidth <= 0) continue;
                    Rect bounds;
                    try
                    {
                        bounds = card.TransformToAncestor(_scroll)
                            .TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                    }
                    catch (InvalidOperationException) { continue; }
                    var visible = Rect.Intersect(bounds, viewport);
                    var requiredHeight = 0.5 * Math.Min(bounds.Height, viewport.Height);
                    if (visible.IsEmpty || visible.Width <= 0 || visible.Height + 0.01 < requiredHeight) continue;
                    _viewedIds.Add(card.Card.Id);
                    CardViewed?.Invoke(card.Card);
                }
            }
            if (generation == _generation && _hasMore && !_fetching &&
                _scroll.ExtentHeight - (_scroll.VerticalOffset + viewport.Height) <= PrefetchDistance)
                AppendBatch();
        }
        finally { _checking = false; }
    }

    private void AppendBatch()
    {
        if (_fetching || !_hasMore || _fetchBatch is null) return;
        _fetching = true;
        var generation = _generation;
        try
        {
            var batch = _fetchBatch(BatchSize) ?? [];
            if (generation != _generation) return;
            var added = 0;
            foreach (var document in batch)
            {
                if (document is null || !_renderedIds.Add(document.Id)) continue;
                var card = new FeedNoteCard(document, Store, Profile)
                {
                    Margin = new Thickness(0),
                    SaveNote = (note, content) => SaveNote?.Invoke(note, content) ?? false,
                    DeleteNote = note => DeleteNote?.Invoke(note) ?? false
                };
                card.Deleted += RemoveCard;
                card.ProfileRequested += RequestProfile;
                card.AvatarChangeRequested += RequestAvatarChange;
                card.Body.InputRejected += error => InputRejected?.Invoke(error);
                card.InputRejected += error => InputRejected?.Invoke(error);
                card.EditingChanged += QueueVisibleCheck;
                _renderedCards.Add(card);
                _cardsPanel.Children.Add(card);
                added++;
            }
            // A repeated batch cannot cause an unbounded layout/fetch loop.
            _hasMore = batch.Count >= BatchSize && added > 0;
        }
        finally { _fetching = false; }
    }

    private void RemoveCard(FeedNoteCard card)
    {
        if (!_renderedCards.Remove(card)) return;
        card.Deleted -= RemoveCard;
        card.ProfileRequested -= RequestProfile;
        card.AvatarChangeRequested -= RequestAvatarChange;
        card.EditingChanged -= QueueVisibleCheck;
        _cardsPanel.Children.Remove(card);
        QueueVisibleCheck();
    }

    private void RequestProfile() => ProfileRequested?.Invoke();
    private void RequestAvatarChange() => AvatarChangeRequested?.Invoke();

    private Rect ViewportBounds()
    {
        if (_scroll.ActualWidth <= 0 || _scroll.ActualHeight <= 0) return Rect.Empty;
        var viewport = FindVisualChild<ScrollContentPresenter>(_scroll);
        if (viewport is not null && viewport.ActualWidth > 0 && viewport.ActualHeight > 0)
        {
            try
            {
                return viewport.TransformToAncestor(_scroll)
                    .TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
            }
            catch (InvalidOperationException) { return Rect.Empty; }
        }
        var height = Math.Min(_scroll.ActualHeight, _scroll.ViewportHeight);
        var width = Math.Min(_scroll.ActualWidth, _scroll.ViewportWidth);
        return width > 0 && height > 0 ? new Rect(0, 0, width, height) : Rect.Empty;
    }

    private void QueueVisibleCheck()
    {
        if (_checkQueued || Dispatcher.HasShutdownStarted) return;
        _checkQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _checkQueued = false;
            CheckVisibleCards();
        }));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private static readonly Brush Surround = MakeSurround();
    private static Brush MakeSurround()
    {
        var brush = new SolidColorBrush(Colors.White);
        brush.Freeze();
        return brush;
    }
}
