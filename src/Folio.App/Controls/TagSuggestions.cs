using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Folio.App.Controls;

/// <summary>Completes existing hashtags without changing the native text editor's focus.</summary>
public sealed class TagSuggestions : IDisposable
{
    private readonly NoteEditor _editor;
    private readonly Func<IReadOnlyList<string>> _tags;
    private readonly ListBox _list;
    private readonly Popup _popup;
    private bool _completing;
    private static readonly Regex Prefix = new(@"(?:^|[^\p{L}\p{M}\p{N}_#-])#(?<tag>[\p{L}\p{M}\p{N}_-]{0,64})$", RegexOptions.CultureInvariant);

    public TagSuggestions(NoteEditor editor, Func<IReadOnlyList<string>> tags)
    {
        _editor = editor;
        _tags = tags;
        _list = new ListBox
        {
            Background = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(3),
            FontFamily = AppTypography.Text, FontSize = 13, MinWidth = 150, MaxWidth = 240,
            MaxHeight = 220, Focusable = false, ItemContainerStyle = ItemStyle()
        };
        _popup = new Popup
        {
            PlacementTarget = editor.TextBox, Placement = PlacementMode.Relative, AllowsTransparency = true,
            StaysOpen = true, Child = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(228, 228, 228)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Child = _list
            }
        };
        _list.PreviewMouseLeftButtonDown += ChooseWithMouse;
        editor.TextBox.TextChanged += Update;
        editor.TextBox.SelectionChanged += Update;
        editor.PreviewKeyDown += KeyDown;
        editor.TextBox.LostKeyboardFocus += CloseOnBlur;
    }

    private void Update(object sender, RoutedEventArgs e)
    {
        if (_completing) return;
        var prefix = CurrentPrefix();
        if (prefix == null) { _popup.IsOpen = false; return; }
        var matches = _tags().Where(tag => tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).Select(tag => "#" + tag).ToList();
        _list.ItemsSource = matches;
        _list.SelectedIndex = matches.Count == 0 ? -1 : 0;
        if (matches.Count == 0 || PresentationSource.FromVisual(_editor.TextBox) == null)
        { _popup.IsOpen = false; return; }
        var caret = _editor.TextBox.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
        _popup.HorizontalOffset = Math.Clamp(caret.Left, 0, Math.Max(0, _editor.ActualWidth - 200));
        _popup.VerticalOffset = caret.Bottom + 5;
        _popup.IsOpen = true;
    }

    private string? CurrentPrefix()
    {
        if (_editor.TextBox.IsReadOnly || !_editor.TextBox.Selection.IsEmpty || _editor.TextBox.CaretPosition.Paragraph is not { } paragraph) return null;
        var before = new TextRange(paragraph.ContentStart, _editor.TextBox.CaretPosition).Text;
        var match = Prefix.Match(before);
        return match.Success ? match.Groups["tag"].Value : null;
    }

    public bool Complete(string tag)
    {
        var prefix = CurrentPrefix();
        if (prefix == null || !_tags().Contains(tag, StringComparer.OrdinalIgnoreCase)) return false;
        var caret = _editor.TextBox.CaretPosition;
        var start = MoveBackText(caret, prefix.Length + 1);
        if (start == null) return false;
        _completing = true;
        _editor.TextBox.BeginChange();
        try
        {
            _editor.TextBox.Selection.Select(start, caret);
            _editor.TextBox.Selection.Text = "#" + tag + " ";
            _editor.TextBox.CaretPosition = _editor.TextBox.Selection.End;
        }
        finally { _editor.TextBox.EndChange(); _completing = false; _popup.IsOpen = false; }
        _editor.FocusEditor();
        return true;
    }

    private static TextPointer? MoveBackText(TextPointer caret, int count)
    {
        var pointer = caret;
        while (count > 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.Text)
            {
                var length = pointer.GetTextRunLength(LogicalDirection.Backward);
                var take = Math.Min(length, count);
                pointer = pointer.GetPositionAtOffset(-take, LogicalDirection.Backward)!;
                count -= take;
            }
            else
            {
                var next = pointer.GetNextContextPosition(LogicalDirection.Backward);
                if (next == null || next.Paragraph != caret.Paragraph) return null;
                pointer = next;
            }
        }
        return pointer;
    }

    private void KeyDown(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down)
        {
            var change = e.Key == Key.Down ? 1 : -1;
            _list.SelectedIndex = (_list.SelectedIndex + change + _list.Items.Count) % _list.Items.Count;
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Tab && _list.SelectedItem is string label)
        { e.Handled = Complete(label[1..]); }
    }

    private void ChooseWithMouse(object sender, MouseButtonEventArgs e)
    {
        for (DependencyObject? item = e.OriginalSource as DependencyObject; item != null; item = VisualTreeHelper.GetParent(item))
        {
            if (item is not ListBoxItem { Content: string label }) continue;
            e.Handled = Complete(label[1..]);
            return;
        }
    }

    private void CloseOnBlur(object sender, KeyboardFocusChangedEventArgs e) => _popup.IsOpen = false;

    public void Dispose()
    {
        _popup.IsOpen = false;
        _editor.TextBox.TextChanged -= Update;
        _editor.TextBox.SelectionChanged -= Update;
        _editor.PreviewKeyDown -= KeyDown;
        _editor.TextBox.LostKeyboardFocus -= CloseOnBlur;
        _list.PreviewMouseLeftButtonDown -= ChooseWithMouse;
    }

    private static Style ItemStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "Row" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new Thickness(9, 7, 9, 7));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(245, 245, 245)), "Row"));
        template.Triggers.Add(selected);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(83, 100, 113))));
        return style;
    }
}

