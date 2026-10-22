using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Folio.App.Controls;
using Folio.Core;

namespace Folio.App;

public partial class MainWindow : Window
{
    private readonly LibraryStore _store;
    private readonly WindowWidthPreferences _widthPreferences;
    private readonly WindowSystemMenu _windowMenu;
    private readonly (Button Button, NoteCollection Collection)[] _collectionButtons;
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly HashSet<string> _reviewRecorded = new(StringComparer.Ordinal);
    private readonly AttachmentPanel _composerMedia;
    private readonly TagSuggestions _tagSuggestions;
    private IReadOnlyList<string> _knownTags = [];
    private UserProfile _profile = new();
    private NoteCollection _collection = NoteCollection.Notes;
    private string? _reviewTag;
    private string _draftId = Guid.NewGuid().ToString("N");
    private string? _errorSource;
    private string _appliedSearchText = "";
    private bool _restoringSearch;
    private bool _reviewMode, _binding, _draftDirty, _draftBlocked, _ready, _closeAfterImport;
    private bool IsImportingImages => _composerMedia.IsImporting || NotesFeed.IsImportingImages;

    public MainWindow(LibraryStore store)
    {
        _store = store;
        _widthPreferences = new WindowWidthPreferences(Path.GetDirectoryName(store.RootPath)!);
        InitializeComponent();
        _collectionButtons = [(NotesButton, NoteCollection.Notes), (MemoButton, NoteCollection.Memo),
            (OpinionsButton, NoteCollection.Opinions), (EconomicAnalysisButton, NoteCollection.EconomicAnalysis)];
        foreach (var (button, collection) in _collectionButtons)
            button.Click += (_, _) => SwitchCollection(collection);
        _windowMenu = new WindowSystemMenu(this);
        ApplyFixedWidth(_widthPreferences.Load());
        _composerMedia = new AttachmentPanel(store);
        _composerMedia.Load([], true);
        ComposerMediaHost.Children.Add(_composerMedia);
        _composerMedia.Changed += (_, _) => MarkDraftDirty();
        _composerMedia.ImportingChanged += (_, _) => UpdateImageImportState();
        _composerMedia.ImportFailed += error => SetError("image", ImageFailureMessages.Describe(error), error);
        NotesFeed.Store = store;
        LoadProfile();
        foreach (var (button, key) in new (Button, string)[]
        {
            (SearchButton, "search"), (MoreButton, "more"), (ImageButton, "image"), (TagButton, "tag"),
            (BoldButton, "bold"), (ItalicButton, "italic"), (HeadingButton, "heading"),
            (BulletsButton, "bullets"), (NumberingButton, "numbering"),
            (ForegroundButton, "textColor"), (HighlightButton, "highlight")
        }) button.Content = AppIcons.Create(key);
        Composer.Load(new CardDocument(), true);
        Composer.Changed += (_, _) => MarkDraftDirty();
        Composer.AttachmentPasteHandler = _composerMedia.TryPasteImages;
        _tagSuggestions = new TagSuggestions(Composer, () => _knownTags);
        Closed += (_, _) => _tagSuggestions.Dispose();
        Composer.SelectionChanged += (_, _) => UpdateSelection();
        Composer.InputRejected += ShowInputError;
        _draftTimer.Tick += (_, _) => { _draftTimer.Stop(); SaveDraft(); };
        NotesFeed.SaveNote = SaveNote;
        NotesFeed.DeleteNote = DeleteNote;
        NotesFeed.CardViewed += RecordView;
        NotesFeed.ProfileRequested += EditProfile;
        NotesFeed.AvatarChangeRequested += ChangeProfilePicture;
        NotesFeed.InputRejected += ShowInputError;
        NotesFeed.ImageImportChanged += UpdateImageImportState;
        NotesFeed.InputChanged += () => ClearError("input");
        NotesFeed.ContentChanged += OnFeedContentChanged;
        LoadDraft();
        _ready = true;
        RefreshFeed();
        UpdateSelection();
    }

    private void MarkDraftDirty()
    {
        if (_binding) return;
        ClearError("input");
        ClearError("image");
        _draftDirty = true;
        _draftTimer.Stop();
        _draftTimer.Start();
        UpdateSelection();
    }

