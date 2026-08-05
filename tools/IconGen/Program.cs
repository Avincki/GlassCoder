// System.IO is not in the implicit-using set for a UseWPF project, unlike a plain console one.
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// Renders the GlassCoder application icon and packs it as a multi-resolution .ico.
///
/// The mark: a pane of pale glass whose fracture has been filled with gold - kintsugi, the house
/// language of the kintsunai logo - where the seam takes the shape of a terminal prompt, '&gt;_'.
/// Glass for the thing the app is for (you can see into the loop), gold in the seam for the house,
/// the prompt for what it does. The prompt reading is what stops a bare chevron from being read as
/// an arrow.
///
/// Every size is drawn from the same unit-square geometry rather than downsampled from one large
/// bitmap, so 16px gets its own stroke weights and its own detail budget instead of a blurred 256.
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        string outIco = args.Length > 0 ? args[0] : "glasscoder.ico";
        string? outPreview = args.Length > 1 ? args[1] : null;

        List<byte[]> pngs = [];
        foreach (int size in Sizes)
        {
            pngs.Add(EncodePng(Render(size)));
        }

        WriteIco(outIco, Sizes, pngs);
        Console.WriteLine($"wrote {outIco} ({new FileInfo(outIco).Length:N0} bytes, {Sizes.Length} sizes)");

        if (outPreview is not null)
        {
            File.WriteAllBytes(outPreview, EncodePng(RenderSheet()));
            Console.WriteLine($"wrote {outPreview}");
        }

        return 0;
    }

    // ---- palette -----------------------------------------------------------------------------

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    // Cool pale ceramic, taken off the kintsunai logo.
    private static readonly Color GlassLight = C("#FAFCFD");
    private static readonly Color GlassMid = C("#DDE8F1");
    private static readonly Color GlassDeep = C("#A6BDD2");
    private static readonly Color EdgeInk = C("#5F7788");

    // Kintsugi gold, pushed darker and more saturated than real leaf so it holds contrast against
    // pale ceramic rather than against the dark lacquer it usually sits on.
    private static readonly Color GoldHi = C("#F5D486");
    private static readonly Color GoldMid = C("#C98A1E");
    private static readonly Color GoldLo = C("#87560F");

    // ---- geometry ----------------------------------------------------------------------------

    // The chevron, as a centreline. Deliberately not symmetric: a crack that is symmetric is a
    // glyph, and the point is that this one is a fracture that happens to land on a glyph.
    private static readonly Point[] Chevron =
    [
        new(0.224, 0.302),
        new(0.312, 0.377),
        new(0.397, 0.442),
        new(0.492, 0.502),
        new(0.394, 0.563),
        new(0.308, 0.628),
        new(0.227, 0.702),
    ];

    // Barely a taper. Kintsugi veins do swell and thin, but a glyph that varies as much as a real
    // fracture stops being a glyph - the first pass tapered 8:1 and read as a swoosh, not a '>'.
    private static readonly double[] ChevronProfile = [0.62, 0.86, 0.95, 1.00, 0.95, 0.86, 0.62];

    private static readonly Point[] Underscore =
    [
        new(0.566, 0.700),
        new(0.697, 0.695),
        new(0.829, 0.702),
    ];

    private static readonly double[] UnderscoreProfile = [0.84, 1.00, 0.84];

    /// <summary>
    /// Deliberately empty, after three tries. Branches struck off at their own angle read as a pin
    /// through the mark; branches continuing the arms end at nearly the same x and the eye joins
    /// them into a vertical bar, so the mark reads '|&gt;'. At icon scale the fracture has to be
    /// carried by the seam itself.
    /// </summary>
    private static readonly (Point From, Point To)[] Branches = [];

    /// <summary>
    /// Deliberately empty. Plate boundaries in the corners read as a dog-eared page rather than as
    /// ceramic, and an icon has no room for detail that has to be explained.
    /// </summary>
    private static readonly (Point From, Point To)[] Facets = [];

    /// <summary>
    /// A tapered vein through the given centreline. Offsetting each vertex along its averaged
    /// normal is what gives the swelling-and-thinning that reads as poured metal rather than as a
    /// stroked path.
    /// </summary>
    private static StreamGeometry Vein(Point[] pts, double[] profile, double halfWidth, double flatten)
    {
        int n = pts.Length;
        Vector[] normals = new Vector[n];

        for (int i = 0; i < n; i++)
        {
            Vector d;
            if (i == 0)
            {
                d = pts[1] - pts[0];
            }
            else if (i == n - 1)
            {
                d = pts[n - 1] - pts[n - 2];
            }
            else
            {
                Vector a = pts[i] - pts[i - 1];
                Vector b = pts[i + 1] - pts[i];
                a.Normalize();
                b.Normalize();
                d = a + b;
            }

            d.Normalize();
            normals[i] = new Vector(-d.Y, d.X);
        }

        double[] hw = new double[n];
        for (int i = 0; i < n; i++)
        {
            // Small renderings flatten the taper towards uniform: a vein that tapers to nothing
            // tapers to nothing at 16px too, and the tips simply disappear.
            double p = profile[i] + ((1.0 - profile[i]) * flatten);
            hw[i] = p * halfWidth;
        }

        StreamGeometry g = new();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(pts[0] + (normals[0] * hw[0]), isFilled: true, isClosed: true);
            for (int i = 1; i < n; i++)
            {
                c.LineTo(pts[i] + (normals[i] * hw[i]), isStroked: true, isSmoothJoin: true);
            }

            for (int i = n - 1; i >= 0; i--)
            {
                c.LineTo(pts[i] - (normals[i] * hw[i]), isStroked: true, isSmoothJoin: true);
            }
        }

        g.Freeze();
        return g;
    }

    // ---- rendering ---------------------------------------------------------------------------

    private static RenderTargetBitmap Render(int size)
    {
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size, size));
            DrawMark(dc, size);
            dc.Pop();
        }

        RenderTargetBitmap bmp = new(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static void DrawMark(DrawingContext dc, int size)
    {
        // Detail budget. Below 32px a hairline is a smudge, so the small renderings carry the
        // silhouette and the seam only - which is the reason each size is drawn rather than scaled.
        bool facets = size >= 40;
        bool branches = size >= 48;
        bool gloss = size >= 24;
        bool molten = size >= 96;

        double flatten = size <= 16 ? 0.72 : size <= 24 ? 0.55 : size <= 32 ? 0.34 : size <= 48 ? 0.16 : 0.0;
        double goldScale = size <= 24 ? 1.18 : size <= 40 ? 1.08 : 1.0;

        const double Radius = 0.215;
        Rect tile = new(0, 0, 1, 1);

        LinearGradientBrush body = new(
            [
                new GradientStop(GlassLight, 0.00),
                new GradientStop(GlassMid, 0.46),
                new GradientStop(GlassDeep, 1.00),
            ],
            new Point(0.06, 0.00),
            new Point(0.94, 1.00));
        body.Freeze();
        dc.DrawRoundedRectangle(body, null, tile, Radius, Radius);

        dc.PushClip(new RectangleGeometry(tile, Radius, Radius));

        if (gloss)
        {
            LinearGradientBrush sheen = new(
                [
                    new GradientStop(Color.FromArgb(0x5E, 0xFF, 0xFF, 0xFF), 0.00),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.60),
                ],
                new Point(0.10, 0.00),
                new Point(0.72, 0.80));
            sheen.Freeze();
            dc.DrawRoundedRectangle(sheen, null, tile, Radius, Radius);
        }

        if (facets)
        {
            Pen facetPen = new(new SolidColorBrush(Color.FromArgb(0x24, 0x46, 0x60, 0x79)), 0.014)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            facetPen.Freeze();
            foreach ((Point from, Point to) in Facets)
            {
                dc.DrawLine(facetPen, from, to);
            }
        }

        if (branches)
        {
            foreach ((Point from, Point to) in Branches)
            {
                // Each branch fades as it leaves the seam, so the tile edge stays clean and the
                // fracture reads as radiating outward rather than as a border.
                LinearGradientBrush fade = new(
                    [
                        new GradientStop(Color.FromArgb(0xCC, GoldMid.R, GoldMid.G, GoldMid.B), 0.00),
                        new GradientStop(Color.FromArgb(0x00, GoldLo.R, GoldLo.G, GoldLo.B), 1.00),
                    ],
                    from,
                    to);
                fade.Freeze();

                Pen pen = new(fade, 0.020)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                pen.Freeze();
                dc.DrawLine(pen, from, to);
            }
        }

        LinearGradientBrush gold = new(
            [
                new GradientStop(GoldHi, 0.00),
                new GradientStop(GoldMid, 0.46),
                new GradientStop(GoldLo, 1.00),
            ],
            new Point(0.24, 0.20),
            new Point(0.80, 0.84));
        gold.Freeze();

        // A dark keyline under the gold so the seam reads as an opening in the glass rather than
        // as something laid on top of it. Kept faint: at full strength it outlines the mark and the
        // whole thing goes from poured metal to sticker.
        Pen keyline = size >= 32
            ? new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0x27, 0x39, 0x4B)), 0.014) { LineJoin = PenLineJoin.Round }
            : null!;
        keyline?.Freeze();

        StreamGeometry chevron = Vein(Chevron, ChevronProfile, 0.056 * goldScale, flatten);
        StreamGeometry underscore = Vein(Underscore, UnderscoreProfile, 0.042 * goldScale, flatten);

        dc.DrawGeometry(gold, keyline, chevron);
        dc.DrawGeometry(gold, keyline, underscore);

        if (molten)
        {
            // The wet highlight real kintsugi has along the crest of the pour.
            StreamGeometry crest = new();
            using (StreamGeometryContext c = crest.Open())
            {
                c.BeginFigure(new Point(0.272, 0.334), isFilled: false, isClosed: false);
                c.LineTo(new Point(0.442, 0.478), isStroked: true, isSmoothJoin: true);
            }

            crest.Freeze();

            Pen crestPen = new(new SolidColorBrush(Color.FromArgb(0x7A, 0xFF, 0xF6, 0xDC)), 0.014)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            crestPen.Freeze();
            dc.DrawGeometry(null, crestPen, crest);
        }

        dc.Pop(); // clip

        // Edge definition, so the tile keeps a shape against a white taskbar or a light title bar.
        Pen edge = new(new SolidColorBrush(Color.FromArgb(0x7A, EdgeInk.R, EdgeInk.G, EdgeInk.B)), 0.018);
        edge.Freeze();
        dc.DrawRoundedRectangle(null, edge, new Rect(0.009, 0.009, 0.982, 0.982), Radius - 0.009, Radius - 0.009);
    }

    /// <summary>A contact sheet: every size at true scale, on a light and a dark ground.</summary>
    private static RenderTargetBitmap RenderSheet()
    {
        const int W = 560, H = 300;
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(C("#FFFFFF")), null, new Rect(0, 0, W, H / 2.0));
            dc.DrawRectangle(new SolidColorBrush(C("#1E2C3C")), null, new Rect(0, H / 2.0, W, H / 2.0));

            for (int row = 0; row < 2; row++)
            {
                double oy = row * (H / 2.0);

                dc.PushTransform(new TranslateTransform(20, oy + 22));
                dc.PushTransform(new ScaleTransform(106, 106));
                DrawMark(dc, 256);
                dc.Pop();
                dc.Pop();

                double x = 150;
                foreach (int size in new[] { 64, 48, 32, 24, 20, 16 })
                {
                    dc.PushTransform(new TranslateTransform(x, oy + 22 + ((64 - size) / 2.0)));
                    dc.PushTransform(new ScaleTransform(size, size));
                    DrawMark(dc, size);
                    dc.Pop();
                    dc.Pop();
                    x += size + 18;
                }
            }
        }

        RenderTargetBitmap bmp = new(W, H, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // ---- encoding ----------------------------------------------------------------------------

    private static byte[] EncodePng(BitmapSource bmp)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using MemoryStream ms = new();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// ICONDIR + one ICONDIRENTRY per image + PNG payloads. PNG-in-ICO is understood by every
    /// Windows since Vista and keeps the file small enough to sit in source control unremarked.
    /// </summary>
    private static void WriteIco(string path, int[] sizes, List<byte[]> pngs)
    {
        using FileStream fs = File.Create(path);
        using BinaryWriter w = new(fs);

        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)sizes.Length);

        int offset = 6 + (16 * sizes.Length);
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (byte[] png in pngs)
        {
            w.Write(png);
        }
    }
}
