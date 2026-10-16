using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskSpaceManager.App;

public sealed class TreemapItem
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    public required long Size { get; init; }
    public bool IsFolder { get; init; }
    public List<TreemapItem>? Children { get; init; }
    internal Color Color;
    internal Rect Rect;
}

/// <summary>Squarified treemap: each rectangle's area is proportional to its size. Two levels deep.</summary>
public sealed class Treemap : FrameworkElement
{
    private static readonly Color[] Hues =
    [
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0xF5, 0x9E, 0x0B),
        Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x06, 0xB6, 0xD4),
        Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x84, 0xCC, 0x16), Color.FromRgb(0xF9, 0x73, 0x16),
        Color.FromRgb(0x64, 0x74, 0x8B),
    ];
    private static readonly Color FileColor = Color.FromRgb(0x94, 0xA3, 0xB8);
    private static readonly Typeface Face = new("Segoe UI");

    private List<TreemapItem> _items = [];
    private readonly List<TreemapItem> _drawn = [];
    private TreemapItem? _hover;

    public event Action<TreemapItem?>? HoverChanged;
    public event Action<TreemapItem>? ItemActivated;

    public Treemap()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    public void SetItems(List<TreemapItem> items)
    {
        _items = items;
        int i = 0;
        foreach (var it in _items)
        {
            it.Color = it.IsFolder ? Hues[i++ % Hues.Length] : FileColor;
            if (it.Children is null) continue;
            int j = 0;
            foreach (var c in it.Children)
                c.Color = c.IsFolder ? Shade(it.Color, 0.15 + 0.5 * (j++ % 4) / 4) : Shade(FileColor, 0.2);
        }
        InvalidateVisual();
    }

    private static Color Shade(Color c, double towardWhite) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * towardWhite),
        (byte)(c.G + (255 - c.G) * towardWhite),
        (byte)(c.B + (255 - c.B) * towardWhite));

    protected override void OnRender(DrawingContext dc)
    {
        _drawn.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.White, null, bounds);
        if (_items.Count == 0 || bounds.Width < 4 || bounds.Height < 4) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Layout(_items, bounds);
        var border = new Pen(Brushes.White, 1);
        border.Freeze();

        foreach (var it in _items)
        {
            var r = it.Rect;
            if (r.Width < 1 || r.Height < 1) continue;
            dc.DrawRectangle(Brush(it.Color), border, r);
            _drawn.Add(it);

            bool header = it.Children is { Count: > 0 } && r.Height > 36 && r.Width > 50;
            if (header)
            {
                DrawLabel(dc, $"{it.Label}  {Core.Fmt.Bytes(it.Size)}", new Rect(r.X + 3, r.Y + 1, r.Width - 6, 16), dpi, bold: true);
                var inner = new Rect(r.X + 2, r.Y + 18, Math.Max(0, r.Width - 4), Math.Max(0, r.Height - 20));
                Layout(it.Children!, inner);
                foreach (var c in it.Children!)
                {
                    var cr = c.Rect;
                    if (cr.Width < 1 || cr.Height < 1) continue;
                    dc.DrawRectangle(Brush(c.Color), border, cr);
                    _drawn.Add(c);
                    if (cr.Width > 46 && cr.Height > 16)
                        DrawLabel(dc, c.Label, new Rect(cr.X + 3, cr.Y + 1, cr.Width - 6, 15), dpi, bold: false);
                }
            }
            else if (r.Width > 46 && r.Height > 16)
            {
                DrawLabel(dc, it.Label, new Rect(r.X + 3, r.Y + 1, r.Width - 6, 16), dpi, bold: true);
                if (r.Height > 32)
                    DrawLabel(dc, Core.Fmt.Bytes(it.Size), new Rect(r.X + 3, r.Y + 16, r.Width - 6, 15), dpi, bold: false);
            }
        }

        if (_hover is not null && _hover.Rect.Width >= 1)
        {
            var hp = new Pen(Brushes.Black, 2);
            hp.Freeze();
            dc.DrawRectangle(null, hp, _hover.Rect);
        }
    }

    private static readonly Dictionary<Color, Brush> BrushCache = [];

    private static Brush Brush(Color c)
    {
        if (!BrushCache.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            BrushCache[c] = b;
        }
        return b;
    }

    private static void DrawLabel(DrawingContext dc, string text, Rect area, double dpi, bool bold)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11,
            Brushes.White, dpi)
        {
            MaxTextWidth = Math.Max(1, area.Width),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (bold) ft.SetFontWeight(FontWeights.SemiBold);
        dc.PushClip(new RectangleGeometry(area));
        // Slight shadow keeps white text readable on light tiles.
        var shadow = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11,
            new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), dpi)
        {
            MaxTextWidth = ft.MaxTextWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        if (bold) shadow.SetFontWeight(FontWeights.SemiBold);
        dc.DrawText(shadow, new Point(area.X + 1, area.Y + 1));
        dc.DrawText(ft, area.TopLeft);
        dc.Pop();
    }

    // ---- Squarified layout (Bruls, Huizing, van Wijk) ----

    private static void Layout(List<TreemapItem> items, Rect bounds)
    {
        var list = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        foreach (var i in items) i.Rect = Rect.Empty;
        double total = list.Sum(i => (double)i.Size);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        double scale = bounds.Width * bounds.Height / total;

        var rect = bounds;
        int start = 0;
        while (start < list.Count)
        {
            double side = Math.Min(rect.Width, rect.Height);
            int end = start;
            double rowArea = 0, worst = double.MaxValue;
            while (end < list.Count)
            {
                double a = list[end].Size * scale;
                double newWorst = Worst(list, start, end, rowArea + a, side, scale);
                if (newWorst > worst) break;
                worst = newWorst;
                rowArea += a;
                end++;
            }

            bool horizontal = rect.Width >= rect.Height; // lay the row along the short side
            double thickness = rowArea / side;
            double offset = 0;
            for (int i = start; i < end; i++)
            {
                double len = list[i].Size * scale / thickness;
                list[i].Rect = horizontal
                    ? new Rect(rect.X, rect.Y + offset, thickness, len)
                    : new Rect(rect.X + offset, rect.Y, len, thickness);
                offset += len;
            }
            rect = horizontal
                ? new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height)
                : new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));
            start = end;
        }
    }

    private static double Worst(List<TreemapItem> list, int start, int endInclusive, double rowArea, double side, double scale)
    {
        double max = 0, min = double.MaxValue;
        for (int i = start; i <= endInclusive; i++)
        {
            double a = list[i].Size * scale;
            max = Math.Max(max, a);
            min = Math.Min(min, a);
        }
        double s2 = side * side, r2 = rowArea * rowArea;
        return Math.Max(s2 * max / r2, r2 / (s2 * min));
    }

    // ---- Interaction ----

    private TreemapItem? HitTest(Point p)
    {
        // Children were added after their parent, so search from the end to get the innermost hit.
        for (int i = _drawn.Count - 1; i >= 0; i--)
            if (_drawn[i].Rect.Contains(p)) return _drawn[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        HoverChanged?.Invoke(hit);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        HoverChanged?.Invoke(null);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (HitTest(e.GetPosition(this)) is { } hit) ItemActivated?.Invoke(hit);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
