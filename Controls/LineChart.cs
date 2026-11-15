using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MacTune.Controls;

/// <summary>Lightweight rolling line chart with smooth fill and live endpoint indicator.</summary>
public sealed class LineChart : FrameworkElement
{
    readonly List<double> _values = new();

    public int Capacity { get; set; } = 90;
    public double? FixedMax { get; set; }
    public string Unit { get; set; } = "";
    public Color Stroke { get; set; } = Color.FromRgb(0x00, 0x71, 0xE3);

    public void Push(double? v)
    {
        if (v is not double d || double.IsNaN(d)) return;
        _values.Add(d);
        if (_values.Count > Capacity) _values.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var gridBrush = new SolidColorBrush(Color.FromArgb(22, 15, 23, 42));
        var gridPen = new Pen(gridBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
        gridPen.Freeze();

        for (int i = 1; i < 4; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        if (_values.Count < 2) return;

        double max = FixedMax ?? Math.Max(1, _values.Max() * 1.18);
        double padRight = 6;
        double usableW = Math.Max(10, w - padRight);
        double step = usableW / (Capacity - 1);
        double x0 = usableW - step * (_values.Count - 1);
        Point P(int i) => new(x0 + i * step, h - Math.Clamp(_values[i] / max, 0, 1) * (h - 10) - 5);

        var pts = new Point[_values.Count];
        for (int i = 0; i < _values.Count; i++) pts[i] = P(i);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Length; i++)
            {
                var mid = new Point((pts[i - 1].X + pts[i].X) / 2, (pts[i - 1].Y + pts[i].Y) / 2);
                g.QuadraticBezierTo(pts[i - 1], mid, true, true);
            }
            g.LineTo(pts[^1], true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(pts[0].X, h), true, true);
            g.LineTo(pts[0], false, false);
            for (int i = 1; i < pts.Length; i++)
            {
                var mid = new Point((pts[i - 1].X + pts[i].X) / 2, (pts[i - 1].Y + pts[i].Y) / 2);
                g.QuadraticBezierTo(pts[i - 1], mid, false, true);
            }
            g.LineTo(pts[^1], false, true);
            g.LineTo(new Point(pts[^1].X, h), false, true);
        }
        area.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(56, Stroke.R, Stroke.G, Stroke.B),
            Color.FromArgb(2, Stroke.R, Stroke.G, Stroke.B), 90);
        fill.Freeze();

        var strokeBrush = new SolidColorBrush(Stroke);
        strokeBrush.Freeze();
        var strokePen = new Pen(strokeBrush, 2.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        strokePen.Freeze();

        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, strokePen, line);

        // Live endpoint dot with soft halo
        var last = pts[^1];
        var haloBrush = new SolidColorBrush(Color.FromArgb(48, Stroke.R, Stroke.G, Stroke.B));
        haloBrush.Freeze();
        dc.DrawEllipse(haloBrush, null, last, 6.5, 6.5);
        dc.DrawEllipse(Brushes.White, strokePen, last, 3.5, 3.5);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelBrush = new SolidColorBrush(Color.FromArgb(130, 100, 116, 139));
        labelBrush.Freeze();
        var label = new FormattedText($"{max:0}{Unit}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text, Segoe UI"), 11, labelBrush, dpi);
        dc.DrawText(label, new Point(2, 2));
    }
}
