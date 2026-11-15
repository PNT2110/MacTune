using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace MacTune.Services;

/// <summary>Draws the CPU temperature as a small colored tray icon.</summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static Icon? _last, _prev;

    public static Icon Render(double? tempC)
    {
        const int s = 32;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            Color bg = tempC switch
            {
                null => Color.FromArgb(30, 111, 232),
                >= 90 => Color.FromArgb(224, 58, 58),
                >= 75 => Color.FromArgb(224, 138, 0),
                _ => Color.FromArgb(30, 111, 232),
            };
            using (var path = RoundRect(new RectangleF(0, 0, s - 1, s - 1), 7))
            using (var brush = new SolidBrush(bg))
                g.FillPath(brush, path);

            string text = tempC is double t ? Math.Min(t, 99).ToString("0") : "–";
            using var font = new Font("Segoe UI", text.Length > 1 ? 17 : 20, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, Brushes.White, (s - size.Width) / 2 + 0.5f, (s - size.Height) / 2 + 1);
        }

        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        // keep the icon currently shown alive; dispose the one before it
        var old = _prev;
        _prev = _last;
        _last = icon;
        old?.Dispose();
        return icon;
    }

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