    private NoteContent CaptureComposer()
    {
        Composer.CompleteInput();
        var content = Composer.Capture();
        content.Images = _composerMedia.Capture();
        return content;
    }

    private bool HasComposerContent => !string.IsNullOrWhiteSpace(Composer.PlainText) || _composerMedia.HasImages;

    private void LoadDraft()
    {
        _binding = true;
        try
        {
            Composer.Load(new CardDocument(), true);
            _composerMedia.Load([], true);
            _draftId = Guid.NewGuid().ToString("N");
            var draft = _store.LoadDraft(_collection);
            _draftBlocked = _store.IsDraftSaveBlocked(_collection);
            if (draft != null)
            {
                // A crash after posting but before clearing the draft must not duplicate the note.
                if (_store.Cards.Any(card => card.Id == draft.CardId))
                {
                    if (!_draftBlocked) _store.ClearDraft(_collection);
                    Composer.Load(new CardDocument(), true);
                    _composerMedia.Load([], true);
                    _draftId = Guid.NewGuid().ToString("N");
                }
                else
                {
                    _draftId = draft.CardId;
                    Composer.Load(new CardDocument { Note = draft.Content }, true);
                    _composerMedia.Load(draft.Content.Images, true);
                }
            }
            if (_draftBlocked) SetError("draft", "The previous draft needs recovery. New notes can still be posted.", new IOException(_store.GetDraftRecoveryNotice(_collection)));
            _draftDirty = false;
        }
        catch (Exception ex)
        {
            _draftBlocked = true;
            SetError("draft", "Draft recovery is needed. New notes can still be posted.", ex);
        }
        finally { _binding = false; }
    }

    private bool SaveDraft()
    {
        if (IsImportingImages) return false;
        if (!_draftDirty) return true;
        if (_draftBlocked) return false;
        try
        {
            var content = CaptureComposer();
            if (!HasComposerContent) _store.ClearDraft(_collection);
            else _store.SaveDraft(new ComposerDraft { CardId = _draftId, Content = content }, _collection);
            _draftDirty = false;
            ClearError("draft");
            return true;
        }
        catch (Exception ex) { SetError("draft", "Draft not saved. Your text is still here.", ex); return false; }
    }

    private void Post_Click(object sender, RoutedEventArgs e) => PostNote();
    private void PostNote()
    {
        if (IsImportingImages) return;
        Composer.CompleteInput();
        if (!HasComposerContent || !NotesFeed.SavePendingEdits()) return;
        _draftTimer.Stop();
        try
        {
            var card = _store.Cards.FirstOrDefault(item => item.Id == _draftId) ?? new CardDocument { Id = _draftId, Collection = _collection };
            NoteText.SetContent(card, CaptureComposer());
            card.UpdatedAt = DateTimeOffset.Now;
            _store.Upsert(card);
            // A stable draft ID makes a failed clear safe to retry.
            if (!_draftBlocked) _store.ClearDraft(_collection);
            _binding = true;
            Composer.Load(new CardDocument(), true);
            _composerMedia.Load([], true);
            _binding = false;
            _draftId = Guid.NewGuid().ToString("N");
            _draftDirty = false;
            _reviewMode = false;
            SearchBox.Text = "";
            RefreshFeed();
            NotesFeed.ScrollToTop();
            UpdateSelection();
            ClearError("post");
            Composer.FocusEditor();
        }
        catch (Exception ex) { SetError("post", "The note could not finish saving. Your text is still here; retry posting.", ex); }
        finally { _binding = false; }
    }

