using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// ═══ W10.2: canonical app icon generator ═══
// Vector geometry of the canonical app icon (light tile + green ECG pulse).
// Outputs:
//   assets/iCemPOC.ico          — PNG-packed multi-size icon (16..256)
//   assets/iCemPOC_256.png      — large PNG (shortcuts, docs)
//   WebApp/public/favicon.png   — 32 px web favicon (SPA dist source; W20:
//                                 legacy Tray/wwwroot is decommissioned)
Console.WriteLine("IconGen: rendering canonical iCemPOC icon...");

string? repoRoot = null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent!)
{
    if (File.Exists(Path.Combine(d.FullName, "iCemDataMonitor.sln"))) { repoRoot = d.FullName; break; }
}
if (repoRoot is null)
{
    Console.Error.WriteLine("IconGen: iCemDataMonitor.sln not found — run from inside the repo.");
    return 1;
}

var assetsDir = Path.Combine(repoRoot, "assets");
var wwwroot = Path.Combine(repoRoot, "WebApp", "public");
Directory.CreateDirectory(assetsDir);

int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
var entries = new List<(int size, byte[] png)>();
foreach (var size in sizes)
{
    byte[] png = Render(size);
    entries.Add((size, png));
}

// ── multi-size .ico (PNG-compressed entries, Vista+ format) ──
var icoPath = Path.Combine(assetsDir, "iCemPOC.ico");
using (var fs = File.Create(icoPath))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0);          // reserved
    w.Write((ushort)1);          // type: icon
    w.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    foreach (var (size, png) in entries)
    {
        w.Write((byte)(size == 256 ? 0 : size)); // width (0 = 256)
        w.Write((byte)(size == 256 ? 0 : size)); // height
        w.Write((byte)0);                        // palette
        w.Write((byte)0);                        // reserved
        w.Write((ushort)1);                      // planes
        w.Write((ushort)32);                     // bpp
        w.Write(png.Length);                     // bytes
        w.Write(offset);                         // offset
        offset += png.Length;
    }
    foreach (var (_, png) in entries)
        w.Write(png);
}
Console.WriteLine($"  {icoPath} ({sizes.Length} sizes)");

// ── large PNG + favicon ──
File.WriteAllBytes(Path.Combine(assetsDir, "iCemPOC_256.png"), entries[^1].png);
var faviconPath = Path.Combine(wwwroot, "favicon.png");
File.WriteAllBytes(faviconPath, entries.First(e => e.size == 32).png);
// W10.3: browsers render tabs best from a MULTI-size .ico (they pick 16/24/32
// themselves) — a lone 32 px PNG downscales to a faint smudge in a light tab.
var faviconIcoPath = Path.Combine(wwwroot, "favicon.ico");
File.Copy(icoPath, faviconIcoPath, overwrite: true);
Console.WriteLine($"  {faviconIcoPath}");

// ── W11.1: iCemEmule — its OWN icon identity (warm tile + teal sine wave) ──
var emuleEntries = new List<(int size, byte[] png)>();
foreach (var size in sizes)
    emuleEntries.Add((size, RenderEmule(size)));

var emuleIcoPath = Path.Combine(assetsDir, "iCemEmule.ico");
using (var fs = File.Create(emuleIcoPath))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    foreach (var (size, png) in emuleEntries)
    {
        w.Write((byte)(size == 256 ? 0 : size));
        w.Write((byte)(size == 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in emuleEntries)
        w.Write(png);
}
File.WriteAllBytes(Path.Combine(assetsDir, "iCemEmule_256.png"), emuleEntries[^1].png);
Console.WriteLine($"  {emuleIcoPath} ({sizes.Length} sizes)");
Console.WriteLine($"  {Path.Combine(assetsDir, "iCemEmule_256.png")}");

Console.WriteLine("IconGen: done.");
return 0;

static byte[] Render(int size)
{
    using var bmp = new Bitmap(size, size);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size / 32f;

        using var tile = RoundedRect(1f * s, 1f * s, 30f * s, 30f * s, 6f * s);
        using var tileBrush = new SolidBrush(Color.FromArgb(0xf4, 0xf4, 0xfa)); // light tile
        g.FillPath(tileBrush, tile);
        using var tilePen = new Pen(Color.FromArgb(0x0f, 0x52, 0x96), Math.Max(1.2f, 1.6f * s));
        g.DrawPath(tilePen, tile);

        var pts = new[]
        {
            new PointF(3.5f, 16.5f), new PointF(9.5f, 16.5f),
            new PointF(11.5f, 8.5f), new PointF(13.5f, 24.5f), new PointF(15.5f, 16.5f),
            new PointF(20.5f, 16.5f), new PointF(22.5f, 12.5f), new PointF(23.5f, 20.5f),
            new PointF(27.5f, 16.5f),
        };
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new PointF(pts[i].X * s, pts[i].Y * s);

        using var pulse = new Pen(Color.FromArgb(0x00, 0xb0, 0x5c), Math.Max(1.4f, 1.8f * s))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pulse, pts);
    }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

/// <summary>iCemEmule identity (W11.1): warm tile + teal sine wave — distinct
/// from the iCemPOC ECG tile.</summary>
static byte[] RenderEmule(int size)
{
    using var bmp = new Bitmap(size, size);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size / 32f;

        using var tile = RoundedRect(1f * s, 1f * s, 30f * s, 30f * s, 6f * s);
        using var tileBrush = new SolidBrush(Color.FromArgb(0xfd, 0xf3, 0xe7)); // warm light tile
        g.FillPath(tileBrush, tile);
        using var tilePen = new Pen(Color.FromArgb(0xc0, 0x56, 0x21), Math.Max(1.2f, 1.6f * s));
        g.DrawPath(tilePen, tile);

        using var pen = new Pen(Color.FromArgb(0x00, 0x79, 0x6b), Math.Max(1.5f, 2f * s))
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

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
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
