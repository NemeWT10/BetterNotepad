using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI.Controls;

/// <summary>
/// A tab strip with its content area. Views are created lazily, the few most recent stay cached
/// (instant switching), older ones are disposed to keep memory low.
/// </summary>
public sealed class TabGroupView : Grid
{
    public const string TabDragFormat = "BetterNotepad.Tab";

    /// <summary>Height of the tab strip; tabs (32 px) stand on its bottom line, 6 px below the title bar.</summary>
    public const double StripHeight = 38;

    private const double StripTop = 6;
    private const int MaxCachedViews = 6;

    private readonly MainWindow _owner;
    private readonly TabStripPanel _tabsPanel = new();
    private readonly ScrollViewer _tabScroll;
    private readonly Grid _contentHost = new();
    private readonly Dictionary<Document, IDocumentView> _views = new();
    private readonly LinkedList<Document> _viewLru = new();
    private readonly Border _viewToggleHost;
    private readonly StackPanel _viewToggle = new() { Orientation = Orientation.Horizontal };
    private readonly Border _dropMarker;
    private readonly StackPanel _scrollButtons = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(4, StripTop, 4, 0) };
    private readonly Button _scrollLeft;
    private readonly Button _scrollRight;
    private IDocumentView? _activeView;
    private TabChip? _dragChip;
    private Point _dragStart;
    private bool _dragging;
    private bool _isGroupActive = true;

    public TabGroupView(MainWindow owner, TabGroup group)
    {
        _owner = owner;
        Group = group;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(StripHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ── tab bar ──
        var bar = new Grid { Background = Brushes.Transparent };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var barBack = new Border();
        barBack.SetResourceReference(Border.BackgroundProperty, "Brush.Chrome");
        Children.Add(barBack);
        var underline = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        underline.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        Children.Add(underline);

        // tabs, then "+" right after the last tab (Windows 11 style); ‹ › at the right edge only on overflow
        var left = new DockPanel { LastChildFill = false, Margin = new Thickness(4, 0, 0, 0) };
        var addButton = new Button
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(4, StripTop, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new Icon { Glyph = Glyphs.Add, FontSize = 12 },
        };
        addButton.SetResourceReference(StyleProperty, "IconButton");
        addButton.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("[tab.new]") { Source = Loc.I });
        addButton.Click += (_, _) => _owner.NewTab(Group);
        DockPanel.SetDock(addButton, Dock.Left);

        // overflow: the strip fades out at a cut edge and gets ‹ › next to the + button (wheel scrolls too)
        _scrollLeft = StripButton(Glyphs.ChevronLeft, -1);
        _scrollRight = StripButton(Glyphs.ChevronRight, +1);
        _scrollButtons.Children.Add(_scrollLeft);
        _scrollButtons.Children.Add(_scrollRight);
        DockPanel.SetDock(_scrollButtons, Dock.Right);

        _tabScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = _tabsPanel,
            Focusable = false,
        };
        _tabScroll.PreviewMouseWheel += (_, e) =>
        {
            _tabScroll.ScrollToHorizontalOffset(_tabScroll.HorizontalOffset - e.Delta * 0.6);
            e.Handled = true;
        };
        _tabScroll.ScrollChanged += (_, _) => UpdateOverflow();
        var stripArea = new Grid();
        DockPanel.SetDock(stripArea, Dock.Left);
        stripArea.Children.Add(_tabScroll);
        _stripArea = stripArea;
        _stripHost = left;
        left.SizeChanged += (_, _) => FitStrip();
        _dropMarker = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 4), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _dropMarker.SetResourceReference(Border.BackgroundProperty, "Brush.AccentGradient");
        stripArea.Children.Add(_dropMarker);
        left.Children.Add(_scrollButtons);
        left.Children.Add(stripArea);
        left.Children.Add(addButton);
        bar.Children.Add(left);

        _viewToggleHost = new Border { Margin = new Thickness(8, 7, 10, 0), VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, Child = _viewToggle };
        _viewToggleHost.SetResourceReference(StyleProperty, "SegmentHost");
        SetColumn(_viewToggleHost, 1);
        bar.Children.Add(_viewToggleHost);
        Children.Add(bar);
        _stripParts = [barBack, underline, bar];

        // empty strip area: double-click → new tab (Notepad++)
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            var src = e.OriginalSource;
            if (ReferenceEquals(src, bar) || ReferenceEquals(src, _tabsPanel) || src is ScrollContentPresenter || ReferenceEquals(src, left) || ReferenceEquals(src, _stripArea))
            {
                _owner.NewTab(Group);
                e.Handled = true;
            }
        };

        bar.AllowDrop = true;
        bar.DragOver += OnTabDragOver;
        bar.DragLeave += (_, _) => _dropMarker.Visibility = Visibility.Collapsed;
        bar.Drop += OnTabDrop;

        // ── content ──
        SetRow(_contentHost, 1);
        _contentHost.SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");
        Children.Add(_contentHost);

        PreviewMouseDown += (_, _) => Activated?.Invoke(this);
        GotKeyboardFocus += (_, _) => Activated?.Invoke(this);

        foreach (var d in group.Tabs) AddChip(d, animate: false);
        group.Tabs.CollectionChanged += OnTabsChanged;
        group.PropertyChanged += OnGroupChanged;
        Loaded += (_, _) => Loc.LanguageChanged += RebuildViewToggle;
        Unloaded += (_, _) => Loc.LanguageChanged -= RebuildViewToggle;
        ShowActive();
    }

    public TabGroup Group { get; }

    private Button StripButton(string glyph, int direction)
    {
        var b = new Button
        {
            Width = 22,
            Height = 28,
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new Icon { Glyph = glyph, FontSize = 10 },
        };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.Click += (_, _) => _tabScroll.ScrollToHorizontalOffset(_tabScroll.HorizontalOffset + direction * Math.Max(120, _tabScroll.ViewportWidth * 0.6));
        return b;
    }

    private readonly UIElement[] _stripParts;
    private bool _compact;

    /// <summary>Pinned-on-top window: no tab strip, the content goes edge to edge.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            RowDefinitions[0].Height = new GridLength(value ? 0 : StripHeight);
            foreach (var part in _stripParts) part.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>True when some tabs are scrolled out of view (for tests and snapshots).</summary>
    public bool StripOverflows => _tabScroll.ScrollableWidth > 0.5;

    private Grid _stripArea = null!;
    private DockPanel _stripHost = null!;

    /// <summary>The strip may use what is left after "+" (and ‹ › when the tabs overflow).</summary>
    private void FitStrip()
    {
        var room = _stripHost.ActualWidth - 32 - (_scrollButtons.Visibility == Visibility.Visible ? 52 : 0);
        room = Math.Max(0, room);
        _stripArea.MaxWidth = room;
        _tabsPanel.AvailableWidth = room;
    }

    private void UpdateOverflow()
    {
        var overflow = StripOverflows;
        var was = _scrollButtons.Visibility == Visibility.Visible;
        _scrollButtons.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (was != overflow) FitStrip();
        if (!overflow)
        {
            _tabScroll.OpacityMask = null;
            return;
        }
        var atStart = _tabScroll.HorizontalOffset < 0.5;
        var atEnd = _tabScroll.HorizontalOffset > _tabScroll.ScrollableWidth - 0.5;
        _scrollLeft.IsEnabled = !atStart;
        _scrollRight.IsEnabled = !atEnd;
        var w = Math.Max(1, _tabScroll.ActualWidth);
        var fade = Math.Min(0.25, 28 / w);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(atStart ? Colors.Black : Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, fade));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - fade));
        mask.GradientStops.Add(new GradientStop(atEnd ? Colors.Black : Colors.Transparent, 1));
        mask.Freeze();
        _tabScroll.OpacityMask = mask;
    }

    public IDocumentView? ActiveView => _activeView;

    public event Action<TabGroupView>? Activated;

    public bool IsGroupActive
    {
        get => _isGroupActive;
        set
        {
            _isGroupActive = value;
            foreach (var chip in Chips) chip.GroupActive = value;
        }
    }

    private IEnumerable<TabChip> Chips => _tabsPanel.Children.OfType<TabChip>();

    public TabChip? ChipFor(Document doc) => Chips.FirstOrDefault(c => c.Document == doc);

    // ───────────── tabs ─────────────

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (Document d in e.NewItems!) AddChip(d, animate: true);
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (Document d in e.OldItems!) RemoveChip(d);
                break;
            default:
                SyncOrder();
                break;
        }
        SyncOrder();
    }

    private void AddChip(Document doc, bool animate)
    {
        if (ChipFor(doc) is not null) return;
        var chip = new TabChip(doc) { GroupActive = _isGroupActive };
        chip.CloseRequested += c => _ = _owner.CloseTabAsync(Group, c.Document);
        chip.RenameCommitted += (c, name) => _owner.Rename(c.Document, name, c);
        chip.PreviewMouseLeftButtonDown += OnChipMouseDown;
        chip.PreviewMouseMove += OnChipMouseMove;
        chip.PreviewMouseLeftButtonUp += OnChipMouseUp;
        chip.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                _ = _owner.CloseTabAsync(Group, doc);
            }
        };
        chip.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Group.Active = doc;
            var menu = _owner.BuildTabMenu(Group, doc);
            menu.PlacementTarget = chip;
            menu.IsOpen = true;
        };
        var index = Group.Tabs.IndexOf(doc);
        _tabsPanel.Children.Insert(Math.Clamp(index, 0, _tabsPanel.Children.Count), chip);
        chip.IsActive = Group.Active == doc;
        if (animate) Motion.Appear(chip, rise: 6, fromScale: 0.9, ms: 260);
    }

    private void RemoveChip(Document doc)
    {
        var chip = ChipFor(doc);
        if (chip is null) return;
        if (_views.Remove(doc, out var view))
        {
            _contentHost.Children.Remove(view.Element);
            _viewLru.Remove(doc);
            if (_activeView == view) _activeView = null;
            view.Dispose();
        }
        chip.IsHitTestVisible = false;
        Motion.Disappear(chip, () => _tabsPanel.Children.Remove(chip), sink: 4, ms: 140);
    }

    private void SyncOrder()
    {
        for (var i = 0; i < Group.Tabs.Count; i++)
        {
            var chip = ChipFor(Group.Tabs[i]);
            if (chip is null) continue;
            var current = _tabsPanel.Children.IndexOf(chip);
            if (current != i && i < _tabsPanel.Children.Count)
            {
                _tabsPanel.Children.RemoveAt(current);
                _tabsPanel.Children.Insert(Math.Min(i, _tabsPanel.Children.Count), chip);
            }
        }
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabGroup.Active)) ShowActive();
    }

    // ───────────── content ─────────────

    private void ShowActive()
    {
        var doc = Group.Active;
        foreach (var chip in Chips) chip.IsActive = chip.Document == doc;
        if (_activeView is not null && _activeView.Document != doc)
        {
            _activeView.OnDeactivated();
            _activeView.Element.Visibility = Visibility.Collapsed;
            _activeView = null;
        }
        if (doc is null)
        {
            RebuildViewToggle();
            ActiveViewChanged?.Invoke(this);
            return;
        }

        if (!_views.TryGetValue(doc, out var view))
        {
            view = DocumentViewRegistry.Create(doc);
            _views[doc] = view;
            _contentHost.Children.Add(view.Element);
        }
        _viewLru.Remove(doc);
        _viewLru.AddFirst(doc);
        Evict();

        view.Element.Visibility = Visibility.Visible;
        _activeView = view;
        view.OnActivated();
        if (doc is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged -= OnActiveDocChanged;
            npc.PropertyChanged += OnActiveDocChanged;
        }
        RebuildViewToggle();
        ChipFor(doc)?.BringIntoView();
        ActiveViewChanged?.Invoke(this);
    }

    public event Action<TabGroupView>? ActiveViewChanged;

    private void OnActiveDocChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != Group.Active)
        {
            ((INotifyPropertyChanged)sender!).PropertyChanged -= OnActiveDocChanged;
            return;
        }
        if (e.PropertyName is nameof(TextDocument.Kind) or nameof(TextDocument.ViewMode)) RebuildViewToggle();
    }

    private void Evict()
    {
        while (_viewLru.Count > MaxCachedViews)
        {
            var oldest = _viewLru.Last!.Value;
            _viewLru.RemoveLast();
            if (oldest == Group.Active || !_views.Remove(oldest, out var view)) continue;
            _contentHost.Children.Remove(view.Element);
            view.Dispose();
            oldest.ReleaseHeavyContent();
        }
    }

    public IDocumentView? ViewFor(Document doc) => _views.GetValueOrDefault(doc);

    public void CaptureState()
    {
        foreach (var v in _views.Values) v.CaptureState();
    }

    /// <summary>
    /// The window went to the tray: every view drops what it rebuilds by itself when shown again (<see cref="IReleasableView"/>:
    /// rendered PDF pages, a Markdown preview document). The views stay, so caret, folds, find, zoom and scroll are untouched
    /// and the window comes back instantly. (Closing the cached views as well was measured: no gain in the tray.)
    /// </summary>
    public void ReleaseRendering()
    {
        foreach (var v in _views.Values) (v as IReleasableView)?.ReleaseRendering();
    }

    public void DisposeViews()
    {
        foreach (var v in _views.Values) v.Dispose();
        _views.Clear();
        _viewLru.Clear();
        _contentHost.Children.Clear();
        Group.Tabs.CollectionChanged -= OnTabsChanged;
        Group.PropertyChanged -= OnGroupChanged;
    }

    // ───────────── Preview | Split | Edit ─────────────

    private void RebuildViewToggle()
    {
        _viewToggle.Children.Clear();
        if (Group.Active is not TextDocument doc || !FileTypes.HasPreview(doc.Kind) || !DocumentViewRegistry.HasPreview(doc.Kind))
        {
            _viewToggleHost.Visibility = Visibility.Collapsed;
            return;
        }
        _viewToggleHost.Visibility = Visibility.Visible;
        var groupName = "view-" + Group.Id.ToString("N");
        if (doc.Kind == DocumentKind.Csv)
        {
            // Table | Split | Text: the table, the table next to the text (selection and scrolling follow), the text alone
            AddSegment(doc, ViewMode.Preview, Glyphs.Table, "view.table", groupName);
            AddSegment(doc, ViewMode.Split, Glyphs.Columns, "view.split", groupName);
            AddSegment(doc, ViewMode.Edit, Glyphs.Text, "view.text", groupName);
            return;
        }
        AddSegment(doc, ViewMode.Preview, Glyphs.View, "view.preview", groupName);
        AddSegment(doc, ViewMode.Split, Glyphs.Columns, "view.split", groupName);
        AddSegment(doc, ViewMode.Edit, Glyphs.Edit, "view.edit", groupName);
    }

    private void AddSegment(TextDocument doc, ViewMode mode, string glyph, string key, string groupName)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Glyph = glyph, FontSize = 12 });
        content.Children.Add(new TextBlock { Text = Loc.T(key), Margin = new Thickness(6, 0, 0, 0), FontSize = 12 });
        var rb = new RadioButton { Content = content, GroupName = groupName, IsChecked = doc.ViewMode == mode, Padding = new Thickness(10, 2, 10, 3) };
        rb.SetResourceReference(StyleProperty, "Segment");
        rb.Checked += (_, _) =>
        {
            doc.ViewMode = mode;
            Motion.Bounce(content, 1.12);
        };
        _viewToggle.Children.Add(rb);
    }

    // ───────────── drag: reorder, move between windows, tear off ─────────────

    private void OnChipMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabChip chip || chip.IsRenaming) return;
        if (e.OriginalSource is DependencyObject src && FindParent<Button>(src) is not null) return;
        if (e.ClickCount == 2)
        {
            chip.BeginRename();
            e.Handled = true;
            return;
        }
        Group.Active = chip.Document;
        Activated?.Invoke(this);
        _dragChip = chip;
        _dragStart = e.GetPosition(this);
        _dragging = false;
    }

    private void OnChipMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragChip is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < 6 && Math.Abs(pos.Y - _dragStart.Y) < 6) return;
            _dragging = true;
            _dragChip.CaptureMouse();
        }

        // pulled out of the strip → OS drag (drop on another window's tabs, or tear off)
        if (pos.Y < -24 || pos.Y > StripHeight + 46)
        {
            var chip = _dragChip;
            EndDrag();
            StartOsDrag(chip);
            return;
        }

        var target = IndexAt(e.GetPosition(_tabsPanel).X);
        var current = Group.Tabs.IndexOf(_dragChip.Document);
        if (target >= 0 && target != current) Group.Move(_dragChip.Document, target);
    }

    private void OnChipMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _dragChip?.ReleaseMouseCapture();
        _dragChip = null;
        _dragging = false;
    }

    private int IndexAt(double x)
    {
        var acc = 0.0;
        var i = 0;
        foreach (var chip in Chips)
        {
            var w = chip.ActualWidth + chip.Margin.Left + chip.Margin.Right;
            if (x < acc + w) return i;
            acc += w;
            i++;
        }
        return Math.Max(0, i - 1);
    }

    private void StartOsDrag(TabChip chip)
    {
        var data = new DataObject(TabDragFormat, $"{chip.Document.Id:N}|{Group.Id:N}");
        var result = DragDrop.DoDragDrop(chip, data, DragDropEffects.Move);
        if (result == DragDropEffects.None && Group.Tabs.Contains(chip.Document))
        {
            // dropped outside any tab strip → its own window under the cursor
            var p = MainWindow.CursorScreenPosition();
            _owner.TearOff(Group, chip.Document, p);
        }
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabDragFormat))
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) return; // the window handles files
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        var x = e.GetPosition(_tabsPanel).X;
        var index = IndexAt(x);
        var offset = Chips.Take(index).Sum(c => c.ActualWidth + c.Margin.Left + c.Margin.Right) - _tabScroll.HorizontalOffset;
        _dropMarker.Margin = new Thickness(Math.Max(0, offset), 8, 0, 4);
        _dropMarker.Visibility = Visibility.Visible;
    }

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        _dropMarker.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(TabDragFormat) is not string payload) return;
        e.Handled = true;
        var parts = payload.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var docId) || !Guid.TryParse(parts[1], out var groupId)) return;
        var doc = DocumentManager.I.FindById(docId);
        var source = Workspace.I.AllGroups.FirstOrDefault(g => g.Id == groupId);
        if (doc is null || source is null) return;
        var index = IndexAt(e.GetPosition(_tabsPanel).X);
        e.Effects = DragDropEffects.Move;
        _owner.MoveTab(source, Group, doc, index);
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
