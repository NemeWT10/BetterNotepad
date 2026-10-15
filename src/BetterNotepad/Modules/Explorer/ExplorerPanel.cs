using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Explorer;

/// <summary>Glyphs and tints of the tree (Segoe Fluent Icons; tints are the theme's own syntax colors, so every theme fits).</summary>
internal static class ExplorerLook
{
    public const string FolderGlyph = "";
    public const string FolderOpenGlyph = "";
    public const string NewFileGlyph = "";
    public const string NewFolderGlyph = "";
    public const string CollapseAllGlyph = "";
    public const string OpenFolderGlyph = "";
    public const string CloseFolderGlyph = "";
    public const string PanelGlyph = "";
    public const string OpenFileGlyph = "";
    public const string LinkGlyph = "";

    public static string GlyphFor(FileIconKind kind) => kind switch
    {
        FileIconKind.Folder => FolderGlyph,
        FileIconKind.Text => "",
        FileIconKind.Markdown => "",
        FileIconKind.Code or FileIconKind.Markup => "",
        FileIconKind.Style => "",
        FileIconKind.Data => "",
        FileIconKind.Config => "",
        FileIconKind.Table => "",
        FileIconKind.Script => "",
        FileIconKind.Image => "",
        FileIconKind.Pdf => "",
        FileIconKind.Document => "",
        FileIconKind.Archive => "",
        FileIconKind.Binary => "",
        FileIconKind.Audio => "",
        FileIconKind.Video => "",
        FileIconKind.Font => "",
        _ => "",
    };

    public static string TintFor(FileIconKind kind) => kind switch
    {
        FileIconKind.Folder => "Brush.AccentText",
        FileIconKind.Code => "Brush.Syntax.Function",
        FileIconKind.Markup => "Brush.Syntax.Tag",
        FileIconKind.Style => "Brush.Syntax.Keyword",
        FileIconKind.Data => "Brush.Syntax.Constant",
        FileIconKind.Config => "Brush.TextDim",
        FileIconKind.Table => "Brush.Syntax.String",
        FileIconKind.Script => "Brush.Syntax.String",
        FileIconKind.Markdown => "Brush.Syntax.Type",
        FileIconKind.Text => "Brush.TextDim",
        FileIconKind.Image => "Brush.Syntax.Number",
        FileIconKind.Pdf => "Brush.Danger",
        FileIconKind.Document => "Brush.Info",
        FileIconKind.Archive => "Brush.Syntax.Attribute",
        FileIconKind.Audio or FileIconKind.Video => "Brush.Syntax.Preprocessor",
        FileIconKind.Font => "Brush.Syntax.Property",
        _ => "Brush.TextFaint",
    };
}

/// <summary>
/// The Explorer side panel of a window: the open folder as a virtualized tree (lazy, off-thread listing, watched without
/// polling), its header with actions, the empty state, keyboard and mouse handling. File operations: ExplorerPanel.Actions.cs.
/// </summary>
public sealed partial class ExplorerPanel : Grid
{
    public const double DefaultWidth = 260;
    public const double MinPanelWidth = 170;

    private readonly MainWindow _window;
    private readonly Grid _header = new() { Margin = new Thickness(14, 6, 6, 0) };
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
    private readonly Button _newFileButton;
    private readonly Button _newFolderButton;
    private readonly Button _refreshButton;
    private readonly Button _collapseButton;
    private readonly Button _moreButton;
    private readonly Button _hideButton;
    private readonly ItemsControl _list = new();
    private readonly ScrollViewer _emptyHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly TextBlock _emptyTitle = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _emptyText = new() { FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _emptyOpen = new() { Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(10, 7, 10, 7), FontSize = 12.5 };
    private readonly Button _emptyActive = new() { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6), FontSize = 12.5 };
    private readonly TextBlock _emptyOpenText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _emptyActiveText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _emptyKeys = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _emptyKeysText = new() { FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _bubble = new() { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(9, 6, 9, 7), Visibility = Visibility.Collapsed };
    private readonly TextBlock _bubbleText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 16 };
    private ScrollViewer? _scroll;
    private ExplorerTree? _tree;
    private ExplorerNode? _selected;
    private Dictionary<string, bool> _openFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _openFilesQueued;
    private bool _starting;
    private bool _quietRootLoss;
    private int _revealVersion;
    private string _typed = "";
    private DateTime _typedAt;
    private (ExplorerNode Node, bool Center)? _pendingScroll;
    private bool _toolsPinned;
    private bool _disposed;

