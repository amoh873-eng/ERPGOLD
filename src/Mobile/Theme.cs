using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace ERPSystem.Mobile;

/// <summary>
/// نظام تصميم احترافي (Modern Enterprise UI): يركز على الوضوح، السرعة، وسهولة الاستخدام.
/// </summary>
public static class Theme
{
    // ── لوحة الألوان (Professional Brand Colors) ──
    public static readonly Color Primary = Color.FromArgb("#4F46E5"); // Indigo
    public static readonly Color PrimarySoft = Color.FromArgb("#F5F7FF");
    public static readonly Color Bg = Color.FromArgb("#F9FAFB");
    public static readonly Color Card = Color.FromArgb("#FFFFFF");
    public static readonly Color Border = Color.FromArgb("#E5E7EB");
    public static readonly Color Ink = Color.FromArgb("#111827");
    public static readonly Color Muted = Color.FromArgb("#6B7280");

    public static readonly Color Success = Color.FromArgb("#10B981");
    public static readonly Color SuccessSoft = Color.FromArgb("#ECFDF5");
    public static readonly Color Danger = Color.FromArgb("#EF4444");
    public static readonly Color DangerSoft = Color.FromArgb("#FEF2F2");
    public static readonly Color Warning = Color.FromArgb("#F59E0B");
    public static readonly Color WarningSoft = Color.FromArgb("#FFFBEB");
    public static readonly Color Secondary = Color.FromArgb("#7C3AED");
    public static readonly Color SecondarySoft = Color.FromArgb("#F5F3FF");

    public static readonly double CornerRadiusSize = 16;

    // ── المكونات (Components) ──
    public static Border CardView(params View[] children)
    {
        var inner = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(16) };
        foreach (var c in children) if (c != null) inner.Add(c);
        return new Border
        {
            BackgroundColor = Card,
            Stroke = Border,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(CornerRadiusSize) },
            Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Offset = new Point(0, 2), Radius = 4, Opacity = 0.05f },
            Margin = new Thickness(0, 4),
            Content = inner
        };
    }

    public static Label H1(string text) => new Label { Text = text, FontSize = 24, FontAttributes = FontAttributes.Bold, TextColor = Ink, Margin = new Thickness(0, 4) };
    public static Label H2(string text) => new Label { Text = text, FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Ink };
    public static Label P(string text, int size = 14) => new Label { Text = text, FontSize = size, TextColor = Muted, LineBreakMode = LineBreakMode.WordWrap };
    public static Label Section(string text) => new Label { Text = text, FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = Muted, Margin = new Thickness(0, 10, 0, 4) };

    public static Button Filled(string text) => new Button
    {
        Text = text,
        BackgroundColor = Primary,
        TextColor = Colors.White,
        CornerRadius = (int)CornerRadiusSize,
        FontAttributes = FontAttributes.Bold,
        HeightRequest = 52,
        FontSize = 16
    };

    public static Button Ghost(string text) => new Button
    {
        Text = text,
        BackgroundColor = Colors.Transparent,
        TextColor = Primary,
        BorderColor = Primary,
        BorderWidth = 1.5,
        CornerRadius = (int)CornerRadiusSize,
        HeightRequest = 48,
        FontSize = 14
    };

    public static Border Chip(string text, Color bg, Color fg)
    {
        return new Border
        {
            BackgroundColor = bg,
            StrokeThickness = 0,
            Padding = new Thickness(12, 4),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
            Content = new Label { Text = text, FontSize = 12, TextColor = fg, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center }
        };
    }

    public static Picker Dropdown(string title)
    {
        return new Picker { Title = title, FontSize = 15, TextColor = Ink, BackgroundColor = Color.FromArgb("#F3F4F6"), HeightRequest = 48 };
    }

    public static Entry Input(string placeholder, string text = "")
    {
        return new Entry { Placeholder = placeholder, Text = text, FontSize = 15, TextColor = Ink, BackgroundColor = Color.FromArgb("#F3F4F6"), HeightRequest = 48, Margin = new Thickness(0, 4) };
    }

    public static Entry LargeInput(string placeholder, string text = "")
    {
        return new Entry
        {
            Placeholder = placeholder,
            Text = text,
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            TextColor = Ink,
            BackgroundColor = Color.FromArgb("#EEF2FF"),
            HeightRequest = 60,
            Margin = new Thickness(0, 6)
        };
    }
}
