using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Folio.App;

/// <summary>Application dialogs use the same typography and English labels as the workspace.</summary>
internal static class DialogBox
{
    public static MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        => Show(null, message, title, buttons, image);

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new Window
        {
            Title = title + " · ClearNote", Owner = owner, Width = 460,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Background = Brushes.Transparent,
            FontFamily = AppTypography.Text, FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(15, 20, 25))
        };
        var panel = new StackPanel { Margin = new Thickness(29) };
        panel.Children.Add(new TextBlock { Text = title, FontFamily = AppTypography.Text, FontSize = 23, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Foreground = new SolidColorBrush(Color.FromRgb(83, 100, 113)), MaxHeight = 450 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0) };
        var result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
        if (buttons != MessageBoxButton.OK)
        {
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 96, Margin = new Thickness(0, 0, 9, 0) };
            cancel.Click += (_, _) => dialog.Close();
            actions.Children.Add(cancel);
        }
        var accept = new Button
        {
            Content = buttons == MessageBoxButton.OK ? "OK" : "Continue", IsDefault = true,
            IsCancel = buttons == MessageBoxButton.OK, Width = 96,
            Style = (Style)Application.Current.FindResource("PrimaryButton")
        };
        accept.Click += (_, _) => { result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK; dialog.Close(); };
        actions.Children.Add(accept); panel.Children.Add(actions);
        var frame = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel
        };
        frame.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) dialog.DragMove(); };
        dialog.Content = frame;
        dialog.ShowDialog();
        return result;
    }
}


