using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace Folio.App;

/// <summary>Small, open line drawings with a consistent optical weight at any UI size.</summary>
public static class AppIcons
{
    private static readonly Brush Slate = CreateBrush("#536471");
    private static readonly Brush BrandInk = CreateBrush("#141414");

    public static FrameworkElement Create(string key, double size = 18)
    {
        if (!double.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (key == "brand") return CreateBrand(size);
        var drawing = key switch
        {
            "search" => "M16.5,10.5 A6,6 0 1 1 4.5,10.5 A6,6 0 1 1 16.5,10.5 M15,15 L20,20",
            "more" => "M5.1,12 A1.1,1.1 0 1 1 2.9,12 A1.1,1.1 0 1 1 5.1,12 M13.1,12 A1.1,1.1 0 1 1 10.9,12 A1.1,1.1 0 1 1 13.1,12 M21.1,12 A1.1,1.1 0 1 1 18.9,12 A1.1,1.1 0 1 1 21.1,12",
            "edit" => "M14.4,5.6 L18.4,9.6 M5,15 L15.5,4.5 Q16.3,3.7 17.1,4.5 L19.5,6.9 Q20.3,7.7 19.5,8.5 L9,19 L4,20 Z",
            "delete" => "M5.5,7 H18.5 M9,7 V4.5 H15 V7 M7,7 L7.8,19.5 H16.2 L17,7 M10,10.5 V16 M14,10.5 V16",
            "image" => "M4.5,5 H19.5 V19 H4.5 Z M4.5,15 L9,10.5 L13.5,15 L16,12.5 L19.5,16 M16.2,8.5 A1,1 0 1 1 14.2,8.5 A1,1 0 1 1 16.2,8.5",
            "tag" => "M4.5,4.5 H11 L20,13.5 L13.5,20 L4.5,11 Z M9,8.2 A0.8,0.8 0 1 1 7.4,8.2 A0.8,0.8 0 1 1 9,8.2",
            "bold" => "M7,4.5 H12.5 C18,4.5 18,11.5 12.5,11.5 H7 M7,4.5 V19.5 H13 C19,19.5 19,11.5 12.5,11.5",
            "italic" => "M10,4.5 H18 M6,19.5 H14 M14,4.5 L10,19.5",
            "heading" => "M5.5,5 V19 M18.5,5 V19 M5.5,12 H18.5",
            "bullets" => "M9,6 H20 M9,12 H20 M9,18 H20 M4.2,6 A0.7,0.7 0 1 1 2.8,6 A0.7,0.7 0 1 1 4.2,6 M4.2,12 A0.7,0.7 0 1 1 2.8,12 A0.7,0.7 0 1 1 4.2,12 M4.2,18 A0.7,0.7 0 1 1 2.8,18 A0.7,0.7 0 1 1 4.2,18",
            "numbering" => "M10,6 H20 M10,12 H20 M10,18 H20 M3,4.7 L4.5,3.5 V8 M2.8,8 H6 M2.8,13 C2.8,10.7 6,10.7 6,12.7 C6,14 3,15.4 2.8,16.5 H6 M3,19 H5.5",
            "textColor" => "M6,17 L12,4 L18,17 M8.3,12 H15.7 M5,20 H19",
            "highlight" => "M6,13 L13,6 L18,11 L11,18 Z M7.5,11.5 L12.5,16.5 M13,6 L15,4 L20,9 L18,11 M6,13 L4,18 L7,20 L11,18 M4,21 H17",
            "close" => "M6.5,6.5 L17.5,17.5 M17.5,6.5 L6.5,17.5",
            "wider" => "M8,5 H4 V19 H8 M16,5 H20 V19 H16 M9,12 H15 M11,10 L9,12 L11,14 M13,10 L15,12 L13,14",
            "narrower" => "M4,5 H8 V19 H4 M20,5 H16 V19 H20 M3,12 H7 M5,10 L7,12 L5,14 M21,12 H17 M19,10 L17,12 L19,14",
            _ => throw new ArgumentException($"Unknown icon: {key}", nameof(key))
        };
        var geometry = Geometry.Parse(drawing).Clone();
        geometry.Transform = new ScaleTransform(size / 24, size / 24);
        geometry.Freeze();
        var canvas = new Canvas { Width = size, Height = size, IsHitTestVisible = false, Focusable = false };
        canvas.Children.Add(new Path
        {
            Data = geometry,
            Stroke = Slate,
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = null
        });
        return canvas;
    }

    private static FrameworkElement CreateBrand(double size)
    {
        var canvas = new Canvas { Width = size, Height = size, IsHitTestVisible = false, Focusable = false };
        AddShape("M96,151 L138,163 L140,184 C162,194 177,212 183,238 L54,238 C60,210 75,191 101,184 L105,169 Z", BrandInk);
        AddShape("M130,18 C83,11 42,42 43,82 C44,114 63,137 88,153 C100,161 111,174 127,176 C146,177 160,159 168,145 L180,130 C185,124 190,121 188,116 L178,105 C188,71 181,32 147,21 C141,19 135,18 130,18 Z", BrandInk);
        AddShape("M73,93 C89,91 109,99 122,118 C103,122 85,112 73,93 Z M144,114 C151,103 163,98 174,99 C169,113 158,121 144,124 Z", Brushes.White);
        AddShape("M143,168 C137,182 138,194 145,208 L158,238 L171,238 L155,202 C150,190 150,182 159,166 Z", Brushes.White);
        AddShape("M174,145 C180,146 183,152 181,158 L169,183 L193,230 C195,234 192,238 187,238 L166,238 L148,190 C146,184 147,178 151,170 L158,157 C150,159 145,157 147,152 L161,142 C165,139 169,139 172,142 Z", BrandInk);
        return canvas;

        void AddShape(string drawing, Brush fill)
        {
            var geometry = Geometry.Parse(drawing).Clone();
            geometry.Transform = new ScaleTransform(size / 256, size / 256);
            geometry.Freeze();
            canvas.Children.Add(new Path { Data = geometry, Fill = fill });
        }
    }

    private static Brush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
