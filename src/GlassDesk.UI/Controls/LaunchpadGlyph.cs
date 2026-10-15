using System.Windows;
using System.Windows.Media;

namespace GlassDesk.UI.Controls;

/// <summary>The same nine-app glyph travels from the Dock into the expanding container.</summary>
public sealed class LaunchpadGlyph : FrameworkElement
{
    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush), typeof(Brush), typeof(LaunchpadGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Optional theme-colored outline; group tiles retain their colorful glyph.</summary>
    public Brush? OutlineBrush
    {
        get => (Brush?)GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        base.OnRender(dc);
        var unit = Math.Min(ActualWidth, ActualHeight) / 48;
        if (unit <= 0) return;
        if (OutlineBrush is { } outline)
        {
            var pen = new Pen(outline, 3.5 * unit) { LineJoin = PenLineJoin.Round };
            for (var i = 0; i < 9; i++)
            {
                var box = new Rect((5.75 + i % 3 * 14) * unit, (5.75 + i / 3 * 14) * unit,
                    8.5 * unit, 8.5 * unit);
                dc.DrawRoundedRectangle(null, pen, box, 1.8 * unit, 1.8 * unit);
            }
            return;
        }
        var colors = new[] { "#BEDBFF", "#C2EEE7", "#E6DCFF", "#D3E5FF", "#F1DCCA", "#C8E9EB", "#DCD9FA", "#BBDEEE", "#EADFEB" };
        for (var i = 0; i < 9; i++)
        {
            var box = new Rect((8 + i % 3 * 11) * unit, (8 + i / 3 * 11) * unit, 9 * unit, 9 * unit);
            dc.DrawGeometry(new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])), null,
                SquircleGlassGeometry.Create(box, 3 * unit));
        }
    }
}
