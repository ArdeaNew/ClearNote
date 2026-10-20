using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Folio.Core;

namespace Folio.App.Controls;

internal static class TextColorMenu
{
    public static void Open(Button anchor, Action<string?> apply, bool highlight)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, FontFamily = AppTypography.Text, Padding = new Thickness(6) };
        var reset = new MenuItem { Header = highlight ? "No background" : "Default text", Padding = new Thickness(9, 8, 9, 8) };
        reset.Click += (_, _) => apply(null);
        menu.Items.Add(reset);
        var colors = highlight
            ? new[] { ("Gray", "#EEEEEE"), ("Blue", "#DBEAF7"), ("Green", "#DBEFDE"), ("Pink", "#F7DFE8"), ("Yellow", "#FFF2B3") }
            : new[] { ("Red", "#A84242"), ("Green", "#34724C"), ("Blue", "#326BA6"), ("Purple", "#7955A2"), ("Gray", "#666666") };
        foreach (var (name, value) in colors)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 10, 0), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)) });
            row.Children.Add(new TextBlock { Text = name });
            var item = new MenuItem { Header = row, Padding = new Thickness(9, 7, 9, 7) };
            AutomationProperties.SetName(item, name);
            item.Click += (_, _) => apply(value);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new TextBox { Width = 125, Text = "#", ToolTip = "Custom #RRGGBB · Enter to apply", FontFamily = AppTypography.Text };
        AutomationProperties.SetName(custom, "Custom color, hex value");
        custom.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (!NoteColors.IsValid(custom.Text) || string.IsNullOrWhiteSpace(custom.Text))
            { custom.BorderBrush = Brushes.IndianRed; return; }
            apply(NoteColors.Normalize(custom.Text));
            menu.IsOpen = false;
        };
        menu.Items.Add(new MenuItem { Header = custom, StaysOpenOnClick = true, Padding = new Thickness(5) });
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
