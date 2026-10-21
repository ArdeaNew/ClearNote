using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Folio.App.Controls;
using Folio.Core;

namespace Folio.App;

/// <summary>Edits a copy of the local identity; only an accepted durable save changes the feed.</summary>
internal sealed class ProfileDialog : Window
{
    private readonly LibraryStore _store;
    private readonly Func<UserProfile, bool> _save;
    private readonly Border _avatar;
    private readonly TextBlock _status;
    private readonly Button _choosePicture;
    private readonly Button _removePicture;
    private readonly Button _accept;
    private readonly Button _cancel;
    private NoteImage? _avatarImage;
    private bool _closeAfterImport;
    private bool _closed;
    public TextBox NameInput { get; }
    public bool IsImporting { get; private set; }
    public event EventHandler? ImportingChanged;

    public ProfileDialog(LibraryStore store, UserProfile profile, Func<UserProfile, bool> save)
    {
        _store = store;
        _save = save;
        _avatarImage = CopyImage(profile.Avatar);
        Title = "Profile · ClearNote";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = AppTypography.Text;
        FontSize = 13;
        Foreground = new SolidColorBrush(Color.FromRgb(15, 20, 25));

        var panel = new StackPanel { Margin = new Thickness(26) };
        var heading = new TextBlock { Text = "Profile", FontSize = 20, Margin = new Thickness(0, 0, 0, 22) };
        heading.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) DragMove(); };
        panel.Children.Add(heading);

        var identity = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition());
        _avatar = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32),
            Background = new SolidColorBrush(Color.FromRgb(244, 244, 244)),
            Margin = new Thickness(0, 0, 16, 0), ClipToBounds = true
        };
        AutomationProperties.SetName(_avatar, "Profile picture preview");
        identity.Children.Add(_avatar);
        var pictureActions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _choosePicture = new Button { Content = "Choose picture", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5, 8, 5) };
        _choosePicture.Click += (_, _) => ChoosePicture();
        _removePicture = new Button { Content = "Remove", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5, 8, 5) };
        _removePicture.Click += (_, _) => { SetAvatar(null); if (_status is not null) _status.Visibility = Visibility.Collapsed; };
        pictureActions.Children.Add(_choosePicture);
        pictureActions.Children.Add(_removePicture);
        Grid.SetColumn(pictureActions, 1);
        identity.Children.Add(pictureActions);
        panel.Children.Add(identity);

        panel.Children.Add(new TextBlock { Text = "Name", Margin = new Thickness(0, 0, 0, 7) });
        NameInput = new TextBox { Text = profile.DisplayName, MaxLength = 160, MinHeight = 38 };
        AutomationProperties.SetName(NameInput, "Display name");
        panel.Children.Add(NameInput);
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(159, 46, 46)), TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        _cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 6, 0) };
        _cancel.Click += (_, _) => Close();
        _accept = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(12, 8, 12, 8) };
        _accept.Click += (_, _) => { if (TrySave()) DialogResult = true; };
        actions.Children.Add(_cancel);
        actions.Children.Add(_accept);
        panel.Children.Add(actions);
        Content = new Border
        {
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(225, 225, 225)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = panel
        };
        UpdateAvatar();
        UpdateActions();
    }

    public bool TrySave()
    {
        if (IsImporting || _closeAfterImport || _closed) return false;
        var name = NameInput.Text.Trim();
        if (name.Length == 0) name = "You";
        if (new System.Globalization.StringInfo(name).LengthInTextElements > 80 ||
            name.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
        {
            ShowError("Use a name of up to 80 characters on one line.");
            return false;
        }
        if (!_save(new UserProfile { DisplayName = name, Avatar = CopyImage(_avatarImage) }))
        {
            ShowError("Profile not saved. Your changes are still here; check the data folder and retry.");
            return false;
        }
        _status.Visibility = Visibility.Collapsed;
        return true;
    }

    public void SetAvatar(NoteImage? image)
    {
        _avatarImage = CopyImage(image);
        UpdateAvatar();
        UpdateActions();
    }

    internal async void ChoosePicture() =>
        await ImportPictureAsync(() => ImageAttachments.ImportFilesAsync(this, _store, remainingSlots: 1));

    private async Task ImportPictureAsync(Func<Task<IReadOnlyList<NoteImage>>> import)
    {
        if (IsImporting || _closeAfterImport || _closed) return;
        SetImporting(true);
        try
        {
            var images = await import();
            if (images.Count == 0) return;
            SetAvatar(images[0]);
            _status.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            OperationDiagnostics.TryWrite(_store, "profile-image", error);
            ShowError(error is InvalidDataException && error.Message.Contains("four images", StringComparison.OrdinalIgnoreCase)
                ? "Choose one picture for your profile."
                : ImageFailureMessages.Describe(error));
        }
        finally
        {
            SetImporting(false);
            if (_closeAfterImport && !_closed)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsImporting)
        {
            _closeAfterImport = true;
            UpdateActions();
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private void SetImporting(bool importing)
    {
        IsImporting = importing;
        UpdateActions();
        ImportingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateActions()
    {
        var ready = !IsImporting && !_closeAfterImport && !_closed;
        _choosePicture.IsEnabled = ready;
        _removePicture.IsEnabled = ready && _avatarImage is not null;
        _accept.IsEnabled = ready;
        _cancel.IsEnabled = !_closeAfterImport && !_closed;
    }

    private void UpdateAvatar()
    {
        _avatar.Background = new SolidColorBrush(Color.FromRgb(244, 244, 244));
        _avatar.Child = null;
        if (_avatarImage is not null)
        {
            try
            {
                _avatar.Background = new ImageBrush(ImageAttachments.LoadPreview(_store.GetImagePath(_avatarImage), 160)) { Stretch = Stretch.UniformToFill };
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or FormatException or COMException)
            { /* A missing picture must leave profile editing available. */ }
        }
        var icon = AppIcons.Create("brand", 32);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        _avatar.Child = icon;
    }

    private void ShowError(string message) { _status.Text = message; _status.Visibility = Visibility.Visible; }
    private static NoteImage? CopyImage(NoteImage? image) => image is null ? null : new NoteImage
    {
        AssetName = image.AssetName, PixelWidth = image.PixelWidth, PixelHeight = image.PixelHeight
    };
}
