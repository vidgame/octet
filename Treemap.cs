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
        (Brush("#3C4C91"), Brush("#9AAFFF")),
        (Brush("#206258"), Brush("#70DFC4")),
        (Brush("#664579"), Brush("#D5A7F0")),
        (Brush("#75483C"), Brush("#F2AD93")),
        (Brush("#295A76"), Brush("#88D4F4")),
        (Brush("#703F62"), Brush("#F2A5D3")),
        (Brush("#4A603D"), Brush("#B8DE95")),
        (Brush("#6F5630"), Brush("#F0CA82"))
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
}