    public ExplorerPanel(MainWindow window)
    {
        _window = window;
        SetResourceReference(BackgroundProperty, "Brush.Chrome");
        ClipToBounds = true;
        UseLayoutRounding = true;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(TabGroupView.StripHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ── header: the folder's name and its actions, on the tab strip's axis; the line continues the strip's underline ──
        var headerLine = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        headerLine.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        Children.Add(headerLine);
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.Height = 32;
        _header.VerticalAlignment = VerticalAlignment.Top;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _header.Children.Add(_title);
        _newFileButton = Tool(ExplorerLook.NewFileGlyph, () => _ = BeginNewItemAsync(folder: false));
        _newFolderButton = Tool(ExplorerLook.NewFolderGlyph, () => _ = BeginNewItemAsync(folder: true));
        _refreshButton = Tool(Glyphs.Refresh, () => _ = RefreshAsync());
        _collapseButton = Tool(ExplorerLook.CollapseAllGlyph, CollapseAll);
        _moreButton = Tool(Glyphs.More, ShowMoreMenu);
        _hideButton = Tool(ExplorerLook.PanelGlyph, () => _window.HideExplorer());
        foreach (var b in new[] { _newFileButton, _newFolderButton, _refreshButton, _collapseButton, _moreButton, _hideButton }) _tools.Children.Add(b);
        Grid.SetColumn(_tools, 1);
        _header.Children.Add(_tools);
        Children.Add(_header);

        // ── tree ──
        BuildList();
        SetRow(_list, 1);
        Children.Add(_list);

        // ── empty state ──
        BuildEmptyState();
        SetRow(_emptyHost, 1);
        Children.Add(_emptyHost);

        // ── name errors float over the rows below the box ──
        _bubble.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        _bubble.SetResourceReference(Border.BorderBrushProperty, "Brush.Danger");
        _bubbleText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _bubble.Child = _bubbleText;
        _bubble.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.3, Color = UI.Themes.ThemeManager.ShadowColor };
        _overlay.Children.Add(_bubble);
        SetRow(_overlay, 1);
        Children.Add(_overlay);

        // ── the edge towards the editor ──
        var edge = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false };
        edge.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        SetRowSpan(edge, 2);
        Children.Add(edge);