    private void Navigation_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (CollectionNavigation.ScrollableWidth <= 0) return;
        CollectionNavigation.ScrollToHorizontalOffset(CollectionNavigation.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private bool SwitchCollection(NoteCollection collection, bool refresh = true)
    {
        if (IsImportingImages || !NotesFeed.SavePendingEdits() || !SaveDraft()) return false;
        _draftTimer.Stop();
        var changed = collection != _collection;
        _collection = collection;
        _reviewMode = false;
        if (changed)
        {
            ClearError("draft");
            // Avoid rebuilding the departing feed while the search box changes.
            _ready = false;
            SearchBox.Text = "";
            LoadDraft();
            _ready = true;
        }
        if (refresh) RefreshFeed();
        UpdateSelection();
        return true;
    }
    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (IsImportingImages) return;
        // Opening the menu is a read-only action. A review round starts only after selection.
        var menu = new ContextMenu
        {
            PlacementTarget = ReviewButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = 4,
            FontFamily = AppTypography.Text,
            MinWidth = 180
        };
        AddReviewTag(menu, "All tags", null);
        var tags = _store.Cards.Where(card => card.Collection == NoteCollection.Notes).SelectMany(NoteTags.Read).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags) AddReviewTag(menu, "#" + tag, tag);
        ReviewButton.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private void AddReviewTag(ContextMenu menu, string label, string? tag)
    {
        var current = _reviewMode && string.Equals(_reviewTag, tag, StringComparison.OrdinalIgnoreCase);
        var item = new MenuItem
        {
            Header = label,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = current ? Ink : Muted,
            Padding = new Thickness(9, 8, 9, 8)
        };
        item.Click += (_, _) => SelectReviewTag(tag);
        menu.Items.Add(item);
    }
    private void SelectReviewTag(string? tag)
    {
        var continued = _reviewMode;
        if (!SwitchCollection(NoteCollection.Notes, refresh: false)) return;
        if (!continued) _reviewRecorded.Clear();
        _reviewTag = tag;
        _reviewMode = true;
        RefreshFeed();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_restoringSearch) return;
        if (!_ready || IsImportingImages) { UpdateSearchIndicator(); return; }
        if (!NotesFeed.SavePendingEdits())
        {
            _restoringSearch = true;
            try
            {
                SearchBox.Text = _appliedSearchText;
                SearchBox.CaretIndex = SearchBox.Text.Length;
            }
            finally { _restoringSearch = false; }
            UpdateSearchIndicator();
            return;
        }
        RefreshFeed();
        UpdateSearchIndicator();
    }
    private void UpdateSearchIndicator()
    {
        if (SearchButton != null && SearchBox != null)
        {
            var query = SearchBox.Text.Trim();
            SearchButton.Background = query.Length == 0 ? Brushes.Transparent : Selected;
            SearchButton.ToolTip = query.Length == 0 ? "Search notes" : "Search: " + query;
        }
    }
    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (IsImportingImages) return;
        SearchPopup.IsOpen = !SearchPopup.IsOpen;
        if (SearchPopup.IsOpen) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
    }
    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        SearchPopup.IsOpen = false;
        SearchButton.Focus();
        e.Handled = true;
    }
    private void RefreshFeed()
    {
        var library = _store.Cards.Where(card => card.Collection == _collection).ToList();
        UpdateKnownTags(library);
        var query = SearchBox.Text.Trim();
        _appliedSearchText = SearchBox.Text;
        var cards = library.Where(card => MatchesCurrentFilter(card, query)).ToList();
        foreach (var (button, collection) in _collectionButtons)
        {
            var selected = !_reviewMode && _collection == collection;
            button.Foreground = selected ? Ink : Muted;
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        ReviewButton.Foreground = _reviewMode ? Ink : Muted;
        ReviewButton.FontWeight = _reviewMode ? FontWeights.SemiBold : FontWeights.Normal;
        if (_reviewMode)
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            NotesFeed.Reset(count =>
            {
                var batch = new List<CardDocument>();
                while (batch.Count < count)
                {
                    var next = ReviewScheduler.Pick(cards, selected, DateTimeOffset.Now);
                    if (next == null) break;
                    selected.Add(next.Id);
                    batch.Add(next);
                }
                return batch;
            }, trackViews: true);
        }
        else
        {
            var ordered = cards.OrderByDescending(card => card.CreatedAt).ThenBy(card => card.Id, StringComparer.Ordinal).ToList();
            var offset = 0;
            NotesFeed.Reset(count =>
            {
                var batch = ordered.Skip(offset).Take(count).ToList();
                offset += batch.Count;
                return batch;
            }, trackViews: false);
        }
    }
    private void UpdateKnownTags(IEnumerable<CardDocument> library) =>
        _knownTags = library.SelectMany(NoteTags.Read).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList();

    private bool MatchesCurrentFilter(CardDocument card, string query) =>
        (!_reviewMode || NoteTags.Matches(card, _reviewTag)) && (query.Length == 0 ||
            (card.Title + "\n" + NoteText.PlainText(card)).Contains(query, StringComparison.OrdinalIgnoreCase));

    private void OnFeedContentChanged()
    {
        var library = _store.Cards.Where(card => card.Collection == _collection).ToList();
        UpdateKnownTags(library);
        var current = library.ToDictionary(card => card.Id, StringComparer.Ordinal);
        // A save may finish while Search_Changed is still validating a new query.
        // Reconcile against the query already shown by the feed until that action succeeds.
        var query = _appliedSearchText.Trim();
        NotesFeed.ReconcileSavedFilter(card => current.TryGetValue(card.Id, out var saved) && MatchesCurrentFilter(saved, query));
    }
    private void RecordView(CardDocument document)
    {
        if (!_reviewMode || document.Collection != NoteCollection.Notes || _reviewRecorded.Contains(document.Id)) return;
        try
        {
            var current = _store.Cards.FirstOrDefault(card => card.Id == document.Id);
            if (current == null || current.Collection != NoteCollection.Notes) return;
            ReviewScheduler.RecordBrowse(current, DateTimeOffset.Now);
            _store.Upsert(current);
            _reviewRecorded.Add(current.Id);
            ClearError("review");
        }
        catch (Exception ex) { SetError("review", "Review not saved. Check the data folder, then refresh to retry.", ex); }
    }
    private bool SaveNote(CardDocument document, NoteContent content)
    {
        if (IsImportingImages) return false;
        try
        {
            var current = _store.Cards.FirstOrDefault(card => card.Id == document.Id)
                ?? throw new IOException("This note was removed. Your edit is still in this window.");
            NoteText.SetContent(current, content);
            current.UpdatedAt = DateTimeOffset.Now;
            _store.Upsert(current);
            ClearError("edit");
            return true;
        }
        catch (Exception ex) { SetError("edit", "Note not saved. Your edit is still here.", ex); return false; }
    }
    private bool DeleteNote(CardDocument document)
    {
        if (IsImportingImages) return false;
        if (DialogBox.Show(this, "Delete this note and its unused images?", "Delete note", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return false;
        try
        {
            if (!SaveDraft()) return false;
            _store.Delete(document.Id);
            ClearError("delete");
            return true;
        }
        catch (Exception ex) { SetError("delete", "The note could not be deleted.", ex); return false; }
    }

    private void UpdateSelection()
    {
        if (Composer == null || PostButton == null) return;
        PostButton.IsEnabled = !IsImportingImages && HasComposerContent;
        BoldButton.Background = Composer.IsBold ? Selected : Brushes.Transparent;
        ItalicButton.Background = Composer.IsItalic ? Selected : Brushes.Transparent;
        HeadingButton.Background = Composer.IsHeading ? Selected : Brushes.Transparent;
        BulletsButton.Background = Composer.IsBulleted ? Selected : Brushes.Transparent;
        NumberingButton.Background = Composer.IsNumbered ? Selected : Brushes.Transparent;
    }
    private void Bold_Click(object sender, RoutedEventArgs e) => Composer.ToggleBold();
    private void Italic_Click(object sender, RoutedEventArgs e) => Composer.ToggleItalic();
    private void Heading_Click(object sender, RoutedEventArgs e) => Composer.ToggleHeading();
    private void Bullets_Click(object sender, RoutedEventArgs e) => Composer.ToggleBullets();
    private void Numbering_Click(object sender, RoutedEventArgs e) => Composer.ToggleNumbering();
    private void Foreground_Click(object sender, RoutedEventArgs e) => TextColorMenu.Open(ForegroundButton, Composer.SetForeground, false);
    private void Highlight_Click(object sender, RoutedEventArgs e) => TextColorMenu.Open(HighlightButton, Composer.SetBackground, true);
    private async void Image_Click(object sender, RoutedEventArgs e)
    {
        if (IsImportingImages) return;
        try { await _composerMedia.PickImagesAsync(this); ClearError("image"); }
        catch (Exception ex) { SetError("image", ImageFailureMessages.Describe(ex), ex); }
    }
    private void Tag_Click(object sender, RoutedEventArgs e)
    {
        Composer.FocusEditor();
        Composer.TextBox.Selection.Text = "#";
        Composer.TextBox.CaretPosition = Composer.TextBox.Selection.End;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (IsImportingImages) return;
        var menu = CreateMoreMenu();
        menu.IsOpen = true;
    }
    private ContextMenu CreateMoreMenu()
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, FontFamily = AppTypography.Text, Padding = new Thickness(6), MinWidth = 200 };
        AddMenu(menu, "Profile", EditProfile);
        menu.Items.Add(new Separator());
        AddWidthMenu(menu, "Wider", true);
        AddWidthMenu(menu, "Narrower", false);
        menu.Items.Add(new Separator());
        AddMenu(menu, "Refresh", () => { if (!IsImportingImages && NotesFeed.SavePendingEdits()) RefreshFeed(); });
        menu.Items.Add(new Separator());
        AddMenu(menu, "Back up library…", Backup);
        AddMenu(menu, "Restore backup…", RestoreBackup);
        AddMenu(menu, "Open data folder", () =>
        {
            try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_store.RootPath)!) { UseShellExecute = true }); }
            catch (Exception ex) { SetError("folder", "The data folder could not be opened.", ex); }
        });
        menu.Items.Add(new Separator());
        AddMenu(menu, "Help", ShowHelp);
        return menu;
    }
    private void AddWidthMenu(ContextMenu menu, string title, bool wider)
    {
        var item = new MenuItem
        {
            Header = title,
            Icon = AppIcons.Create(wider ? "wider" : "narrower", 16),
            Padding = new Thickness(9, 8, 9, 8),
            IsEnabled = CanAdjustWidth(wider, WidthWorkArea())
        };
        item.Click += (_, _) => TryAdjustWidth(wider, WidthWorkArea());
        menu.Items.Add(item);
    }
    private static void AddMenu(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title, Padding = new Thickness(9, 8, 9, 8) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
    private void LoadProfile()
    {
        try
        {
            _profile = _store.LoadProfile();
            NotesFeed.Profile = _profile;
            if (_store.ProfileRecoveryNotice is { } notice)
                SetError("profile", "Your profile needs recovery. Notes are still available.", new IOException(notice));
        }
        catch (Exception error)
        {
            _profile = new UserProfile();
            NotesFeed.Profile = _profile;
            SetError("profile", "Your profile could not be loaded. Notes are still available.", error);
        }
    }
    private void EditProfile() => ShowProfile(false);
    private void ChangeProfilePicture() => ShowProfile(true);
    private void ShowProfile(bool choosePicture)
    {
        if (IsImportingImages) return;
        var dialog = new ProfileDialog(_store, _profile, TryApplyProfile) { Owner = this };
        if (choosePicture) dialog.Loaded += (_, _) => dialog.ChoosePicture();
        dialog.ShowDialog();
    }
    private bool TryApplyProfile(UserProfile profile)
    {
        try
        {
            _store.SaveProfile(profile);
            _profile = new UserProfile
            {
                DisplayName = profile.DisplayName.Normalize().Trim(),
                Avatar = profile.Avatar is null ? null : new NoteImage
                {
                    AssetName = profile.Avatar.AssetName,
                    PixelWidth = profile.Avatar.PixelWidth,
                    PixelHeight = profile.Avatar.PixelHeight
                }
            };
            NotesFeed.ApplyProfile(_profile);
            ClearError("profile");
            return true;
        }
        catch (Exception error)
        {
            SetError("profile", "Profile not saved. Your previous profile is unchanged.", error);
            return false;
        }
    }
    private string BackupFolder => Path.Combine(Path.GetDirectoryName(_store.RootPath)!, "Backups");
    private void SafetyBackup(string label)
    {
        Directory.CreateDirectory(BackupFolder);
        _store.Backup(Path.Combine(BackupFolder, $"ClearNote-{label}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip"));
    }
    private void Backup()
    {
        if (IsImportingImages || !NotesFeed.SavePendingEdits() || !SaveDraft()) return;
        try { SafetyBackup("backup"); ClearError("backup"); }
        catch (Exception ex) { SetError("backup", "The backup could not be saved.", ex); }
    }
    private void RestoreBackup()
    {
        if (IsImportingImages || !NotesFeed.SavePendingEdits() || !SaveDraft()) return;
        var picker = new OpenFileDialog { Title = "Restore a ClearNote backup", InitialDirectory = Directory.Exists(BackupFolder) ? BackupFolder : _store.RootPath, Filter = "ClearNote backup|*.zip" };
        if (picker.ShowDialog(this) != true) return;
        if (DialogBox.Show(this, "Replace your library with this backup? Your current data will be preserved first.", "Restore library", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            SafetyBackup("before-restore");
            _store.Restore(picker.FileName);
            LoadProfile();
            _reviewRecorded.Clear();
            _reviewMode = false;
            _binding = true;
            Composer.Load(new CardDocument(), true);
            _composerMedia.Load([], true);
            _binding = false;
            _draftId = Guid.NewGuid().ToString("N");
            LoadDraft();
            RefreshFeed();
            UpdateSelection();
            ClearError("restore");
        }
        catch (Exception ex) { SetError("restore", "The backup could not be restored. Your previous data is preserved.", ex); }
    }
    private void ShowHelp() => DialogBox.Show(this,
        "Write above and post with Ctrl+Enter. Each feed keeps its own local draft. Use Markdown headings, emphasis, quotes, and lists. Enter continues a numbered list. Code fences stay plain text.\n\nType #tag to add a tag; choose an existing suggestion with Tab or Enter. Click Review and choose a tag from its menu.\n\nAttach up to four images with the image button or paste. Images stay with your notes and backups.\n\nChoose Profile from the menu to set your name and picture. Your local profile appears on every note.\n\nNotes holds learning material. Memo keeps everyday reference, Opinions collects viewpoints and excerpts, and Analysis keeps economic research. Each has an independent feed and draft. Review uses Notes only. Search finds text in the current feed. Double-click a note to edit, or use its menu to edit or delete.\n\nReview puts due notes first. A note counts once when at least half of it (or half the viewport for a long note) enters view. Returning from another feed starts a new round.\n\nCtrl+S saves drafts and edits. F5 refreshes.\n\nData folder\n" + Path.GetDirectoryName(_store.RootPath), "Using ClearNote");

    private void SetError(string source, string text, Exception error)
    {
        var log = OperationDiagnostics.TryWrite(_store, source, error);
        _errorSource = source;
        ErrorStatus.Text = text;
        ErrorStatus.ToolTip = text + $"\n\nCode: 0x{error.HResult:X8}" + (log == null ? "" : "\n\nDetails: " + log);
        ErrorStatus.Visibility = Visibility.Visible;
    }
    private void ShowInputError(Exception error) => SetError("input",
        error is ImageInputException { InnerException: { } cause } ? ImageFailureMessages.Describe(cause) :
        "That paste could not be inserted. Existing content is preserved.", error);
    private void ClearError(string source)
    {
        if (_errorSource != source) return;
        _errorSource = null;
        ErrorStatus.Visibility = Visibility.Collapsed;
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (IsImportingImages && (e.Key == Key.F5 || Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Enter or Key.S))
        { e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter && Composer.IsKeyboardFocusWithin)
        { PostNote(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        { _draftTimer.Stop(); SaveDraft(); NotesFeed.SavePendingEdits(); e.Handled = true; }
        else if (e.Key == Key.F5)
        { if (NotesFeed.SavePendingEdits()) RefreshFeed(); e.Handled = true; }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        { SearchPopup.IsOpen = false; SearchButton.Focus(); e.Handled = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsImportingImages) { _closeAfterImport = true; e.Cancel = true; return; }
        _draftTimer.Stop();
        if (!NotesFeed.SavePendingEdits() || !SaveDraft()) { e.Cancel = true; return; }
    }
    private void UpdateImageImportState()
    {
        var ready = !IsImportingImages;
        foreach (var (button, _) in _collectionButtons) button.IsEnabled = ready;
        foreach (var button in new[] { ReviewButton, SearchButton, MoreButton, ImageButton })
            button.IsEnabled = ready;
        SearchBox.IsEnabled = ready;
        NotesFeed.IsEnabled = ready;
        if (!ready) SearchPopup.IsOpen = false;
        UpdateSelection();
        if (ready && _draftDirty)
        {
            _draftTimer.Stop();
            _draftTimer.Start();
        }
        if (ready && _closeAfterImport)
        {
            _closeAfterImport = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
        }
    }
    private static readonly Brush Ink = BrushesFor("#0F1419");
    private static readonly Brush Muted = BrushesFor("#536471");
    private static readonly Brush Selected = BrushesFor("#EFEFEF");
    private static Brush BrushesFor(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}

