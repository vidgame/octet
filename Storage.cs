using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;

namespace Octet;

internal sealed class StorageItem(string name, StorageItem? parent, bool isDirectory, long size = 0)
{
    public string Name { get; } = name;
    public StorageItem? Parent { get; } = parent;
    public List<StorageItem>? Children { get; } = isDirectory ? [] : null;
    public bool IsDirectory => Children is not null;
    public long Size { get; set; } = size;
    public int Files { get; set; } = isDirectory ? 0 : 1;
    public int Folders { get; set; }
    public bool ReadError { get; set; }

    public string FullPath
    {
        get
        {
            if (Parent is null) return Name;
            var parts = new Stack<string>();
            for (StorageItem? item = this; item is not null; item = item.Parent)
                parts.Push(item.Name);
            return Path.Join(parts.ToArray());
        }
    }

    public string DisplayName => Parent is null && Path.GetFileName(Name) is { Length: > 0 } leaf ? leaf : Name;
    public string SizeText => ReadError && Files == 0 ? "—" : StorageText.Bytes(Size);
    public string Location => Parent?.FullPath ?? FullPath;
    public string Kind => ReadError ? "Accès partiel" : IsDirectory ? "Dossier" : "Fichier";

    public static readonly IComparer<StorageItem> LargestFirst = Comparer<StorageItem>.Create((left, right) =>
    {
        var bySize = right.Size.CompareTo(left.Size);
        return bySize != 0 ? bySize : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    });
}

internal static class StorageText
{
    private static readonly string[] Units = ["o", "Ko", "Mo", "Go", "To", "Po", "Eo"];
    public static CultureInfo French { get; } = CultureInfo.GetCultureInfo("fr-FR");

    public static string Bytes(long bytes)
    {
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return $"{value.ToString(unit == 0 ? "N0" : value < 10 ? "0.##" : "0.#", French)} {Units[unit]}";
    }

    public static string Count(int count) => count.ToString("N0", French);
    public static string FileCount(int count) => Quantity(count, "fichier", "fichiers");
    public static string Quantity(int count, string singular, string plural) => $"{Count(count)} {(count < 2 ? singular : plural)}";
    public static string Percent(double share) => share is > 0 and < 0.1 ? "< 0,1 %" : share.ToString("0.#", French) + " %";
}

internal readonly record struct ScanProgress(long Bytes, int Files, int Folders, string Path);
internal sealed record ScanResult(StorageItem Root, int Unreadable, int ReparsePoints, TimeSpan Elapsed);
internal sealed record RankedItems(List<StorageItem> Items, int Total);

internal static class StorageScanner
{
    private const int ProgressCheckStride = 64;
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        BufferSize = 16 * 1024,
        IgnoreInaccessible = false
    };

    private sealed class FolderEntries(StorageItem item, string path) : FileSystemEnumerator<StorageItem?>(path, Options)
    {
        public StorageItem Item { get; } = item;
        public string Path { get; } = path;

        protected override StorageItem? TransformEntry(ref FileSystemEntry entry)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var directory = entry.IsDirectory;
            return new StorageItem(entry.FileName.ToString(), Item, directory, directory ? 0 : entry.Length);
        }
    }

    public static ScanResult Scan(string path, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = new StorageItem(path, null, true);
        var stack = new Stack<FolderEntries>();
        var unreadable = 0;
        var reparsePoints = 0;
        var folders = 0;
        var files = 0;
        long bytes = 0;
        long lastReport = 0;
        var entriesUntilProgressCheck = 0;

        try
        {
            cancellation.ThrowIfCancellationRequested();
            stack.Push(new FolderEntries(root, path));
            while (stack.TryPeek(out var frame))
            {
                cancellation.ThrowIfCancellationRequested();
                if (progress is not null && entriesUntilProgressCheck-- == 0)
                {
                    entriesUntilProgressCheck = ProgressCheckStride - 1;
                    var elapsed = clock.ElapsedMilliseconds;
                    if (elapsed - lastReport >= 180)
                    {
                        lastReport = elapsed;
                        progress.Report(new ScanProgress(bytes, files, folders, frame.Path));
                    }
                }

                if (!MoveNext(frame, ref unreadable))
                {
                    stack.Pop().Dispose();
                    entriesUntilProgressCheck = 0;
                    frame.Item.Children!.Sort(StorageItem.LargestFirst);
                    if (frame.Item.Parent is { } parent)
                    {
                        parent.Size += frame.Item.Size;
                        parent.Files += frame.Item.Files;
                        parent.Folders += 1 + frame.Item.Folders;
                    }
                    continue;
                }

                if (frame.Current is not { } child)
                {
                    reparsePoints++;
                    continue;
                }

                frame.Item.Children!.Add(child);
                if (!child.IsDirectory)
                {
                    frame.Item.Size += child.Size;
                    frame.Item.Files++;
                    files++;
                    bytes += child.Size;
                    continue;
                }

                folders++;
                entriesUntilProgressCheck = 0;
                try
                {
                    stack.Push(new FolderEntries(child, Path.Join(frame.Path, child.Name)));
                }
                catch (Exception error) when (IsFileSystemError(error))
                {
                    child.ReadError = true;
                    frame.Item.Folders++;
                    unreadable++;
                }
            }
        }
        finally
        {
            while (stack.TryPop(out var frame)) frame.Dispose();
        }

        cancellation.ThrowIfCancellationRequested();
        return new ScanResult(root, unreadable, reparsePoints, clock.Elapsed);
    }

    public static RankedItems Rank(StorageItem scope, string query, int limit, CancellationToken cancellation)
    {
        var winners = new PriorityQueue<StorageItem, long>();
        var pending = new Stack<StorageItem>();
        var total = 0;
        var names = StorageText.French.CompareInfo;
        pending.Push(scope);

        while (pending.TryPop(out var folder))
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var item in folder.Children!)
            {
                cancellation.ThrowIfCancellationRequested();
                if (item.Children is { Count: > 0 }) pending.Push(item);
                var matches = query.Length == 0
                    ? !item.IsDirectory
                    : names.IndexOf(item.Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
                if (!matches) continue;

                total++;
                if (winners.Count < limit) winners.Enqueue(item, item.Size);
                else if (winners.TryPeek(out _, out var smallest) && item.Size > smallest)
                    winners.DequeueEnqueue(item, item.Size);
            }
        }

        var items = winners.UnorderedItems.Select(pair => pair.Element).ToList();
        items.Sort(StorageItem.LargestFirst);
        return new RankedItems(items, total);
    }

    public static bool IsFileSystemError(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static bool MoveNext(FolderEntries frame, ref int unreadable)
    {
        try
        {
            return frame.MoveNext();
        }
        catch (Exception error) when (frame.Item.Parent is not null && IsFileSystemError(error))
        {
            frame.Item.ReadError = true;
            unreadable++;
            return false;
        }
    }
}
