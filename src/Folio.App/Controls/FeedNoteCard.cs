using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Folio.Core;

namespace Folio.App.Controls;

/// <summary>A note in the feed, with an editor independent of the new-note composer.</summary>
public sealed class FeedNoteCard : UserControl
{
    private readonly TextBlock _date;
    private readonly TextBlock _author;
    private readonly Border _avatar;
    private readonly TextBlock _title;
    private readonly StackPanel _editToolbar;
    private readonly StackPanel _editActions;
    private readonly Button _options;
    private readonly Button _save;
    private readonly Border _shell;
    private readonly LibraryStore? _store;
    private readonly AttachmentPanel? _attachments;
    private bool _saving;

    public CardDocument Card { get; }
    public NoteEditor Body { get; }
    public AttachmentPanel? Attachments => _attachments;
    public bool IsEditing { get; private set; }
    public bool IsImportingImages => _attachments?.IsImporting == true;
    public Func<CardDocument, NoteContent, bool>? SaveNote { get; set; }
    public Func<CardDocument, bool>? DeleteNote { get; set; }
    internal event Action<FeedNoteCard>? Deleted;
    internal event Action<FeedNoteCard>? Saved;
    internal event Action? EditingChanged;
    internal event Action? ImageImportChanged;
    internal event Action? InputChanged;
    public event Action? ProfileRequested;
    public event Action? AvatarChangeRequested;
    public event Action<Exception>? InputRejected;

    public FeedNoteCard(CardDocument card, LibraryStore? store = null, UserProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        Card = card;
        _store = store;
        FontFamily = AppTypography.Text;
        Foreground = Ink;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(this, "Note");
        AutomationProperties.SetAutomationId(this, "FeedNote-" + card.Id);

        var content = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _avatar = CreateAvatar(profile);
        var avatarButton = MakeButton("", "Edit profile", new Thickness(0));
        avatarButton.Content = _avatar;
        avatarButton.Width = AvatarSize;
        avatarButton.Height = AvatarSize;
        avatarButton.Margin = new Thickness(0, 0, 12, 0);
        avatarButton.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(avatarButton, "NoteAvatar-" + card.Id);
        var profileMenu = new ContextMenu { FontFamily = AppTypography.Text, MinWidth = 180 };
        var editProfile = new MenuItem { Header = "Edit profile", Icon = AppIcons.Create("edit", 16) };
        editProfile.Click += (_, _) => ProfileRequested?.Invoke();
        var changePicture = new MenuItem { Header = "Change picture", Icon = AppIcons.Create("image", 16) };
        changePicture.Click += (_, _) => AvatarChangeRequested?.Invoke();
        profileMenu.Items.Add(editProfile);
        profileMenu.Items.Add(changePicture);
        avatarButton.ContextMenu = profileMenu;
        avatarButton.Click += (_, _) => ProfileRequested?.Invoke();
        header.Children.Add(avatarButton);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var displayName = profile?.DisplayName;
        _author = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(displayName) ? "You" : displayName.Trim(),
            FontFamily = AppTypography.Text, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis
        };
        AutomationProperties.SetAutomationId(_author, "NoteAuthor-" + card.Id);
        identity.Children.Add(_author);
        _date = new TextBlock
        {
            FontFamily = AppTypography.Text, FontSize = 11.5, Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        identity.Children.Add(_date);
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);
        _options = MakeButton("\u2026", "More note options", new Thickness(0));
        _options.Content = AppIcons.Create("more", 18);
        _options.Width = 30;
        _options.Height = 28;
        _options.FontSize = 20;
        _options.Margin = new Thickness(8, -3, -7, -3);
        AutomationProperties.SetAutomationId(_options, "NoteOptions-" + card.Id);
        var optionsMenu = new ContextMenu { FontFamily = AppTypography.Text, Placement = PlacementMode.Bottom, MinWidth = 172 };
        var edit = new MenuItem { Header = "Edit note", Icon = AppIcons.Create("edit", 16) };
        edit.Click += (_, _) => BeginEdit();
        var delete = new MenuItem { Header = "Delete note", Icon = AppIcons.Create("delete", 16) };
        delete.Click += (_, _) => TryDelete();
        optionsMenu.Items.Add(edit);
        optionsMenu.Items.Add(delete);
        _options.ContextMenu = optionsMenu;
        _options.Click += (_, _) =>
        {
            optionsMenu.PlacementTarget = _options;
            optionsMenu.IsOpen = true;
        };
        Grid.SetColumn(_options, 2);
        header.Children.Add(_options);
        content.Children.Add(header);

