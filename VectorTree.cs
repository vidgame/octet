using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace Octet;

internal sealed class VectorTree : Grid
{
    private const int PageSize = 32;
    private const double Padding = 14;
    private const double NodeHeight = 48;
    private const double NodeWidth = 232;
    private const double RootWidth = 248;
    private const double ColumnGap = 32;
    private const double RowGap = 10;
    private const double PagerHeight = 30;

    private readonly Canvas canvas = new();
    private readonly TreeViewport viewport;
    private readonly ShapePath connectors = new() { StrokeThickness = 1.4, Opacity = 0.66, IsHitTestVisible = false };
    private readonly TextBlock empty = new() { TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
    private readonly Dictionary<StorageItem, Node> nodes = new();
    private readonly Dictionary<StorageItem, BranchState> branches = new();
    private Node? root;
    private StorageItem? selected;

    private readonly record struct BranchState(bool Expanded, int Page);
    private sealed record Pager(DockPanel Panel, Button Previous, Button Next, TextBlock Range);

    private sealed class Node(StorageItem item, int level, StackPanel card, Button button)
    {
        public StorageItem Item { get; } = item;
        public int Level { get; } = level;
        public StackPanel Card { get; } = card;
        public Button Button { get; } = button;
        public Button? Toggle { get; set; }
        public Pager? Pages { get; set; }
        public List<Node> Children { get; } = [];
        public Point Position { get; set; }
        public double Width => Level == 0 ? RootWidth : NodeWidth;
        public double Height => NodeHeight + (Pages?.Panel.Visibility == Visibility.Visible ? PagerHeight : 0);
    }

    public event Action<StorageItem>? ItemSelected;
    public event Action<StorageItem>? ItemActivated;

    public VectorTree()
    {
        connectors.SetResourceReference(ShapePath.StrokeProperty, "Dim");
        empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        canvas.Children.Add(connectors);
        canvas.Children.Add(empty);
        KeyboardNavigation.SetDirectionalNavigation(canvas, KeyboardNavigationMode.Contained);
        viewport = new TreeViewport(canvas);
        Children.Add(viewport);
        AutomationProperties.SetName(this, "Arbre des dossiers et des fichiers");
    }

    public void SetFolder(StorageItem? folder)
    {
        var restoreFocus = IsKeyboardFocusWithin;
        if (nodes.Count > 0) canvas.Children.RemoveRange(2, nodes.Count);
        nodes.Clear();
        branches.Clear();
        selected = folder;
        root = null;
        if (folder is null)
        {
            connectors.Data = null;
            empty.Visibility = Visibility.Collapsed;
            canvas.Width = 0;
            canvas.Height = 0;
            return;
        }

        branches[folder] = new BranchState(true, 0);
        root = AddNode(folder, 0);
        BuildBranches(root);
        empty.Text = folder.ReadError ? "Le contenu de ce dossier est inaccessible." : "Ce dossier est vide.";
        empty.Visibility = folder.Children!.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LayoutGraph();
        ResetView();
        if (restoreFocus) root.Button.Focus();
    }

    public void ResetView() => viewport.ResetView();

    public void ZoomBy(int steps) => viewport.ZoomBy(steps);

    public void SetSelected(StorageItem item)
    {
        if (selected == item) return;
        var previous = selected;
        selected = item;
        if (previous is not null && nodes.TryGetValue(previous, out var previousNode)) StyleSelection(previousNode);
        if (nodes.TryGetValue(item, out var node)) StyleSelection(node);
    }

    private void BuildBranches(Node branch)
    {
        var pending = new Stack<Node>();
        pending.Push(branch);
        while (pending.TryPop(out var node))
        {
            var state = branches.GetValueOrDefault(node.Item);
            var children = node.Item.Children;
            if (children is not { Count: > 0 }) continue;

            var action = state.Expanded ? "Replier" : "Déplier";
            node.Toggle!.Content = state.Expanded ? "−" : "+";
            node.Toggle.ToolTip = $"{action} {node.Item.DisplayName}";
            AutomationProperties.SetName(node.Toggle, (string)node.Toggle.ToolTip);
            if (node.Pages is { } pager)
                pager.Panel.Visibility = state.Expanded ? Visibility.Visible : Visibility.Collapsed;
            if (!state.Expanded) continue;

            var start = state.Page * PageSize;
            var count = Math.Min(PageSize, children.Count - start);
            if (node.Pages is { } pages)
            {
                pages.Range.Text = $"{StorageText.Count(start + 1)}–{StorageText.Count(start + count)} / {StorageText.Count(children.Count)}";
                pages.Previous.IsEnabled = start > 0;
                pages.Next.IsEnabled = start + count < children.Count;
            }

            for (var index = start; index < start + count; index++)
                node.Children.Add(AddNode(children[index], node.Level + 1));
            for (var index = node.Children.Count - 1; index >= 0; index--)
                pending.Push(node.Children[index]);
        }
    }

    private void UpdateBranch(Node node, BranchState state)
    {
        var previousFocused = node.Pages?.Previous.IsKeyboardFocused == true;
        var nextFocused = node.Pages?.Next.IsKeyboardFocused == true;
        var pending = new Stack<Node>(node.Children);
        while (pending.TryPop(out var child))
        {
            foreach (var grandchild in child.Children) pending.Push(grandchild);
            canvas.Children.Remove(child.Card);
            nodes.Remove(child.Item);
        }
        node.Children.Clear();
        branches[node.Item] = state;
        BuildBranches(node);
        LayoutGraph();

        if (node.Pages is { } pages)
        {
            if (previousFocused && !pages.Previous.IsEnabled) pages.Next.Focus();
            if (nextFocused && !pages.Next.IsEnabled) pages.Previous.Focus();
        }
    }

    private void ChangePage(Node node, int direction)
    {
        var state = branches.GetValueOrDefault(node.Item);
        var page = state.Page + direction;
        if (state.Expanded && page >= 0 && page <= (node.Item.Children!.Count - 1) / PageSize)
            UpdateBranch(node, state with { Page = page });
    }

    private Node AddNode(StorageItem item, int level)
    {
        var isRoot = level == 0;
        var title = item.DisplayName;
        var summary = item.SizeText;
        if (item.Children is { } entries && (entries.Count > 0 || !item.ReadError))
            summary += entries.Count == 0 ? " · Vide" : $" · {StorageText.Quantity(entries.Count, "élément", "éléments")}";
        if (item.ReadError) summary += " · partiel";

        var icon = new ShapePath
        {
            Data = (Geometry)FindResource(item.IsDirectory ? "FolderIcon" : "FileIcon"),
            Style = (Style)FindResource("Icon"),
            Margin = new Thickness(0, 0, 7, 0)
        };
        icon.SetResourceReference(ShapePath.StrokeProperty, isRoot ? "AccentInk" : item.IsDirectory ? "Accent" : "Green");
        DockPanel.SetDock(icon, Dock.Left);
        var name = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        var details = new TextBlock
        {
            Text = summary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 10,
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(0, 2, 0, 0)
        };
        details.SetResourceReference(TextBlock.ForegroundProperty, isRoot ? "AccentInk" : item.ReadError ? "Warning" : "Muted");

        var button = new Button
        {
            Height = NodeHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 3, 8, 3),
            BorderThickness = new Thickness(1.5),
            Content = new DockPanel
            {
                Children = { icon, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, details } } }
            },
            ToolTip = new TextBlock
            {
                Text = $"{title} · {item.Kind} · {item.SizeText}\n{item.FullPath}",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            }
        };
        if (isRoot) button.SetResourceReference(Button.ForegroundProperty, "AccentInk");
        AutomationProperties.SetName(button, $"{title}, {summary}");
        button.Click += (_, _) => ItemSelected?.Invoke(item);
        if (!isRoot)
        {
            button.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                e.Handled = true;
                ItemActivated?.Invoke(item);
            };
            button.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                ItemActivated?.Invoke(item);
            };
        }

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(button);
        var card = new StackPanel { Children = { header } };
        var node = new Node(item, level, card, button);
        if (item.Children is { Count: > 0 } children)
        {
            var toggle = new Button { Width = 34, Height = NodeHeight, FontSize = 18, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
            toggle.SetResourceReference(Button.ForegroundProperty, "Accent");
            toggle.Click += (_, _) =>
            {
                var state = branches.GetValueOrDefault(item);
                UpdateBranch(node, state with { Expanded = !state.Expanded });
            };
            Grid.SetColumn(toggle, 1);
            header.Children.Add(toggle);
            node.Toggle = toggle;
            if (children.Count > PageSize) node.Pages = AddPager(node);
        }
        nodes.Add(item, node);
        StyleSelection(node);
        canvas.Children.Add(card);
        return node;
    }

    private Pager AddPager(Node node)
    {
        var previous = PageButton(-1, "BackIcon", $"Page précédente de {node.Item.DisplayName}");
        var next = PageButton(1, "RightIcon", $"Page suivante de {node.Item.DisplayName}");
        var range = new TextBlock
        {
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        range.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(previous, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        var panel = new DockPanel
        {
            Height = PagerHeight - 4,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
            Children = { previous, next, range }
        };
        node.Card.Children.Add(panel);
        return new Pager(panel, previous, next, range);

        Button PageButton(int direction, string iconName, string label)
        {
            var button = new Button
            {
                Width = 34,
                MinHeight = 0,
                Padding = new Thickness(0),
                ToolTip = label,
                Content = new ShapePath { Style = (Style)FindResource("Icon"), Data = (Geometry)FindResource(iconName) }
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => ChangePage(node, direction);
            return button;
        }
    }

    private void StyleSelection(Node node)
    {
        var active = node.Item == selected;
        var isRoot = node.Level == 0;
        var background = isRoot ? "Accent" : active ? "Active" : node.Item.IsDirectory ? "Raised" : "Panel";
        var border = isRoot ? (active ? "AccentInk" : "Accent") : (active ? "Accent" : "Line");
        node.Button.SetResourceReference(Button.BackgroundProperty, background);
        node.Button.SetResourceReference(Button.BorderBrushProperty, border);
    }

    private void LayoutGraph()
    {
        if (root is null) return;
        var order = new List<Node>(nodes.Count);
        var top = Padding;
        var rowHeight = 0d;
        var right = Padding;
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            order.Add(node);
            var left = node.Level == 0 ? Padding :
                Padding + RootWidth + ColumnGap + (node.Level - 1) * (NodeWidth + ColumnGap);
            node.Position = new Point(left, top);
            node.Card.Width = node.Width;
            Canvas.SetLeft(node.Card, left);
            Canvas.SetTop(node.Card, top);
            rowHeight = Math.Max(rowHeight, node.Height);
            right = Math.Max(right, left + node.Width);

            // The first child shares its parent's row. A leaf finishes that row.
            if (node.Children.Count == 0)
            {
                top += rowHeight + RowGap;
                rowHeight = 0;
            }
            for (var index = node.Children.Count - 1; index >= 0; index--)
                pending.Push(node.Children[index]);
        }

        canvas.Width = right + Padding;
        canvas.Height = top - RowGap + Padding + (empty.Visibility == Visibility.Visible ? 36 : 0);
        empty.Width = RootWidth;
        Canvas.SetLeft(empty, Padding);
        Canvas.SetTop(empty, Padding + root.Height + 13);

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            foreach (var node in order)
            {
                if (node.Children.Count == 0) continue;

                var start = node.Position + new Vector(node.Width, NodeHeight / 2);
                var spine = start.X + ColumnGap / 2;
                path.BeginFigure(start, isFilled: false, isClosed: false);
                path.LineTo(new Point(spine, start.Y), isStroked: true, isSmoothJoin: false);
                if (node.Children.Count > 1)
                    path.LineTo(new Point(spine, node.Children[^1].Position.Y + NodeHeight / 2), isStroked: true, isSmoothJoin: false);

                foreach (var child in node.Children)
                {
                    var end = child.Position + new Vector(0, NodeHeight / 2);
                    path.BeginFigure(new Point(spine, end.Y), isFilled: false, isClosed: false);
                    path.LineTo(end, isStroked: true, isSmoothJoin: false);
                }
            }
        }
        geometry.Freeze();
        connectors.Data = geometry;
    }
}
