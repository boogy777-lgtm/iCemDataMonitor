using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Halliburton.iCemEmule.Core;

/// <summary>
/// Canonical iCemEmule icon (W11.1): rounded light tile + warm border + teal
/// SINE wave — the EMULATOR identity, distinct from the iCemPOC ECG tile.
/// The wave color carries tray semantics: green = active, amber = idle.
/// </summary>
public static class EmuIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Make(int size, Color wave)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size / 32f;

            using var tile = Rounded(1f * s, 1f * s, 30f * s, 30f * s, 6f * s);
            using var tileBrush = new SolidBrush(Color.FromArgb(0xfd, 0xf3, 0xe7)); // warm light tile
            g.FillPath(tileBrush, tile);
            using var tilePen = new Pen(Color.FromArgb(0xc0, 0x56, 0x21), Math.Max(1.2f, 1.6f * s));
            g.DrawPath(tilePen, tile);

            using var pen = new Pen(wave, Math.Max(1.5f, 2f * s))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            var pts = new PointF[17];
            for (int i = 0; i <= 16; i++)
            {
                float x = 5f + 22f * i / 16f;
                float y = 16f - 5.5f * (float)Math.Sin(i * Math.PI / 8);
                pts[i] = new PointF(x * s, y * s);
            }
            g.DrawLines(pen, pts);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    /// <summary>16 px tray icon (status-colored wave).</summary>
    public static Icon Make(Color wave) => Make(16, wave);

    private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
