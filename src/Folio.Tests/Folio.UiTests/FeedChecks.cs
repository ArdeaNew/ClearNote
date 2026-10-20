using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Synthetic, arranged WPF controls only: no windows, clipboard, or persisted user data.</summary>
public static class FeedChecks
{
    public static void Run()
    {
        BatchesAndVisibleViews();
        VisibilityThresholdAndEditing();
        NaturalHeightAndWheel();
        ContinuousSeparatorsAndContrast();
        DoubleClickEditing();
        CompactInlineEditing();
        AuthorIdentity();
        InlineSaveCancelAndComposer();
        DeleteAcceptance();
    }

    private static void BatchesAndVisibleViews()
    {
        var documents = Enumerable.Range(1, 45).Select(index => Card("Note " + index)).ToList();
        var requests = new List<int>();
        var viewed = new List<string>();
        var offset = 0;
        var feed = new FeedView();
        feed.CardViewed += card => viewed.Add(card.Id);
        feed.Reset(count =>
        {
            requests.Add(count);
            var batch = documents.Skip(offset).Take(count).ToList();
            offset += batch.Count;
            return batch;
        }, true);
        Assert(requests.SequenceEqual([20]) && feed.RenderedCards.Count == 20, "The first batch must request and render exactly 20 notes.");
        SetCardHeights(feed, 240);
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(viewed.SequenceEqual([documents[0].Id]), "Only the first note crosses half-visibility in the initial viewport; loaded notes below the fold must not count.");
        feed.CheckVisibleCards();
        feed.CheckVisibleCards();
        Assert(viewed.Count == 1, "Repeated layout checks must not count a visible note more than once.");

        var scroll = OuterScroll(feed);
        scroll.ScrollToVerticalOffset(160);
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(viewed.Contains(documents[1].Id), "Scrolling a second note into view must record it.");
        scroll.ScrollToTop();
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(viewed.Count(id => id == documents[0].Id) == 1, "Scrolling back to the first note must preserve per-reset deduplication.");
        Assert(!viewed.Contains(documents[19].Id), "The last loaded note must remain unviewed before it enters the viewport.");

        scroll.ScrollToBottom();
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(feed.RenderedCards.Count == 40 && requests.SequenceEqual([20, 20]), "Reaching the bottom must automatically append the next batch of 20.");
        SetCardHeights(feed, 240);
        Layout(feed);
        scroll.ScrollToBottom();
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(feed.RenderedCards.Count == 45 && requests.SequenceEqual([20, 20, 20]), "The last short batch must append without a persistent load-more control.");
        SetCardHeights(feed, 240);
        Layout(feed);
        scroll.ScrollToBottom();
        Layout(feed);
        for (var index = 0; index < 5; index++) feed.CheckVisibleCards();
        Assert(requests.Count == 3 && feed.RenderedCards.Select(card => card.Card.Id).Distinct().Count() == 45, "An exhausted feed must stop fetching and retain unique cards.");

        var repeatedFetches = 0;
        feed.Reset(count => { repeatedFetches++; return documents.Take(count).ToList(); }, false);
        SetCardHeights(feed, 240);
        Layout(feed);
        scroll.ScrollToBottom();
        Layout(feed);
        feed.CheckVisibleCards();
        Layout(feed);
        for (var index = 0; index < 5; index++) feed.CheckVisibleCards();
        Assert(repeatedFetches == 2 && feed.RenderedCards.Count == 20, "A repeated batch must not cause duplicate cards or an endless fetch loop.");
    }

    private static void VisibilityThresholdAndEditing()
    {
        var documents = Enumerable.Range(0, 3).Select(index => Card("Threshold " + index)).ToList();
        var feed = new FeedView();
        var viewed = new List<string>();
        feed.CardViewed += card => viewed.Add(card.Id);
        feed.Reset(_ => documents, true);
        SetCardHeights(feed, 240);
        Layout(feed);
        var first = feed.RenderedCards[0];
        var scroll = OuterScroll(feed);
        // The continuous feed starts at y=0; leave 119 of its 240 pixels visible.
        scroll.ScrollToVerticalOffset(121);
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(!viewed.Contains(first.Card.Id), "A note with less than half its height visible must not count.");
        scroll.ScrollToVerticalOffset(119);
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(viewed.Count(id => id == first.Card.Id) == 1, "A note crossing the half-visible boundary must count once.");

        viewed.Clear();
        feed.Reset(_ => documents, true);
        SetCardHeights(feed, 240);
        Layout(feed);
        first = feed.RenderedCards[0];
        first.BeginEdit();
        feed.CheckVisibleCards();
        Assert(!viewed.Contains(first.Card.Id), "An inline editor must not count as reviewing a note.");
        first.CancelEdit();
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(viewed.Contains(first.Card.Id), "The saved read-only note can count after inline editing ends.");
        viewed.Clear();
        feed.Reset(_ => documents, false);
        SetCardHeights(feed, 240);
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(viewed.Count == 0, "Notes mode must never record passive review events.");
    }

