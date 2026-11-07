using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ShapePath = System.Windows.Shapes.Path;

namespace Octet;

public partial class MainWindow : Window
{
    private const int ResultLimit = 250;
    private const int UseImmersiveDarkMode = 20;
    private readonly DispatcherTimer statusTimer;
    private readonly List<TileVisual> tiles = [];
    private readonly Dictionary<string, ScanResult> scanCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<FolderVisit> backHistory = new();
    private readonly VectorTree vectorTree = new();
    private List<MapEntry> mapEntries = [];
    private ScanResult? scan;
    private StorageItem? scope;
    private StorageItem? selected;
    private StorageItem? vectorScope;
    private CancellationTokenSource? scanWork;
    private CancellationTokenSource? resultWork;
    private string? startDrive;
    private string? visiblePath;
    private bool settingFolder;
    private bool revealing;
    private bool closed;

    private sealed record Drive(string Path, string Label, long Capacity, long Free);
    private sealed record TileVisual(MapEntry Entry, Button Button, ShapePath Icon, TextBlock Name, TextBlock Size, TextBlock Share);
    private sealed record FolderVisit(StorageItem Folder, StorageItem? Selection, string Query);

    public MainWindow()
    {
        InitializeComponent();
        VectorTreeHost.Content = vectorTree;
        vectorTree.ItemSelected += SelectItem;
        vectorTree.ItemActivated += Activate;
        Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 28));
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 28));
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        statusTimer.Tick += (_, _) => RestoreStatus();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length == 2) _ = OpenSourceAsync(args[1]);
        await RefreshDrivesAsync();
    }

    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        var enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        closed = true;
        scanWork?.Cancel();
        CancelResults();
        statusTimer.Stop();
    }

    private async Task RefreshDrivesAsync()
    {
        var drives = await Task.Run(ReadDrives);
        if (closed) return;

        DriveList.Children.Clear();
        foreach (var drive in drives)
        {
            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var icon = MakeIcon("DriveIcon", "Accent", new Thickness(0, 0, 8, 0));
            DockPanel.SetDock(icon, Dock.Left);
            heading.Children.Add(icon);
            var letter = new TextBlock { Text = drive.Path.TrimEnd('\\'), Margin = new Thickness(0, 0, 7, 0) };
            DockPanel.SetDock(letter, Dock.Left);
            heading.Children.Add(letter);
            heading.Children.Add(new TextBlock
            {
                Text = drive.Label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush("Muted"),
                FontSize = 11,
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            });

            var info = new StackPanel();
            info.Children.Add(heading);
            info.Children.Add(new ProgressBar
            {
                Maximum = drive.Capacity,
                Value = Math.Clamp(drive.Capacity - drive.Free, 0, drive.Capacity),
                IsHitTestVisible = false
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{StorageText.Bytes(drive.Free)} libres sur {StorageText.Bytes(drive.Capacity)}",
                FontWeight = FontWeights.Normal,
                Foreground = Brush("Muted"),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var button = new Button
            {
                Style = (Style)FindResource("DriveButton"),
                Content = info,
                CommandParameter = drive.Path,
                ToolTip = $"{drive.Path} · {drive.Label}\n{StorageText.Bytes(drive.Free)} libres sur {StorageText.Bytes(drive.Capacity)}"
            };
            AutomationProperties.SetName(button, $"Ouvrir le lecteur {drive.Path} {drive.Label}");
            button.Click += async (_, _) => await OpenSourceAsync(drive.Path);
            DriveList.Children.Add(button);
        }

        if (drives.Count == 0)
            DriveList.Children.Add(new TextBlock
            {
                Text = "Aucun lecteur disponible.",
                Foreground = Brush("Dim"),
                TextWrapping = TextWrapping.Wrap
            });

        var system = Path.GetPathRoot(Environment.SystemDirectory);
        startDrive = (drives.FirstOrDefault(drive => string.Equals(drive.Path, system, StringComparison.OrdinalIgnoreCase))
            ?? drives.FirstOrDefault())?.Path;
        StartDrive.IsEnabled = startDrive is not null;
        if (startDrive is not null) StartDriveText.Text = $"Analyser {startDrive}";
        UpdateDriveHighlight();
    }

    private static List<Drive> ReadDrives()
    {
        var drives = new List<Drive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var kind = drive.DriveType;
                if (kind is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram) || !drive.IsReady) continue;
                var label = drive.VolumeLabel;
                if (string.IsNullOrWhiteSpace(label)) label = kind == DriveType.Removable ? "Amovible" : "Disque local";
                drives.Add(new Drive(drive.Name, label, drive.TotalSize, drive.TotalFreeSpace));
            }
            catch (Exception error) when (StorageScanner.IsFileSystemError(error))
            {
                // The drive may have been unplugged between queries.
            }
        }
        return drives;
    }

    private void UpdateDriveHighlight()
    {
        var root = visiblePath is null ? null : Path.GetPathRoot(visiblePath);
        foreach (var button in DriveList.Children.OfType<Button>())
            button.Tag = string.Equals(root, (string)button.CommandParameter, StringComparison.OrdinalIgnoreCase) ? "Active" : null;
    }

    private async void ChooseFolderClicked(object sender, RoutedEventArgs e) => await ChooseFolderAsync();

    private async Task ChooseFolderAsync()
    {
        if (scanWork is not null) return;
        var dialog = new OpenFolderDialog { Title = "Choisir un dossier à analyser" };
        if (scope is not null) dialog.InitialDirectory = scope.FullPath;
        if (dialog.ShowDialog(this) == true) await OpenSourceAsync(dialog.FolderName);
    }

    private async void StartDriveClicked(object sender, RoutedEventArgs e)
    {
        if (startDrive is not null) await OpenSourceAsync(startDrive);
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        if (scan is not null) await OpenSourceAsync(scan.Root.FullPath, refresh: true);
    }

    private async Task OpenSourceAsync(string path, bool refresh = false)
    {
        if (scanWork is not null || closed) return;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (StorageScanner.IsFileSystemError(error) || error is ArgumentException or NotSupportedException)
        {
            ShowStatus($"Impossible d’analyser cette source. {error.Message}", "Warning");
            return;
        }

        if (!refresh && scanCache.TryGetValue(path, out var cached))
        {
            if (scan != cached) ShowScan(cached);
            return;
        }

        using var work = new CancellationTokenSource();
        scanWork = work;
        CancelResults();
        SetScanMode(true);
        ScanningTitle.Text = "Lecture des fichiers";
        ScanningPath.Text = path;
        ScanningPath.ToolTip = path;
        ScanningCounts.Text = "Préparation de l’analyse…";
        SetVisiblePath(path);
        UpdateDriveHighlight();
        RestoreStatus();

        var progress = new Progress<ScanProgress>(update =>
        {
            if (closed || scanWork != work || work.IsCancellationRequested) return;
            ScanningPath.Text = update.Path;
            ScanningPath.ToolTip = update.Path;
            ScanningCounts.Text = $"{StorageText.Bytes(update.Bytes)}   ·   {StorageText.FileCount(update.Files)}   ·   {StorageText.Quantity(update.Folders, "dossier", "dossiers")}";
        });

        ScanResult? result = null;
        string? message = null;
        var warning = false;
        try
        {
            result = await Task.Run(() => StorageScanner.Scan(path, progress, work.Token), work.Token);
            if (work.IsCancellationRequested) result = null;
            if (result is null) message = "Analyse annulée.";
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            message = "Analyse annulée.";
        }
        catch (Exception error) when (StorageScanner.IsFileSystemError(error) || error is ArgumentException or NotSupportedException)
        {
            message = $"Impossible d’analyser cette source. {error.Message}";
            warning = true;
        }
        finally
        {
            scanWork = null;
        }

        if (closed) return;
        if (result is not null)
        {
            scanCache[path] = result;
            ShowScan(result, preserveFolder: refresh);
            await RefreshDrivesAsync();
        }
        else
        {
            SetScanMode(false);
            SetVisiblePath(selected?.FullPath);
            UpdateDriveHighlight();
            UpdateView();
            ShowStatus(message!, warning ? "Warning" : "Dim");
        }
    }

    private void ShowScan(ScanResult result, bool preserveFolder = false)
    {
        var folder = preserveFolder && ResolveItem(result.Root, scope) is { IsDirectory: true } restored ? restored : result.Root;
        var selection = preserveFolder ? ResolveItem(result.Root, selected) : null;
        var query = preserveFolder ? SearchBox.Text : "";
        RestoreBackHistory(preserveFolder ? result.Root : null);
        vectorTree.SetFolder(null);
        vectorScope = null;
        scan = result;
        SetScanMode(false);
        ShowFolder(folder, selection, query);
        RestoreStatus();
    }

    private static StorageItem? ResolveItem(StorageItem root, StorageItem? oldItem)
    {
        if (oldItem is null) return null;
        var parts = new Stack<string>();
        for (var node = oldItem; node.Parent is not null; node = node.Parent) parts.Push(node.Name);

        StorageItem? current = root;
        while (parts.TryPop(out var name))
        {
            current = current.Children?.Find(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            if (current is null) return null;
        }
        return current;
    }

    private void RestoreBackHistory(StorageItem? newRoot)
    {
        if (newRoot is null)
        {
            backHistory.Clear();
            return;
        }

        var visits = backHistory.Reverse().ToArray();
        backHistory.Clear();
        foreach (var visit in visits)
        {
            if (ResolveItem(newRoot, visit.Folder) is { IsDirectory: true } folder)
                backHistory.Push(new FolderVisit(folder, ResolveItem(newRoot, visit.Selection), visit.Query));
        }
    }

    private void SetScanMode(bool scanning)
    {
        ScanView.Visibility = Show(scanning);
        HomeView.Visibility = Show(!scanning && scan is null);
        Dashboard.Visibility = Show(!scanning && scan is not null);
        CancelScan.IsEnabled = scanning;
        ChooseFolder.IsEnabled = !scanning;
        DriveList.IsEnabled = !scanning;
    }

    private void CancelScanClicked(object sender, RoutedEventArgs e)
    {
        scanWork?.Cancel();
        CancelScan.IsEnabled = false;
        ScanningTitle.Text = "Arrêt de l’analyse…";
    }

    private void ShowFolder(StorageItem folder, StorageItem? selection = null, string query = "")
    {
        settingFolder = true;
        scope = folder;
        FolderTitle.Text = folder.DisplayName;
        FolderTitle.ToolTip = folder.DisplayName;
        FolderPath.Text = folder.FullPath;
        FolderPath.ToolTip = folder.FullPath;
        FolderPath.Visibility = Show(folder.DisplayName != folder.FullPath);
        while (backHistory.TryPeek(out var visit) && visit.Folder == folder) backHistory.Pop();
        BackFolder.IsEnabled = backHistory.Count > 0;
        BackFolder.ToolTip = backHistory.TryPeek(out var previous)
            ? $"Revenir à {previous.Folder.DisplayName} (Alt + Gauche)"
            : "Aucun dossier précédent";
        UpFolder.IsEnabled = folder.Parent is not null;
        TotalSize.Text = folder.SizeText;
        TotalFiles.Text = StorageText.Count(folder.Files);
        TotalFolders.Text = StorageText.Count(folder.Folders);
        SearchBox.Text = query;
        FolderTree.ItemsSource = folder.Children;
        TreeEmpty.Text = folder.Children!.Count == 0
            ? folder.ReadError ? "Ce dossier est inaccessible." : "Ce dossier ne contient aucun élément analysé."
            : "";
        BuildMap(folder);
        SelectItem(selection ?? folder);
        settingFolder = false;
        UpdateView();
        UpdateDriveHighlight();
    }

    private void SelectItem(StorageItem item)
    {
        selected = item;
        SetVisiblePath(item.FullPath);
        MapSelectionName.Text = item.DisplayName;
        MapSelectionName.ToolTip = item.FullPath;
        MapSelectionInfo.Text = item.IsDirectory
            ? $"{item.SizeText} · {StorageText.FileCount(item.Files)}"
            : $"{item.SizeText} · Fichier";
        if (item.ReadError) MapSelectionInfo.Text += " · Accès partiel";
        OpenMapSelection.Visibility = Show(item.IsDirectory && item != scope);
        foreach (var tile in tiles) tile.Button.Tag = tile.Entry.Item == item ? "Selected" : null;
        if (!settingFolder && VectorView.IsVisible) vectorTree.SetSelected(item);

        var entry = mapEntries.Find(entry => entry.Item == item);
        if (!ReferenceEquals(entry, MapList.SelectedItem)) MapList.SelectedItem = entry;
        if (item != ResultsList.SelectedItem)
            ResultsList.SelectedItem = ResultsList.Items.Contains(item) ? item : null;
    }

    private void SetVisiblePath(string? path)
    {
        visiblePath = path;
        SelectionPath.Text = path ?? "Aucun élément sélectionné";
        SelectionPath.ToolTip = path;
        SelectionPath.IsEnabled = path is not null;
        CopyButton.IsEnabled = path is not null;
        RevealButton.IsEnabled = path is not null && !revealing;
    }

    private void UpFolderClicked(object sender, RoutedEventArgs e) => Up();

    private void BackFolderClicked(object sender, RoutedEventArgs e) => Back();

    private void Back()
    {
        if (scanWork is null && backHistory.TryPop(out var previous))
            ShowFolder(previous.Folder, previous.Selection, previous.Query);
    }

    private void Up()
    {
        if (scope?.Parent is { } parent) NavigateTo(parent, scope);
    }

    private void NavigateTo(StorageItem folder, StorageItem? selection = null)
    {
        if (scanWork is not null || scope is null || folder == scope) return;
        backHistory.Push(new FolderVisit(scope, selected, SearchBox.Text));
        ShowFolder(folder, selection);
    }

    private void BuildMap(StorageItem folder)
    {
        mapEntries = Treemap.Entries(folder);
        tiles.Clear();
        MapCanvas.Children.Clear();
        MapList.ItemsSource = mapEntries;
        MapEmpty.Text = folder.ReadError
            ? "Ce dossier est inaccessible."
            : "Aucune taille à représenter. Consultez la liste pour voir les éléments de 0 o.";
        MapEmpty.Visibility = Show(mapEntries.Count == 0);

        foreach (var entry in mapEntries)
        {
            var icon = MakeIcon(entry.Item switch
            {
                null => "ListIcon",
                { IsDirectory: true } => "FolderIcon",
                _ => "FileIcon"
            }, null, new Thickness(0, 0, 0, 6));
            icon.Stroke = entry.Accent;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            var name = new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var size = new TextBlock { Text = entry.SizeText, Foreground = entry.Accent, Margin = new Thickness(0, 2, 0, 0) };
            var share = new TextBlock
            {
                Text = entry.ShareText,
                FontWeight = FontWeights.SemiBold,
                Foreground = entry.Accent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { icon, name, size } };
            var content = new Grid
            {
                Margin = new Thickness(10, 7, 10, 7),
                ClipToBounds = true,
                Children = { labels, share }
            };
            var button = new Button
            {
                Style = (Style)FindResource("TileButton"),
                Background = entry.Fill,
                Content = content,
                ToolTip = entry.Tooltip,
                DataContext = entry
            };
            AutomationProperties.SetName(button, entry.Description);
            button.Click += (_, _) =>
            {
                if (entry.Item is null) TreeTab.IsChecked = true;
                else
                {
                    SelectItem(entry.Item);
                    MapList.ScrollIntoView(entry);
                }
            };
            button.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2 && entry.Item is not null)
                {
                    e.Handled = true;
                    Activate(entry.Item);
                }
            };
            button.PreviewKeyDown += ItemListKeyDown;
            MapCanvas.Children.Add(button);
            tiles.Add(new TileVisual(entry, button, icon, name, size, share));
        }
        LayoutMap();
    }

    private void MapSizeChanged(object sender, SizeChangedEventArgs e) => LayoutMap();

    private void LayoutMap()
    {
        if (tiles.Count == 0 || MapCanvas.ActualWidth <= 0 || MapCanvas.ActualHeight <= 0) return;
        var positions = Treemap.Layout(mapEntries, MapCanvas.ActualWidth, MapCanvas.ActualHeight);
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            var box = positions[index];
            const double gap = 4;
            var width = Math.Max(0, box.Width - gap);
            var height = Math.Max(0, box.Height - gap);
            tile.Button.Visibility = Show(width >= 5 && height >= 5);
            tile.Button.Width = width;
            tile.Button.Height = height;
            Canvas.SetLeft(tile.Button, box.X + gap / 2);
            Canvas.SetTop(tile.Button, box.Y + gap / 2);
            var large = width >= 220 && height >= 120;
            tile.Name.Visibility = Show(width >= 52 && height >= 36);
            tile.Size.Visibility = Show(width >= 72 && height >= 55);
            tile.Icon.Visibility = Show(width >= 95 && height >= 76);
            tile.Share.Visibility = Show(width >= 160 && height >= 138);
            tile.Name.FontSize = large ? 19 : 13;
            tile.Size.FontSize = large ? 15 : 12;
            tile.Share.FontSize = width >= 340 && height >= 210 ? 42 : 26;
        }
    }

    private void MapSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settingFolder || MapList.SelectedItem is not MapEntry entry) return;
        if (entry.Item is { } item)
        {
            if (item != selected) SelectItem(item);
        }
        else
        {
            MapList.SelectedItem = mapEntries.Find(candidate => candidate.Item == selected);
            TreeTab.IsChecked = true;
        }
    }

    private void OpenMapSelectionClicked(object sender, RoutedEventArgs e)
    {
        if (selected is { IsDirectory: true } item) NavigateTo(item);
    }

    private void MapListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListRowAt(MapList, e) is { Content: MapEntry entry }) ActivateMapEntry(entry);
    }

    private void TreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!settingFolder && e.NewValue is StorageItem item && item != selected) SelectItem(item);
    }

    private void TreeHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: StorageItem item }) return;
        e.Handled = true;
        Activate(item);
    }

    private void ResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingFolder && ResultsList.SelectedItem is StorageItem item && item != selected) SelectItem(item);
    }

    private void ResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListRowAt(ResultsList, e) is { Content: StorageItem item }) Activate(item);
    }

    private static ListBoxItem? ListRowAt(ListBox list, MouseButtonEventArgs e) =>
        e.OriginalSource is DependencyObject source ? ItemsControl.ContainerFromElement(list, source) as ListBoxItem : null;

    private void ItemListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        switch (sender)
        {
            case Button { DataContext: MapEntry entry }:
                ActivateMapEntry(entry);
                break;
            case ListBox { SelectedItem: MapEntry entry }:
                ActivateMapEntry(entry);
                break;
            case ListBox { SelectedItem: StorageItem item }:
                Activate(item);
                break;
            case TreeView { SelectedItem: StorageItem item }:
                Activate(item);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ActivateMapEntry(MapEntry entry)
    {
        if (entry.Item is { } item) Activate(item);
        else TreeTab.IsChecked = true;
    }

    private void Activate(StorageItem item)
    {
        if (item.IsDirectory) NavigateTo(item);
        else
        {
            SelectItem(item);
            _ = RevealAsync(item.FullPath);
        }
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        var blank = SearchBox.Text.Length == 0;
        SearchHint.Visibility = Show(blank);
        ClearSearch.Visibility = Show(!blank);
        UpdateView();
    }

    private void ClearSearchClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void ViewChanged(object sender, RoutedEventArgs e)
    {
        if (scope is null || settingFolder) return;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        else UpdateView();
    }

    private void ResetTreeViewClicked(object sender, RoutedEventArgs e) => vectorTree.ResetView();

    private void ZoomOutTreeClicked(object sender, RoutedEventArgs e) => vectorTree.ZoomBy(-1);

    private void ZoomInTreeClicked(object sender, RoutedEventArgs e) => vectorTree.ZoomBy(1);

    private void BrowseListClicked(object sender, RoutedEventArgs e)
    {
        TreeTab.IsChecked = true;
        TreeTab.Focus();
    }

    private void UpdateView()
    {
        if (scope is null || settingFolder || scanWork is not null) return;
        var query = SearchBox.Text.Trim();
        var results = query.Length > 0 || FilesTab.IsChecked == true;
        var tree = !results && TreeTab.IsChecked == true;
        var vector = !results && VectorTab.IsChecked == true;
        MapView.Visibility = Show(!results && !tree && !vector);
        TreeViewPanel.Visibility = Show(tree);
        VectorView.Visibility = Show(vector);
        ResultsView.Visibility = Show(results);
        CancelResults();

        if (results) _ = FindResultsAsync(scope, query);
        else if (tree) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SynchronizeTreeSelection);
        else if (vector)
        {
            if (vectorScope != scope)
            {
                vectorTree.SetFolder(scope);
                vectorScope = scope;
            }
            vectorTree.SetSelected(selected ?? scope);
        }
    }

    private void SynchronizeTreeSelection()
    {
        if (closed || !FolderTree.IsVisible || FolderTree.SelectedItem == selected) return;
        if (FolderTree.SelectedItem is StorageItem previous && FindTreeRow(previous) is { } previousRow)
            previousRow.IsSelected = false;
        if (selected is not null && FindTreeRow(selected) is { } nextRow) nextRow.IsSelected = true;
    }

    private TreeViewItem? FindTreeRow(StorageItem item)
    {
        var trail = new Stack<StorageItem>();
        for (StorageItem? node = item; node is not null && node != scope; node = node.Parent) trail.Push(node);
        ItemsControl? row = FolderTree;
        while (row is not null && trail.TryPop(out var node))
            row = row.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
        return row as TreeViewItem;
    }

    private void CancelResults()
    {
        resultWork?.Cancel();
        resultWork = null;
    }

    private async Task FindResultsAsync(StorageItem folder, string query)
    {
        using var work = new CancellationTokenSource();
        resultWork = work;
        var searching = query.Length > 0;
        ResultsTitle.Text = searching ? "Résultats de recherche" : "Fichiers les plus volumineux";
        ResultsInfo.Text = "Dans ce dossier et ses sous-dossiers";
        ResultsList.ItemsSource = null;
        ResultsEmpty.Text = searching ? "Recherche en cours…" : "Calcul du classement…";

        try
        {
            if (searching) await Task.Delay(160, work.Token);
            var results = await Task.Run(() => StorageScanner.Rank(folder, query, ResultLimit, work.Token), work.Token);
            if (resultWork != work || closed) return;
            ResultsList.ItemsSource = results.Items;
            if (selected is not null && results.Items.Contains(selected)) ResultsList.SelectedItem = selected;
            var count = searching ? StorageText.Quantity(results.Total, "résultat", "résultats") : StorageText.FileCount(results.Total);
            ResultsInfo.Text = results.Total > ResultLimit
                ? $"{StorageText.Count(ResultLimit)} plus volumineux sur {StorageText.Count(results.Total)} · Dossier et sous-dossiers"
                : $"{count} · Dossier et sous-dossiers";
            ResultsEmpty.Text = results.Total > 0 ? "" : searching ? "Aucun élément ne correspond à ce nom." : "Aucun fichier dans ce dossier.";
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
        }
        finally
        {
            if (resultWork == work) resultWork = null;
        }
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.F && Dashboard.IsVisible)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
            else if (e.Key == Key.L && visiblePath is not null)
            {
                SelectionPath.Focus();
                SelectionPath.SelectAll();
            }
            else if (e.Key == Key.O && scanWork is null) _ = ChooseFolderAsync();
            else return;
            e.Handled = true;
        }
        else if (e.Key == Key.BrowserBack && Dashboard.IsVisible)
        {
            Back();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && Dashboard.IsVisible)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Left) Back();
            else if (key == Key.Up) Up();
            else return;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void WindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1 || !Dashboard.IsVisible) return;
        Back();
        e.Handled = true;
    }

    private void CopyClicked(object sender, RoutedEventArgs e)
    {
        if (visiblePath is null) return;
        try
        {
            Clipboard.SetText(visiblePath);
            ShowStatus("Chemin copié dans le presse-papiers.", "Green", temporary: true);
        }
        catch (ExternalException)
        {
            ShowStatus("Le presse-papiers est occupé. Réessayez.", "Warning", temporary: true);
        }
    }

    private async void RevealClicked(object sender, RoutedEventArgs e)
    {
        if (visiblePath is { } path) await RevealAsync(path);
    }

    private async Task RevealAsync(string path)
    {
        if (revealing) return;
        revealing = true;
        RevealButton.IsEnabled = false;
        try
        {
            await Explorer.RevealAsync(path);
        }
        catch (Exception error) when (StorageScanner.IsFileSystemError(error) || error is ExternalException or ArgumentException or InvalidOperationException)
        {
            if (!closed) ShowStatus($"Impossible d’afficher cet élément. {error.Message}", "Warning", temporary: true);
        }
        finally
        {
            revealing = false;
            if (!closed) RevealButton.IsEnabled = visiblePath is not null;
        }
    }

    private void RestoreStatus()
    {
        if (scanWork is not null)
        {
            ShowStatus("Analyse en cours…", "Accent");
            return;
        }

        if (scan is null)
        {
            ShowStatus("Choisissez un lecteur ou un dossier pour démarrer.", "Dim");
            return;
        }

        var duration = scan.Elapsed.TotalSeconds < 0.1 ? "moins de 0,1 s" : $"{scan.Elapsed.TotalSeconds.ToString("0.#", StorageText.French)} s";
        var summary = $"Analyse terminée en {duration} · {StorageText.FileCount(scan.Root.Files)}";
        if (scan.Unreadable > 0) summary += $" · {StorageText.Count(scan.Unreadable)} dossiers partiellement lus ou inaccessibles";
        if (scan.ReparsePoints > 0) summary += $" · {StorageText.Count(scan.ReparsePoints)} éléments redirigés ignorés";
        ShowStatus(summary, scan.Unreadable == 0 ? "Green" : "Warning");
    }

    private void ShowStatus(string text, string color, bool temporary = false)
    {
        statusTimer.Stop();
        StatusDot.Fill = Brush(color);
        StatusText.Text = text;
        StatusText.ToolTip = text;
        if (temporary) statusTimer.Start();
    }

    private ShapePath MakeIcon(string geometry, string? color, Thickness margin)
    {
        var icon = new ShapePath { Style = (Style)FindResource("Icon"), Data = (Geometry)FindResource(geometry), Margin = margin };
        if (color is not null) icon.Stroke = Brush(color);
        return icon;
    }

    private Brush Brush(string resource) => (Brush)FindResource(resource);
    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
