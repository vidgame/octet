using System.Windows;
using System.Windows.Media;

namespace Octet;

internal sealed record MapEntry(StorageItem? Item, string Name, long Size, double Share, Brush Fill, Brush Accent)
{
    public string SizeText => StorageText.Bytes(Size);
    public string ShareText => StorageText.Percent(Share);
    public string Description => $"{Name} · {SizeText} · {ShareText}";
    public string Tooltip => Item is null ? Description : $"{Description}\n{Item.FullPath}";
}

internal static class Treemap
{
    private const int TileLimit = 28;
    private static readonly (Brush Fill, Brush Accent)[] Palette =
    [
        (TileFill("#354C8C", "#26345F"), Brush("#C2D0FF")),
        (TileFill("#235F59", "#1A4442"), Brush("#A0F1DE")),
        (TileFill("#634A7B", "#423452"), Brush("#E8CBFF")),
        (TileFill("#74483B", "#54352D"), Brush("#FFD1BA")),
        (TileFill("#2B5872", "#213F53"), Brush("#B2E4FC")),
        (TileFill("#704662", "#4F3249"), Brush("#F9C6E5")),
        (TileFill("#485D3C", "#35452F"), Brush("#D3EAB4")),
        (TileFill("#6B5533", "#4D3E26"), Brush("#F5DCA4"))
    ];

    public static List<MapEntry> Entries(StorageItem folder)
    {
        var entries = new List<MapEntry>();
        if (folder.Size <= 0) return entries;

        long otherSize = 0;
        var otherCount = 0;
        foreach (var child in folder.Children!)
        {
            if (child.Size <= 0) break;
            var share = 100d * child.Size / folder.Size;
            if (entries.Count == TileLimit || (entries.Count >= 8 && share < 0.4))
            {
                otherSize += child.Size;
                otherCount++;
                continue;
            }

            var (fill, accent) = Palette[entries.Count % Palette.Length];
            entries.Add(new MapEntry(child, child.Name, child.Size, share, fill, accent));
        }

        if (otherCount > 0)
            entries.Add(new MapEntry(null, $"Autres · {StorageText.Count(otherCount)}", otherSize,
                100d * otherSize / folder.Size, Brush("#39485F"), Brush("#9FAFC8")));

        return entries;
    }

    public static Rect[] Layout(IReadOnlyList<MapEntry> entries, double width, double height)
    {
        var boxes = new Rect[entries.Count];
        if (entries.Count > 0 && width > 0 && height > 0)
            Split(0, entries.Count, entries.Sum(entry => entry.Size), new Rect(0, 0, width, height));
        return boxes;

        void Split(int start, int end, long total, Rect box)
        {
            if (end - start == 1)
            {
                boxes[start] = box;
                return;
            }

            var pivot = start + 1;
            var first = entries[start].Size;
            var half = total / 2d;
            while (pivot < end - 1)
            {
                var next = first + entries[pivot].Size;
                if (Math.Abs(next - half) >= Math.Abs(first - half)) break;
                first = next;
                pivot++;
            }

            var fraction = (double)first / total;
            if (box.Width >= box.Height)
            {
                var width = box.Width * fraction;
                Split(start, pivot, first, new Rect(box.X, box.Y, width, box.Height));
                Split(pivot, end, total - first, new Rect(box.X + width, box.Y, box.Width - width, box.Height));
            }
            else
            {
                var height = box.Height * fraction;
                Split(start, pivot, first, new Rect(box.X, box.Y, box.Width, height));
                Split(pivot, end, total - first, new Rect(box.X, box.Y + height, box.Width, box.Height - height));
            }
        }
    }

    private static Brush Brush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush TileFill(string light, string dark)
    {
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(light),
            (Color)ColorConverter.ConvertFromString(dark),
            new Point(0, 0),
            new Point(1, 1));
        brush.Freeze();
        return brush;
    }
}