    private static void NaturalHeightAndWheel()
    {
        var longText = string.Join("\n", Enumerable.Range(1, 100).Select(index => $"Paragraph {index}: all text stays reachable."));
        var document = Card(longText);
        var viewed = 0;
        var feed = new FeedView();
        feed.CardViewed += _ => viewed++;
        feed.Reset(_ => new[] { document }, true);
        Layout(feed);
        var card = feed.RenderedCards.Single();
        Assert(card.ActualHeight > 700 && card.Body.ActualHeight > 600, "Long read-only notes must use natural card height rather than a clipped or internally scrolling box.");
        Assert(card.Body.TextBox.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled, "Read-only note bodies must disable their internal vertical scrollbar.");
        Assert(card.Body.PlainText.Contains("Paragraph 100:"), "The final paragraph of a long note must remain in the card.");
        feed.CheckVisibleCards();
        Assert(viewed == 1, "A note taller than the viewport must count after half the viewport is occupied.");
        var scroll = OuterScroll(feed);
        var previous = scroll.VerticalOffset;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        card.Body.TextBox.RaiseEvent(wheel);
        Layout(feed);
        Assert(wheel.Handled && scroll.VerticalOffset > previous, "The mouse wheel over a read-only card must scroll the outer feed.");
        scroll.ScrollToBottom();
        Layout(feed);
        var bottom = card.TransformToAncestor(scroll).Transform(new Point(0, card.ActualHeight)).Y;
        Assert(bottom <= scroll.ActualHeight + 1, "The end of an expanded long card must be reachable in the outer viewport.");
    }

    private static void ContinuousSeparatorsAndContrast()
    {
        var feed = new FeedView();
        feed.Reset(_ => new[] { Card("First entry"), Card("Second entry") }, false);
        Layout(feed);
        var first = feed.RenderedCards[0];
        var second = feed.RenderedCards[1];
        var scroll = OuterScroll(feed);
        var firstOrigin = first.TransformToAncestor(scroll).Transform(new Point());
        var secondOrigin = second.TransformToAncestor(scroll).Transform(new Point());
        Assert(Math.Abs(firstOrigin.X) < .01 && Math.Abs(firstOrigin.Y) < .01,
            "The first entry must start at the reading column edge without an outer card gutter.");
        Assert(Math.Abs(secondOrigin.Y - firstOrigin.Y - first.ActualHeight) < .01,
            "Adjacent entries must meet at a continuous separator without floating-card gaps.");
        Assert(Math.Abs(first.ActualWidth - scroll.ViewportWidth) < .01,
            "Entry separators must span the full feed viewport.");
        var shell = (Border)first.Content;
        Assert(shell.BorderThickness == new Thickness(0, 0, 0, 1) && shell.CornerRadius == new CornerRadius(0),
            "Each entry must have one straight bottom hairline, with no individual card outline.");
        var timestamp = Descendants(first).OfType<TextBlock>().Single(text => text.Text == "2020-01-02 03:04" || text.Text == first.Card.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        Assert(timestamp.Foreground is SolidColorBrush { Color: var color } && color == Color.FromRgb(83, 100, 113),
            "Metadata must use the readable slate text color rather than faint gray.");
    }

    private static void DoubleClickEditing()
    {
        var document = Card("A selectable original note");
        var feed = new FeedView { SaveNote = (_, _) => true };
        var reviews = 0;
        feed.CardViewed += _ => reviews++;
        feed.Reset(_ => new[] { document }, true);
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(reviews == 1, "The visible Review entry must count once before editing.");
        var card = feed.RenderedCards.Single();
        var options = Descendants(card).OfType<Button>().Single(button =>
            AutomationProperties.GetAutomationId(button) == "NoteOptions-" + document.Id);
        DoubleClick(card, options);
        Assert(!card.IsEditing, "Double-clicking the options action must not open the note editor.");
        DoubleClick(card, card.Body.TextBox, MouseButton.Right);
        Assert(!card.IsEditing, "Right-button gestures must retain their normal text/context behavior.");
        card.Body.TextBox.SelectAll();
        var click = DoubleClick(card, card.Body.TextBox);
        Assert(click.Handled && card.IsEditing && card.Body.PlainText.Trim() == "A selectable original note",
            "Double-clicking selected read-only note text must open the same note for inline editing.");
        card.Body.PlainText = "An unsaved replacement";
        DoubleClick(card, card.Body.TextBox);
        Assert(card.IsEditing && card.Body.PlainText.Trim() == "An unsaved replacement",
            "Further double-clicks inside an active editor must preserve its unsaved buffer.");
        Assert(card.TrySave(), "The existing save action must accept a double-click edit.");
        Layout(feed);
        feed.CheckVisibleCards();
        Assert(!card.IsEditing && reviews == 1 && NoteText.PlainText(document).Trim() == "An unsaved replacement",
            "Saving an edited Review entry must not count a second memory event.");
        DoubleClick(card, (Border)card.Content);
        Assert(card.IsEditing, "Double-clicking the entry content surface must also open editing.");
        card.Body.PlainText = "Cancel this replacement";
        card.CancelEdit();
        Assert(card.Body.PlainText.Trim() == "An unsaved replacement",
            "Cancel must still restore the saved content after a double-click edit.");
        var readOnly = new FeedNoteCard(Card("No persistence callback"));
        DoubleClick(readOnly, readOnly.Body.TextBox);
        Assert(!readOnly.IsEditing, "A display without a save callback must not open an unsaveable editor.");
    }

    private static MouseButtonEventArgs DoubleClick(FeedNoteCard card, DependencyObject source, MouseButton button = MouseButton.Left)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        { RoutedEvent = Control.PreviewMouseDoubleClickEvent, Source = source };
        card.RaiseEvent(args);
        return args;
    }