        _title = new TextBlock
        {
            FontFamily = AppTypography.Text, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = Ink, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        content.Children.Add(_title);

        Body = new NoteEditor
        {
            FontFamily = AppTypography.Text, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetAutomationId(Body.TextBox, "FeedNoteText-" + card.Id);
        Body.Changed += (_, _) => InputChanged?.Invoke();
        _editToolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 0, 0, 9),
            Visibility = Visibility.Collapsed
        };
        _editToolbar.Children.Add(FormatIconButton("bold", "Bold", () => Body.ToggleBold()));
        _editToolbar.Children.Add(FormatIconButton("italic", "Italic", () => Body.ToggleItalic()));
        _editToolbar.Children.Add(FormatIconButton("heading", "Heading", () => Body.ToggleHeading()));
        _editToolbar.Children.Add(FormatIconButton("bullets", "Bulleted list", () => Body.ToggleBullets()));
        _editToolbar.Children.Add(FormatIconButton("numbering", "Numbered list", () => Body.ToggleNumbering()));
        var foreground = FormatIconButton("textColor", "Text color", () => { });
        foreground.Click += (_, _) => TextColorMenu.Open(foreground, Body.SetForeground, false);
        _editToolbar.Children.Add(foreground);
        var background = FormatIconButton("highlight", "Selected text background", () => { });
        background.Click += (_, _) => TextColorMenu.Open(background, Body.SetBackground, true);
        _editToolbar.Children.Add(background);
        if (_store is not null)
        {
            var image = FormatIconButton("image", "Add images", PickImages);
            _editToolbar.Children.Add(image);
        }
        content.Children.Add(_editToolbar);

        content.Children.Add(Body);
        if (_store is not null)
        {
            _attachments = new AttachmentPanel(_store);
            content.Children.Add(_attachments);
            Body.AttachmentPasteHandler = _attachments.TryPasteImages;
            _attachments.Changed += (_, _) => InputChanged?.Invoke();
            _attachments.ImportFailed += error => InputRejected?.Invoke(new ImageInputException(error));
            _attachments.ImportingChanged += (_, _) =>
            {
                _editToolbar.IsEnabled = !IsImportingImages;
                ImageImportChanged?.Invoke();
            };
        }