        MouseEnter += (_, _) => UpdateTools();
        MouseLeave += (_, _) => UpdateTools();
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            UpdateTools();
            if (_selected is not null) _selected.HasFocusRing = _list.IsKeyboardFocusWithin;
        };
        SizeChanged += (_, _) =>
        {
            UpdateHeaderLayout();
            PositionBubble();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ApplyPendingScroll();
        };
        Loc.LanguageChanged += OnLanguageChanged;
        UI.Themes.ThemeManager.I.Changed += OnThemeChanged;
        DocumentManager.I.Opened += OnDocumentOpened;
        DocumentManager.I.Closed += OnDocumentClosed;
        foreach (var d in DocumentManager.I.Documents) d.PropertyChanged += OnDocumentChanged;
        RebuildOpenFiles();
        ApplyTexts();
        ShowEmpty();
    }

    public ExplorerTree? Tree => _tree;

    public string? RootPath => _tree?.RootPath;

    public ExplorerNode? Selected => _selected;

    /// <summary>The panel is on screen: shown in a window that is neither minimized nor hidden in the tray.</summary>
    public bool IsShownNow => _window.ExplorerShown && _window.IsVisible && _window.WindowState != WindowState.Minimized;

    /// <summary>The inline name box has the keyboard focus (window shortcuts without modifiers stay out of it).</summary>
    public bool IsEditingName => _edit is { Done: false } e && e.Box.IsKeyboardFocusWithin;

    public static ExplorerPanel? Of(DependencyObject d)
    {
        for (DependencyObject? p = d; p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ExplorerPanel panel) return panel;
        return null;
    }

    private Button Tool(string glyph, Action click)
    {
        var b = new Button { Width = 24, Height = 24, Margin = new Thickness(1, 0, 0, 0), Content = new Icon { Glyph = glyph, FontSize = 12.5 } };
        b.SetResourceReference(StyleProperty, "IconButton");
        ToolTipService.SetPlacement(b, PlacementMode.Bottom);
        b.Click += (_, _) => click();
        return b;
    }

    private void BuildList()
    {
        _list.Focusable = true;
        _list.IsTabStop = true;
        _list.FocusVisualStyle = null;
        _list.Background = Brushes.Transparent;
        _list.ItemTemplate = new DataTemplate(typeof(ExplorerNode)) { VisualTree = new FrameworkElementFactory(typeof(ExplorerRow)) };
        _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer), "Scroll");
        scroll.SetValue(FocusableProperty, false);
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(Control.PaddingProperty, new Thickness(0, 5, 0, 8));
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        _list.Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = scroll };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLengthUnit(_list, VirtualizationCacheLengthUnit.Item);
        VirtualizingPanel.SetCacheLength(_list, new VirtualizationCacheLength(16));
        KeyboardNavigation.SetDirectionalNavigation(_list, KeyboardNavigationMode.None);
        KeyboardNavigation.SetTabNavigation(_list, KeyboardNavigationMode.Once);

        _list.Loaded += (_, _) =>
        {
            if (_scroll is not null) return;
            _list.ApplyTemplate();
            _scroll = _list.Template.FindName("Scroll", _list) as ScrollViewer;
            if (_scroll is not null) _scroll.ScrollChanged += (_, _) => PositionBubble();
            ApplyPendingScroll();
        };
        _list.PreviewMouseLeftButtonDown += OnListMouseDown;
        _list.PreviewMouseRightButtonDown += OnListRightDown;
        _list.MouseRightButtonUp += OnListRightUp;
        _list.PreviewMouseDown += OnListMiddleDown;
        _list.PreviewTextInput += OnTypeAhead;
    }

    private void BuildEmptyState()
    {
        var stack = new StackPanel { Margin = new Thickness(14, 32, 14, 18), MaxWidth = 250 };
        var badge = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(13), HorizontalAlignment = HorizontalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        var glyph = new Icon { Glyph = ExplorerLook.FolderOpenGlyph, FontSize = 21 };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        badge.Child = glyph;
        stack.Children.Add(badge);
        _emptyTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _emptyText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        stack.Children.Add(_emptyTitle);
        stack.Children.Add(_emptyText);
        _emptyOpen.SetResourceReference(StyleProperty, "PrimaryButton");
        _emptyOpen.Click += (_, _) => _window.OpenFolderDialog();
        _emptyActive.SetResourceReference(StyleProperty, "SecondaryButton");
        _emptyActive.Click += (_, _) => _window.OpenActiveFileFolder();
        ToolTipService.SetShowOnDisabled(_emptyActive, true);
        _emptyOpen.Content = _emptyOpenText;
        _emptyActive.Content = _emptyActiveText;
        stack.Children.Add(_emptyOpen);
        stack.Children.Add(_emptyActive);
        // the shortcut: key chips, what they do below (wraps in a narrow panel)
        var keys = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        _emptyKeysText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        keys.Children.Add(_emptyKeys);
        keys.Children.Add(_emptyKeysText);
        stack.Children.Add(keys);
        _emptyHost.Content = stack;
    }

    // ───────────── texts, theme ─────────────

    private void OnLanguageChanged()
    {
        ApplyTexts();
        if (_edit is { Done: false }) Validate();
    }

    private void OnThemeChanged()
    {
        if (_bubble.Effect is System.Windows.Media.Effects.DropShadowEffect fx) fx.Color = UI.Themes.ThemeManager.ShadowColor;
    }

    /// <summary>Shortcuts were changed in the settings: tooltips and the empty state's key chips follow.</summary>
    internal void RefreshTexts() => ApplyTexts();

    private void ApplyTexts()
    {
        _newFileButton.ToolTip = Loc.T("explorer.newFile");
        _newFolderButton.ToolTip = Loc.T("explorer.newFolder");
        _refreshButton.ToolTip = Loc.T("explorer.refresh");
        _collapseButton.ToolTip = Loc.T("explorer.collapseAll");
        _moreButton.ToolTip = Loc.T("explorer.more");
        var gesture = Keymap.GestureText("view.toggleExplorer");
        _hideButton.ToolTip = gesture.Length > 0 ? $"{Loc.T("explorer.hide")}  ({gesture})" : Loc.T("explorer.hide");
        _emptyTitle.Text = Loc.T("explorer.empty.title");
        _emptyText.Text = Loc.T("explorer.empty.text");
        _emptyOpenText.Text = Loc.T("explorer.empty.open");
        _emptyActiveText.Text = Loc.T("explorer.empty.activeFolder");
        BuildKeyHint(gesture);
        UpdateTitle();
        UpdateEmptyState(_window.ActiveDocument);
    }

    /// <summary>"Ctrl" "Shift" "E" key chips and what they do, under the empty state's buttons.</summary>
    private void BuildKeyHint(string gesture)
    {
        _emptyKeys.Children.Clear();
        var shown = gesture.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyKeys.Visibility = _emptyKeysText.Visibility = shown;
        _emptyKeysText.Text = Loc.T("explorer.empty.toggle");
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var chip = new Border { Child = new TextBlock { Text = part }, VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(StyleProperty, "KeyChip");
            _emptyKeys.Children.Add(chip);
        }
    }

    private void UpdateTitle()
    {
        if (_tree is null)
        {
            _title.Text = Loc.T("explorer.title");
            _title.ToolTip = null;
            _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        }
        else
        {
            _title.Text = _tree.Root.Name;
            _title.ToolTip = _tree.RootPath;
            _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        }
    }

    private void UpdateEmptyState(Document? active)
    {
        var folder = active?.FilePath is { } p ? Path.GetDirectoryName(p) : null;
        _emptyActive.IsEnabled = folder is not null;
        _emptyActive.ToolTip = folder is null ? Loc.T("explorer.empty.noActive") : Loc.T("explorer.empty.activeFolderTip", folder);
    }

    /// <summary>Header actions: shown while the pointer is over the panel or it has the focus (VS Code); fewer when narrow.</summary>
    private void UpdateTools()
    {
        var show = _toolsPinned || IsMouseOver || IsKeyboardFocusWithin || _edit is not null || _menuOpen;
        var target = show ? 1.0 : 0.0;
        if (Math.Abs(_tools.Opacity - target) < 0.01 && !_tools.HasAnimatedProperties) return;
        if (Motion.I.Enabled && IsVisible)
            _tools.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(show ? 120 : 200)) { EasingFunction = Motion.EaseOut });
        else
        {
            _tools.BeginAnimation(OpacityProperty, null);
            _tools.Opacity = target;
        }
        _tools.IsHitTestVisible = show;
    }

    /// <summary>Snapshots and tests: the header's actions visible without a pointer.</summary>
    internal void PinTools(bool on)
    {
        _toolsPinned = on;
        UpdateTools();
    }

    private void UpdateHeaderLayout()
    {
        var hasFolder = _tree is not null;
        var roomy = ActualWidth >= 236;
        _newFileButton.Visibility = _newFolderButton.Visibility = _moreButton.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;
        _refreshButton.Visibility = _collapseButton.Visibility = hasFolder && roomy ? Visibility.Visible : Visibility.Collapsed;
        _hideButton.Visibility = hasFolder ? Visibility.Collapsed : Visibility.Visible; // with a folder: "…" → Hide panel
    }

    // ───────────── root ─────────────

    /// <summary>Shows a folder (nothing is listed until the panel is on screen) or the empty state (null).</summary>
    public void SetRoot(string? path, IReadOnlyList<string>? expanded, bool quietIfMissing = false)
    {
        CancelEdit();
        if (_tree is not null)
        {
            _tree.RootLost -= OnRootLost;
            _tree.RowsRemoved -= OnRowsRemoved;
            _tree.Dispose();
            _tree = null;
        }
        _selected = null;
        _pendingScroll = null;
        _revealVersion++;
        if (path is null)
        {
            _list.ItemsSource = null;
            ShowEmpty();
            return;
        }
        _tree = new ExplorerTree(path, expanded)
        {
            CanApply = () => IsShownNow,
            FileState = p => _openFiles.TryGetValue(p, out var dirty) ? (true, dirty) : (false, false),
        };
        _tree.RootLost += OnRootLost;
        _tree.RowsRemoved += OnRowsRemoved;
        _list.ItemsSource = _tree.Rows;
        _quietRootLoss = quietIfMissing;
        _list.Visibility = Visibility.Visible;
        _emptyHost.Visibility = Visibility.Collapsed;
        UpdateTitle();
        UpdateHeaderLayout();
        if (IsShownNow) _ = StartAsync();
    }

    private void ShowEmpty()
    {
        _list.Visibility = Visibility.Collapsed;
        _emptyHost.Visibility = Visibility.Visible;
        UpdateTitle();
        UpdateHeaderLayout();
        UpdateEmptyState(_window.ActiveDocument);
    }

    /// <summary>The window shows the panel (again): first listing, or the watcher batch that waited.</summary>
    public void OnShown()
    {
        if (_disposed) return;
        UpdateEmptyState(_window.ActiveDocument);
        if (_tree is null || !IsShownNow) return;
        if (!_tree.IsListed && !_starting) _ = StartAsync();
        else _tree.Resume();
        FollowActive(_window.ActiveDocument);
        ApplyPendingScroll();
    }

    /// <summary>Lists the folder, starts watching it, restores expanded folders. Completes when that is done (tests await it).</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    private Task StartAsync()
    {
        if (_starting || _tree is null) return Ready;
        Ready = StartCoreAsync(_tree);
        return Ready;
    }

    private async Task StartCoreAsync(ExplorerTree tree)
    {
        _starting = true;
        try
        {
            await tree.LoadAsync();
        }
        finally
        {
            _starting = false;
        }
        if (tree != _tree) return;
        _quietRootLoss = false;
        PublishExpanded();
        if (_window.ActiveDocument?.FilePath is { } p && ExplorerRules.IsSameOrUnder(tree.RootPath, p)) await RevealAsync(p, center: true);
    }

    private void OnRootLost()
    {
        var tree = _tree;
        if (tree is null) return;
        var quiet = _quietRootLoss;
        Dispatcher.BeginInvoke(() =>
        {
            if (_tree != tree) return;
            if (!quiet) _window.Toasts.Show(Loc.T("explorer.folderGone", tree.Root.Name), ToastKind.Warning);
            _window.CloseFolder();
        });
    }

    private void OnRowsRemoved()
    {
        if (_tree is null) return;
        if (_selected is { } s && (s.Detached || !_tree.Rows.Contains(s))) Select(null);
        if (_edit is { Done: false } e && !_tree.Rows.Contains(e.Node)) CancelEdit();
    }

    /// <summary>The session keeps the expanded folders (only written when they changed).</summary>
    private void PublishExpanded()
    {
        if (_tree is null || !_tree.IsListed) return;
        var list = _tree.ExpandedRelative();
        if (!list.SequenceEqual(_window.Model.ExplorerExpanded, StringComparer.OrdinalIgnoreCase)) _window.Model.ExplorerExpanded = list;
    }

    public async Task RefreshAsync()
    {
        if (_tree is null) return;
        await _tree.RefreshLoadedAsync();
        if (_selected is { Detached: true }) Select(null);
    }

    public void CollapseAll()
    {
        if (_tree is null) return;
        CancelEdit();
        _tree.CollapseAll();
        PublishExpanded();
        _scroll?.ScrollToTop();
    }

    // ───────────── open files (dots) ─────────────

    private void OnDocumentOpened(Document d)
    {
        d.PropertyChanged += OnDocumentChanged;
        QueueOpenFiles();
    }

    private void OnDocumentClosed(Document d)
    {
        d.PropertyChanged -= OnDocumentChanged;
        QueueOpenFiles();
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Document.FilePath) or nameof(Document.IsDirty)) QueueOpenFiles();
    }

    private void QueueOpenFiles()
    {
        if (_openFilesQueued || _disposed) return;
        _openFilesQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _openFilesQueued = false;
            if (!_disposed) RebuildOpenFiles();
        });
    }

    private void RebuildOpenFiles()
    {
        var next = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DocumentManager.I.Documents)
            if (d.FilePath is { } p) next[p] = d.IsDirty || next.GetValueOrDefault(p);
        var old = _openFiles;
        _openFiles = next;
        if (_tree is null) return;
        foreach (var p in old.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var open = next.TryGetValue(p, out var dirty);
            if (old.TryGetValue(p, out var wasDirty) == open && wasDirty == dirty) continue;
            if (_tree.Find(p) is { IsDirectory: false } node)
            {
                node.IsOpen = open;
                node.IsDirty = open && dirty;
            }
        }
    }

    // ───────────── following the active tab ─────────────

    /// <summary>The active tab changed: its file is expanded to and highlighted (scrolled into view, focus stays where it is).</summary>
    public void FollowActive(Document? doc)
    {
        UpdateEmptyState(doc);
        if (_tree is null || !IsShownNow || _edit is { Done: false }) return;
        if (doc?.FilePath is not { } path || !ExplorerRules.IsSameOrUnder(_tree.RootPath, path)) return;
        _ = RevealAsync(path, center: true);
    }

    public async Task<ExplorerNode?> RevealAsync(string path, bool center)
    {
        var tree = _tree;
        if (tree is null) return null;
        var version = ++_revealVersion;
        var node = await tree.RevealAsync(path);
        if (tree != _tree || version != _revealVersion || node is null || node == tree.Root) return node;
        Select(node, scroll: true, center: center);
        PublishExpanded();
        return node;
    }

    // ───────────── selection, scrolling ─────────────

    public void Select(ExplorerNode? node, bool scroll = true, bool center = false)
    {
        if (!ReferenceEquals(_selected, node))
        {
            if (_selected is not null)
            {
                _selected.IsSelected = false;
                _selected.HasFocusRing = false;
            }
            _selected = node;
            if (node is not null)
            {
                node.IsSelected = true;
                node.HasFocusRing = _list.IsKeyboardFocusWithin;
            }
        }
        if (node is not null && scroll) ScrollIntoView(node, center);
    }

    private void ScrollIntoView(ExplorerNode node, bool center)
    {
        if (_tree is null) return;
        if (_scroll is null || !IsVisible || _scroll.ViewportHeight <= 0)
        {
            _pendingScroll = (node, center);
            return;
        }
        var index = _tree.Rows.IndexOf(node);
        if (index < 0) return;
        _list.UpdateLayout(); // the extent must include rows inserted a moment ago
        var top = index * ExplorerRow.RowHeight;
        var bottom = top + ExplorerRow.RowHeight;
        var offset = _scroll.VerticalOffset;
        var viewport = _scroll.ViewportHeight;
        if (top >= offset && bottom <= offset + viewport) return;
        if (center) _scroll.ScrollToVerticalOffset(Math.Max(0, top - (viewport - ExplorerRow.RowHeight) / 2));
        else if (top < offset) _scroll.ScrollToVerticalOffset(top);
        else _scroll.ScrollToVerticalOffset(bottom - viewport);
    }

    private void ApplyPendingScroll()
    {
        if (_pendingScroll is not { } p || _scroll is null || !IsVisible) return;
        _pendingScroll = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_tree is not null && _tree.Rows.Contains(p.Node)) ScrollIntoView(p.Node, p.Center);
        });
    }

    /// <summary>Row index of the selection, -1 when none.</summary>
    private int SelectedIndex => _selected is null || _tree is null ? -1 : _tree.Rows.IndexOf(_selected);

    private void SelectAt(int index)
    {
        if (_tree is null || _tree.Rows.Count == 0) return;
        index = Math.Clamp(index, 0, _tree.Rows.Count - 1);
        // placeholders and new-item rows are skipped
        var step = index >= SelectedIndex ? 1 : -1;
        for (var i = index; i >= 0 && i < _tree.Rows.Count; i += step)
            if (_tree.Rows[i].IsItem)
            {
                Select(_tree.Rows[i]);
                return;
            }
        for (var i = index; i >= 0 && i < _tree.Rows.Count; i -= step)
            if (_tree.Rows[i].IsItem)
            {
                Select(_tree.Rows[i]);
                return;
            }
    }

    /// <summary>Keyboard focus into the tree (Ctrl+Shift+E, Esc from the name box); selects the active file or the first row.</summary>
    public void FocusTree()
    {
        if (_tree is null)
        {
            if (_emptyOpen.IsVisible) _emptyOpen.Focus();
            return;
        }
        _list.Focus();
        Keyboard.Focus(_list);
        if (_selected is null && _tree.Rows.Count > 0) SelectAt(0);
        if (_selected is not null) _selected.HasFocusRing = _list.IsKeyboardFocusWithin;
    }

    // ───────────── keyboard ─────────────

    /// <summary>Keys of the focused tree (the window asks first, so F2 / Delete / Enter mean the tree here, not the tab).</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_tree is null || _edit is { Done: false } edit && edit.Box.IsKeyboardFocusWithin) return false;
        if (!_list.IsKeyboardFocusWithin) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var sel = _selected;
        if (mods == ModifierKeys.Shift && key == Key.F10)
        {
            OpenContextMenu(sel, null);
            return true;
        }
        if (mods != ModifierKeys.None) return false;
        var page = Math.Max(1, (int)((_scroll?.ViewportHeight ?? 240) / ExplorerRow.RowHeight) - 1);
        switch (key)
        {
            case Key.Down:
                SelectAt(SelectedIndex + 1);
                return true;
            case Key.Up:
                SelectAt(sel is null ? 0 : SelectedIndex - 1);
                return true;
            case Key.PageDown:
                SelectAt(SelectedIndex + page);
                return true;
            case Key.PageUp:
                SelectAt(Math.Max(0, SelectedIndex - page));
                return true;
            case Key.Home:
                SelectAt(0);
                return true;
            case Key.End:
                SelectAt(_tree.Rows.Count - 1);
                return true;
            case Key.Right when sel is { IsDirectory: true }:
                if (!sel.IsExpanded) _ = ExpandAsync(sel);
                else if (SelectedIndex + 1 < _tree.Rows.Count && _tree.Rows[SelectedIndex + 1].Depth > sel.Depth) SelectAt(SelectedIndex + 1);
                return true;
            case Key.Left when sel is not null:
                if (sel is { IsDirectory: true, IsExpanded: true }) Collapse(sel);
                else if (sel.Parent is { } parent && parent != _tree.Root) Select(parent);
                return true;
            case Key.Enter when sel is not null:
                if (sel.IsDirectory) Toggle(sel);
                else OpenNode(sel, focus: true);
                return true;
            case Key.Space when sel is not null:
                if (sel.IsDirectory) Toggle(sel);
                else OpenNode(sel, focus: false);
                return true;
            case Key.F2 when sel is not null:
                BeginRename(sel);
                return true;
            case Key.Delete when sel is not null:
                _ = DeleteAsync(sel);
                return true;
            case Key.Escape:
                _window.ActiveView?.FocusContent();
                return true;
            case Key.Apps:
                OpenContextMenu(sel, null);
                return true;
        }
        return false;
    }

    /// <summary>Typing letters jumps to the next entry starting with them (one letter again = the next such entry).</summary>
    private void OnTypeAhead(object sender, TextCompositionEventArgs e)
    {
        if (_tree is null || _edit is { Done: false } || string.IsNullOrWhiteSpace(e.Text)) return;
        var now = DateTime.UtcNow;
        if (now - _typedAt > TimeSpan.FromMilliseconds(900)) _typed = "";
        _typedAt = now;
        var prefix = _typed + e.Text;
        var names = _tree.Rows.Select(r => r.IsItem ? r.Name : null).ToList();
        var current = SelectedIndex;
        int hit;
        if (prefix.Length > 1 && prefix.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(prefix[0])))
            hit = ExplorerRules.FindByPrefix(names, current + 1, prefix[..1]);
        else
            hit = ExplorerRules.FindByPrefix(names, _typed.Length == 0 ? current + 1 : Math.Max(0, current), prefix);
        _typed = prefix;
        if (hit >= 0) Select(_tree.Rows[hit]);
        e.Handled = true;
    }

    // ───────────── mouse ─────────────

    private static ExplorerRow? RowFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ExplorerRow row) return row;
            if (d is ItemsControl) return null;
        }
        return null;
    }

    private static bool IsInside<T>(object source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is T) return true;
            if (d is ExplorerPanel) return false;
        }
        return false;
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInside<TextBox>(e.OriginalSource) || IsInside<ScrollBar>(e.OriginalSource)) return;
        _list.Focus();
        e.Handled = true;
        if (RowFrom(e.OriginalSource)?.Node is not { IsItem: true } node) return;
        Select(node, scroll: false);
        if (e.ClickCount > 1) return;
        if (node.IsDirectory) Toggle(node);
        else OpenNode(node, focus: (Keyboard.Modifiers & ModifierKeys.Control) == 0);
    }

    /// <summary>Middle click opens a file and leaves the focus in the tree.</summary>
    private void OnListMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || RowFrom(e.OriginalSource)?.Node is not { IsItem: true, IsDirectory: false } node) return;
        Select(node, scroll: false);
        OpenNode(node, focus: false);
        e.Handled = true;
    }

    private void OnListRightDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInside<TextBox>(e.OriginalSource)) return;
        _list.Focus();
        if (RowFrom(e.OriginalSource)?.Node is { IsItem: true } node) Select(node, scroll: false);
        e.Handled = true;
    }

    private void OnListRightUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInside<TextBox>(e.OriginalSource) || _tree is null) return;
        var node = RowFrom(e.OriginalSource)?.Node is { IsItem: true } n ? n : null;
        OpenContextMenu(node, e);
        e.Handled = true;
    }

    // ───────────── expand / collapse / open ─────────────

    public void Toggle(ExplorerNode dir)
    {
        if (dir.IsExpanded) Collapse(dir);
        else _ = ExpandAsync(dir);
    }

    public async Task ExpandAsync(ExplorerNode dir)
    {
        if (_tree is null) return;
        await _tree.ExpandAsync(dir);
        PublishExpanded();
    }

    public void Collapse(ExplorerNode dir)
    {
        if (_tree is null) return;
        if (_edit is { Done: false } e && ExplorerRules.IsSameOrUnder(dir.FullPath, e.Node.FullPath) && e.Node != dir) CancelEdit();
        // a selection inside goes to the folder itself (it would vanish with the rows)
        if (_selected is { } s && s != dir && ExplorerRules.IsSameOrUnder(dir.FullPath, s.FullPath)) Select(dir, scroll: false);
        _tree.Collapse(dir);
        PublishExpanded();
    }

    public void OpenNode(ExplorerNode node, bool focus)
    {
        if (node.IsDirectory || !node.IsItem) return;
        _window.OpenFile(node.FullPath, focus: focus);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loc.LanguageChanged -= OnLanguageChanged;
        UI.Themes.ThemeManager.I.Changed -= OnThemeChanged;
        DocumentManager.I.Opened -= OnDocumentOpened;
        DocumentManager.I.Closed -= OnDocumentClosed;
        foreach (var d in DocumentManager.I.Documents) d.PropertyChanged -= OnDocumentChanged;
        _tree?.Dispose();
        _tree = null;
    }
}