    private static void CompactInlineEditing()
    {
        var document = Card("A short note.");
        document.Note!.Paragraphs[0].Runs[0].Background = "#EEEEEE";
        var feed = new FeedView { SaveNote = (_, _) => false };
        feed.Reset(_ => new[] { document }, false);
        Layout(feed);
        var card = feed.RenderedCards.Single();
        card.BeginEdit();
        Layout(feed);
        Assert(card.Body.ActualHeight is > 0 and < 70,
            "Editing a short note must fit its one-line text rather than reserve a blank 150-pixel editor.");
        Assert(card.Body.Background is SolidColorBrush { Color.A: 0 } &&
               card.Body.TextBox.Background is SolidColorBrush { Color.A: 0 } &&
               card.Body.TextBox.Document.Background is SolidColorBrush { Color.A: 0 },
            "The editor and document surfaces must let the content region's background show through.");
        Assert(card.CaptureEdit().Paragraphs[0].Runs[0].Background == "#EEEEEE",
            "Transparent editor surfaces must preserve the user's selected text highlight.");

        var longText = string.Join('\n', Enumerable.Range(1, 80).Select(index => $"Editable paragraph {index}: all content stays available."));
        card.Body.PlainText = longText;
        Layout(feed);
        Layout(feed);
        Assert(card.Body.ActualHeight is > 300 and <= 420 && card.Body.CanUndo,
            "An inline editor must grow with its content, stop at 420 pixels and retain native Undo.");
        var inner = Descendants(card.Body.TextBox).OfType<ScrollViewer>().Single();
        Assert(inner.ScrollableHeight > 0 && inner.ExtentHeight > inner.ViewportHeight,
            "A long editable note must overflow into its own vertical scroller rather than lose text.");
        card.Body.TextBox.ScrollToEnd();
        Layout(feed);
        Assert(inner.VerticalOffset > 0 && card.Body.PlainText.EndsWith("Editable paragraph 80: all content stays available.", StringComparison.Ordinal),
            "The final paragraph must remain reachable at the bottom of the compact editor.");
        Assert(!card.TrySave() && card.IsEditing && card.Body.PlainText == longText,
            "A failed Save must preserve the full long-note draft and its open editor.");
        card.Body.Undo();
        Layout(feed);
        Assert(card.Body.PlainText == "A short note." && card.Body.ActualHeight is > 0 and < 70,
            "Undoing the long replacement must shrink the editor back to its natural short height.");
        card.CancelEdit();
        Assert(!card.IsEditing && card.Body.PlainText == "A short note." && card.Body.TextBox.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "Cancel must restore the saved note and natural read-only feed scrolling.");
    }