        _editActions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed
        };
        var cancel = MakeButton("Cancel", "Cancel edit", new Thickness(12, 7, 12, 7));
        cancel.Margin = new Thickness(0, 0, 6, 0);
        cancel.Click += (_, _) => CancelEdit();
        _save = MakeButton("Save", "Save note", new Thickness(15, 7, 15, 7));
        _save.Background = Brushes.Transparent;
        _save.Foreground = Ink;
        _save.BorderBrush = FrozenBrush(225, 225, 225);
        _save.BorderThickness = new Thickness(0);
        _save.Click += (_, _) => TrySave();
        if (_attachments is not null)
            _attachments.ImportingChanged += (_, _) => _save.IsEnabled = !IsImportingImages;
        _editActions.Children.Add(cancel);
        _editActions.Children.Add(_save);
        content.Children.Add(_editActions);

        _shell = new Border
        {
            Style = (Style)FindResource("ContentRegion"), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0), Padding = new Thickness(18, 14, 18, 12),
            Child = content
        };
        Content = _shell;
        AddHandler(PreviewMouseDoubleClickEvent, new MouseButtonEventHandler(OnPreviewDoubleClick), handledEventsToo: true);
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += (_, e) =>
        {
            if (!IsEditing) return;
            if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            { TrySave(); e.Handled = true; }
        };
        ShowReadOnly();
    }

    public void BeginEdit()
    {
        if (IsEditing) { Body.FocusEditor(); return; }
        IsEditing = true;
        Body.SetInlineLayout(false);
        Body.MinHeight = 0;
        Body.MaxHeight = 420;
        Body.Load(Card, editing: true);
        _attachments?.Load(NoteText.Read(Card).Images, editable: true);
        _editToolbar.Visibility = Visibility.Visible;
        _editActions.Visibility = Visibility.Visible;
        _options.Visibility = Visibility.Collapsed;
        _shell.BorderBrush = FocusLine;
        Body.FocusEditor();
        EditingChanged?.Invoke();
    }

    private Border CreateAvatar(UserProfile? profile)
    {
        var avatar = new Border
        {
            Width = AvatarSize, Height = AvatarSize, CornerRadius = new CornerRadius(AvatarSize / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Clip = new EllipseGeometry(new Point(AvatarSize / 2, AvatarSize / 2), AvatarSize / 2, AvatarSize / 2)
        };
        AutomationProperties.SetName(avatar, "Author avatar");
        UpdateAvatar(avatar, profile);
        return avatar;
    }

    /// <summary>Updates only the identity, preserving the note, selection and review viewport.</summary>
    public void ApplyProfile(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        UpdateAvatar(_avatar, profile);
        _author.Text = string.IsNullOrWhiteSpace(profile.DisplayName) ? "You" : profile.DisplayName.Trim();
    }

    private void UpdateAvatar(Border avatar, UserProfile? profile)
    {
        avatar.Child = null;
        if (_store is not null && profile?.Avatar is { } image)
        {
            try
            {
                avatar.Child = new Image
                {
                    Source = ImageAttachments.LoadPreview(_store.GetImagePath(image), 96),
                    Width = AvatarSize, Height = AvatarSize, Stretch = Stretch.UniformToFill
                };
                return;
            }
            catch (Exception error) when (error is System.IO.InvalidDataException or System.IO.IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException)
            {
                // The author identity must remain readable even when a local avatar is unavailable.
                OperationDiagnostics.TryWrite(_store, "avatar-preview", error);
            }
        }
        var mark = AppIcons.Create("brand", 30);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        avatar.Child = mark;
    }

    public NoteContent CaptureEdit()
    {
        var content = Body.Capture();
        content.Images = _attachments?.Capture() ?? NoteText.Read(Card).Images;
        return content;
    }

    /// <summary>Keep the editor and its contents open if persistence fails.</summary>
    public bool TrySave()
    {
        if (!IsEditing) return true;
        if (_saving || IsImportingImages || SaveNote is null) return false;
        _saving = true;
        _save.IsEnabled = false;
        try
        {
            Body.CompleteInput();
            var content = CaptureEdit();
            if (!SaveNote(Card, content)) return false;
            // The callback has durably accepted this content before the display changes.
            Card.Note = content;
            ShowReadOnly();
            EditingChanged?.Invoke();
            Saved?.Invoke(this);
            return true;
        }
        finally { _saving = false; _save.IsEnabled = true; }
    }

    public void CancelEdit()
    {
        if (!IsEditing || _saving || IsImportingImages) return;
        ShowReadOnly();
        EditingChanged?.Invoke();
    }

    private void TryDelete()
    {
        if (_saving || IsImportingImages || DeleteNote is null || !DeleteNote(Card)) return;
        Deleted?.Invoke(this);
    }

    private void ShowReadOnly()
    {
        IsEditing = false;
        Body.MinHeight = 0;
        Body.MaxHeight = double.PositiveInfinity;
        Body.Load(Card, editing: false);
        _attachments?.Load(NoteText.Read(Card).Images, editable: false);
        Body.SetInlineLayout(true);
        _date.Text = Card.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        _title.Text = Card.Title.Trim();
        _title.Visibility = _title.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _editToolbar.Visibility = Visibility.Collapsed;
        _editActions.Visibility = Visibility.Collapsed;
        _options.Visibility = Visibility.Visible;
        _shell.BorderBrush = Line;
    }

    private async void PickImages()
    {
        if (!IsEditing || _store is null || _attachments is null) return;
        try { await _attachments.PickImagesAsync(Window.GetWindow(this)); }
        catch (Exception error)
        { InputRejected?.Invoke(new ImageInputException(error)); }
    }

    private void OnPreviewDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsEditing || _saving || SaveNote is null) return;
        // Intercept before RichTextBox selects a word, but let buttons, menus and scrollbars
        // keep their own gestures. Editing never reloads an already active draft.
        for (var current = e.OriginalSource as DependencyObject; current is not null && current != this; current = ParentOf(current))
            if (current is ButtonBase or MenuItem or ScrollBar or Hyperlink) return;
        BeginEdit();
        e.Handled = true;
    }

    private static DependencyObject? ParentOf(DependencyObject child)
    {
        if (child is FrameworkContentElement content) return content.Parent;
        return child is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsEditing) return;
        if (_attachments is { HasImages: true } && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            for (var current = e.OriginalSource as DependencyObject; current is not null && current != this; current = ParentOf(current))
                if (ReferenceEquals(current, _attachments)) return;
        // A read-only RichTextBox still handles the wheel; route it to the feed instead.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroll) continue;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta);
            e.Handled = true;
            return;
        }
    }

    private Button FormatButton(string label, string name, Action action)
    {
        var button = MakeButton(label, name, new Thickness(0));
        button.Width = 31;
        button.Height = 29;
        button.Margin = new Thickness(0, 0, 3, 0);
        button.Focusable = false;
        button.Click += (_, _) => action();
        return button;
    }

    private Button FormatIconButton(string icon, string name, Action action)
    {
        var button = FormatButton("", name, action);
        button.Content = AppIcons.Create(icon, 17);
        return button;
    }

    internal static Button MakeButton(string label, string name, Thickness padding, double cornerRadius = 6)
    {
        var shell = new FrameworkElementFactory(typeof(Border)) { Name = "Shell" };
        shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        shell.SetBinding(Border.BackgroundProperty, ParentBinding(nameof(Button.Background)));
        shell.SetBinding(Border.BorderBrushProperty, ParentBinding(nameof(Button.BorderBrush)));
        shell.SetBinding(Border.BorderThicknessProperty, ParentBinding(nameof(Button.BorderThickness)));
        shell.SetBinding(Border.PaddingProperty, ParentBinding(nameof(Button.Padding)));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        shell.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = shell };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, FrozenBrush(247, 247, 247), "Shell"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.45, "Shell"));
        template.Triggers.Add(disabled);
        var button = new Button
        {
            Content = label, FontFamily = AppTypography.Text, FontSize = 12.5,
            Foreground = Ink, Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = padding, Template = template, Cursor = Cursors.Hand,
            MinHeight = 28, ToolTip = name
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static Binding ParentBinding(string property) => new(property)
    { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) };

    private const double AvatarSize = 42;
    private static readonly Brush Ink = FrozenBrush(15, 20, 25);
    private static readonly Brush Muted = FrozenBrush(83, 100, 113);
    private static readonly Brush Line = FrozenBrush(239, 240, 241);
    private static readonly Brush FocusLine = FrozenBrush(184, 184, 184);
    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}

