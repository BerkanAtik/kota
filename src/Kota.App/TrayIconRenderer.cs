using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Kota.App;

/// <summary>Draws the tray icon: a colored tile with the current usage percentage on it.</summary>
static class TrayIconRenderer
{
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public static Icon Render(double? percent)
    {
        int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var background = percent is { } p ? ToDrawing(Theme.SeverityColor(p)) : Color.FromArgb(120, 120, 120);
            using (var path = RoundedRect(new RectangleF(0, 0, size, size), size * 0.24f))
            using (var brush = new SolidBrush(background))
                g.FillPath(brush, path);

            string text = percent switch
            {
                null => "–",
                >= 100 => "!",
                var v => Math.Min(99, (int)Math.Round(v.Value)).ToString(),
            };
            float fontPixels = size * (text.Length >= 2 ? 0.66f : 0.78f);
            using var font = new Font("Segoe UI", fontPixels, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var textColor = percent is >= 70 and < 90 ? Color.FromArgb(32, 24, 0) : Color.White;
            using var textBrush = new SolidBrush(textColor);
            g.DrawString(text, font, textBrush, new RectangleF(-size, 0.5f, size * 3, size), format);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    static Color ToDrawing(System.Windows.Media.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