    private static void AuthorIdentity()
    {
        var document = Card("A note by the local author");
        var custom = new FeedNoteCard(document, profile: new UserProfile { DisplayName = "Ada" });
        Layout(custom);
        var author = Descendants(custom).OfType<TextBlock>().Single(text =>
            AutomationProperties.GetAutomationId(text) == "NoteAuthor-" + document.Id);
        Assert(author.Text == "Ada", "Each note must display the configured local author name.");
        var standard = new FeedNoteCard(Card("Default local author"));
        Layout(standard);
        Assert(Descendants(standard).OfType<TextBlock>().Any(text => text.Text == "You"),
            "A note without a saved profile must display a readable default identity.");
        Assert(Descendants(custom).OfType<Border>().Any(border =>
            AutomationProperties.GetName(border) == "Author avatar" && border.Child is not null),
            "An author without a custom avatar must still have the app's open default mark.");
    }

    private static void InlineSaveCancelAndComposer()
    {
        var firstDocument = Card("Original first");
        var secondDocument = Card("Original second");
        var feed = new FeedView();
        feed.Reset(_ => new[] { firstDocument, secondDocument }, false);
        Layout(feed);
        var first = feed.RenderedCards[0];
        var second = feed.RenderedCards[1];
        var composer = new NoteEditor();
        composer.Load(Card("Unsent composer draft"), true);
        var saves = new List<string>();
        var allowSecond = false;
        feed.SaveNote = (document, content) =>
        {
            saves.Add(document.Id);
            return document.Id != secondDocument.Id || allowSecond;
        };
        first.BeginEdit();
        first.Body.PlainText = "Discard this edit";
        Assert(saves.Count == 0 && NoteText.PlainText(firstDocument).Trim() == "Original first", "Typing in a card must not persist content before Save.");
        first.CancelEdit();
        Assert(!first.IsEditing && first.Body.PlainText.Trim() == "Original first", "Cancel must reload the original note.");
        first.BeginEdit();
        second.BeginEdit();
        first.Body.PlainText = "Saved first";
        second.Body.PlainText = "Pending second";
        Assert(first.CaptureEdit().Paragraphs.SelectMany(paragraph => paragraph.Runs).Any(run => run.Text.Contains("Saved first")), "CaptureEdit must capture the active inline buffer.");
        feed.CheckVisibleCards();
        Assert(first.IsEditing && second.IsEditing && second.Body.PlainText.Contains("Pending second"), "Mere scroll checks must preserve inline editors and their buffers.");
        Assert(!feed.SavePendingEdits(), "A failed inline save must veto pending-edit completion.");
        Assert(!first.IsEditing && first.Body.PlainText.Trim() == "Saved first", "Successful inline Save must show the accepted note.");
        Assert(second.IsEditing && second.Body.PlainText.Trim() == "Pending second" && NoteText.PlainText(secondDocument).Trim() == "Original second", "A failed Save must keep its editor and unsaved text, preserving the saved document.");
        Assert(composer.PlainText.Trim() == "Unsent composer draft", "Inline edits must never replace the independent top composer draft.");
        allowSecond = true;
        Assert(feed.SavePendingEdits() && !second.IsEditing && second.Body.PlainText.Trim() == "Pending second", "Retrying a failed save must close the editor only after acceptance.");
    }

    private static void DeleteAcceptance()
    {
        var feed = new FeedView();
        var document = Card("Keep until deletion succeeds");
        feed.Reset(_ => new[] { document }, false);
        Layout(feed);
        var options = Descendants(feed.RenderedCards.Single()).OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "NoteOptions-" + document.Id);
        var delete = options.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Delete note"));
        feed.DeleteNote = _ => false;
        delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert(feed.RenderedCards.Count == 1, "A rejected or failed delete must keep the card in the feed.");
        feed.DeleteNote = _ => true;
        delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert(feed.RenderedCards.Count == 0, "The card must be removed after its delete callback accepts the operation.");
    }

    private static CardDocument Card(string text) => new()
    {
        CreatedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 0, TimeSpan.Zero),
        Note = new NoteContent
        {
            Paragraphs = text.Split('\n').Select(line => new NoteParagraph
            { Runs = [new NoteRun { Text = line }] }).ToList()
        }
    };

    private static ScrollViewer OuterScroll(FeedView feed) => Descendants(feed).OfType<ScrollViewer>()
        .Single(scroll => AutomationProperties.GetAutomationId(scroll) == "NotesFeed");

    private static void SetCardHeights(FeedView feed, double height)
    {
        foreach (var card in feed.RenderedCards) card.Height = height;
    }

    private static void Layout(FrameworkElement control)
    {
        control.Width = 520;
        control.Height = 300;
        control.Measure(new Size(520, 300));
        control.Arrange(new Rect(0, 0, 520, 300));
        control.UpdateLayout();
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
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
