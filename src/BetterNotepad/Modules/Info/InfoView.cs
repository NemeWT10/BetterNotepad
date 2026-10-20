using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Editor;
using BetterNotepad.Metadata;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Info;

/// <summary>
/// "Info" tab for files nothing can display: name, size, dates, path, open folder; SHA-256 / MD5 on demand (with progress);
/// metadata groups read-only, with "Clear metadata" after a confirmation.
/// </summary>
public sealed class InfoView : Grid, IDocumentView
{
    private readonly FileInfoDocument _doc;
    private readonly StackPanel _column = new() { MaxWidth = 760, Margin = new Thickness(32, 28, 32, 40) };
    private readonly StackPanel _metaHost = new();
    private readonly Dictionary<HashAlgorithmKind, TextBox> _hashBoxes = new();
    private Button? _computeButton;
    private Button? _stripButton;
    private Border? _progressFill;
    private Grid? _progressTrack;
    private Dictionary<HashAlgorithmKind, string>? _hashes;
    private MetadataReport? _report;
    private string? _metaError;
    private bool _metaLoading;
    private bool _hashing;
    private CancellationTokenSource? _cts;

    public InfoView(FileInfoDocument doc)
    {
        _doc = doc;
        SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Content = _column };
        Children.Add(scroll);
        Loaded += (_, _) =>
        {
            Loc.LanguageChanged += Rebuild;
            Rebuild();
            if (_report is null && !_metaLoading) _ = LoadMetadataAsync();
        };
        Unloaded += (_, _) => Loc.LanguageChanged -= Rebuild;
    }

    public Document Document => _doc;

    public FrameworkElement Element => this;

    public CodeEditor? Editor => null;

    /// <summary>
    /// Shown inside another view (the image tab's info panel): no header (the tab names the file), narrow margins and labels.
    /// </summary>
    public bool Embedded
    {
        get => _embedded;
        init
        {
            _embedded = value;
            if (value)
            {
                _column.MaxWidth = double.PositiveInfinity;
                _column.Margin = new Thickness(18, 14, 18, 24);
            }
        }
    }

    private readonly bool _embedded;

    /// <summary>"Clear metadata" rewrote the file (the owner refreshes what it knows about it).</summary>
    public event Action? Stripped;

    /// <summary>For tests and snapshots: metadata has been read (or failed).</summary>
    internal bool MetadataReady => !_metaLoading && (_report is not null || _metaError is not null);

    private string Path => _doc.FilePath ?? "";

    // ───────────── layout ─────────────

    private void Rebuild()
    {
        _column.Children.Clear();
        _hashBoxes.Clear();
        var info = new FileInfo(Path);
        var exists = info.Exists;

        // header: icon, name, folder, "open folder" (an embedded panel has its own title)
        var head = new Grid { Margin = new Thickness(0, 0, 0, 22), Visibility = _embedded ? Visibility.Collapsed : Visibility.Visible };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 16, 0), BorderThickness = new Thickness(1) };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        tile.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var glyph = new Icon { Glyph = _doc.Kind == DocumentKind.Hex ? Glyphs.Hex : Glyphs.Info, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        tile.Child = glyph;
        head.Children.Add(tile);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Text(_doc.Title, 20, "Brush.Text", FontWeights.SemiBold));
        var hint = Text(exists ? Loc.T("info.unsupported") : Loc.T("info.missing"), 12.5, exists ? "Brush.TextDim" : "Brush.Warning");
        hint.Margin = new Thickness(0, 3, 0, 0);
        hint.TextWrapping = TextWrapping.Wrap;
        names.Children.Add(hint);
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        var reveal = MakeButton(Loc.T("info.openFolder"), Glyphs.OpenFolder, "SecondaryButton", () => MainWindow.Of(this)?.RevealInExplorer(_doc));
        reveal.VerticalAlignment = VerticalAlignment.Center;
        reveal.IsEnabled = exists;
        reveal.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(reveal, 2);
        head.Children.Add(reveal);
        _column.Children.Add(head);

        // file
        var file = Section(Loc.T("info.fileGroup"));
        if (exists)
        {
            Row(file, Loc.T("info.size"), FormatSize(info.Length));
            Row(file, Loc.T("info.kind"), info.Extension.Length > 0 ? info.Extension.TrimStart('.').ToUpperInvariant() : "—");
            // a narrow panel: the short date (one line)
            string Date(DateTime t) => _embedded ? t.ToString("g", Loc.I.Culture) : FormatDate(t);
            Row(file, Loc.T("info.created"), Date(info.CreationTime));
            Row(file, Loc.T("info.modified"), Date(info.LastWriteTime));
            Row(file, Loc.T("info.accessed"), Date(info.LastAccessTime));
        }
        Row(file, Loc.T("info.path"), Path, selectable: true, last: true);

        // checksums, on demand
        var hashStart = _column.Children.Count;
        if (exists)
        {
            var hashes = Section(Loc.T("info.hashGroup"), out var hashHeader);
            _computeButton = MakeButton(Loc.T(_hashes is null ? "info.compute" : "info.copyAll"), _hashes is null ? Glyphs.Refresh : Glyphs.Copy, "SecondaryButton", OnHashButton);
            _computeButton.Padding = new Thickness(12, 5, 12, 5);
            _computeButton.IsEnabled = !_hashing;
            hashHeader.Children.Add(_computeButton);
            Row(hashes, "SHA-256", "", mono: true, hash: HashAlgorithmKind.Sha256);
            Row(hashes, "MD5", "", mono: true, hash: HashAlgorithmKind.Md5, last: !_hashing);
            if (_hashing)
            {
                _progressTrack = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 14) };
                var track = new Border { CornerRadius = new CornerRadius(2) };
                track.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceHover");
                _progressFill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
                _progressFill.SetResourceReference(Border.BackgroundProperty, "Brush.AccentFill");
                _progressTrack.Children.Add(track);
                _progressTrack.Children.Add(_progressFill);
                hashes.Children.Add(_progressTrack);
            }
            FillHashes();
        }

        // metadata (read-only) + clear
        var metaStart = _column.Children.Count;
        var meta = Section(Loc.T("info.metaGroup"), out var metaHeader);
        _stripButton = MakeButton(Loc.T("info.strip"), Glyphs.Delete, "SecondaryButton", () => _ = StripAsync());
        _stripButton.Padding = new Thickness(12, 5, 12, 5);
        _stripButton.IsEnabled = exists && !_doc.IsLocked && _report is { } r && (r.EntryCount > 0 || r.CanStripContent);
        metaHeader.Children.Add(_stripButton);
        (_metaHost.Parent as Panel)?.Children.Remove(_metaHost); // the previous build's card
        meta.Children.Add(_metaHost);
        FillMetadata();

        // in the image tab's panel the picture's metadata (camera, GPS…) matters more than checksums: it comes first
        if (_embedded && exists)
        {
            var hashParts = _column.Children.Cast<UIElement>().Skip(hashStart).Take(Math.Max(0, metaStart - hashStart)).ToList();
            foreach (var part in hashParts) _column.Children.Remove(part);
            foreach (var part in hashParts) _column.Children.Add(part);
        }
    }

    private StackPanel Section(string title) => Section(title, out _);

    /// <summary>Title (with an optional button on the right) and a card; returns the card's row stack.</summary>
    private StackPanel Section(string title, out StackPanel headerTools)
    {
        var header = new Grid { Margin = new Thickness(4, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Bottom };
        t.SetResourceReference(StyleProperty, "SectionTitle");
        t.Margin = new Thickness(0, 0, 0, 2);
        header.Children.Add(t);
        headerTools = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(headerTools, 1);
        header.Children.Add(headerTools);
        _column.Children.Add(header);
        var rows = new StackPanel();
        var card = new Border { Child = rows };
        card.SetResourceReference(StyleProperty, "Card");
        card.Margin = new Thickness(0, 0, 0, 22);
        _column.Children.Add(card);
        return rows;
    }

    private void Row(StackPanel rows, string label, string value, bool selectable = false, bool mono = false, HashAlgorithmKind? hash = null, bool last = false)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_embedded ? 112 : 170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = Text(label, 12.5, "Brush.TextDim");
        l.VerticalAlignment = VerticalAlignment.Center;
        l.TextTrimming = TextTrimming.CharacterEllipsis;
        l.Margin = new Thickness(0, 10, 12, 10);
        grid.Children.Add(l);
        FrameworkElement v;
        if (selectable || mono)
        {
            var box = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = mono ? 12 : 12.5 };
            box.SetResourceReference(StyleProperty, "BareField");
            if (mono) box.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
            if (hash is { } h) _hashBoxes[h] = box;
            v = box;
        }
        else
        {
            var tb = Text(value, 12.5, "Brush.Text");
            tb.TextWrapping = TextWrapping.Wrap;
            tb.VerticalAlignment = VerticalAlignment.Center;
            v = tb;
        }
        v.Margin = new Thickness(0, 8, 0, 8);
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        rows.Children.Add(grid);
        if (!last)
        {
            var sep = new Border { Height = 1 };
            sep.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
            rows.Children.Add(sep);
        }
    }

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return tb;
    }

    private static Button MakeButton(string text, string glyph, string style, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Glyph = glyph, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content };
        b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => click();
        return b;
    }

    // ───────────── values ─────────────

    private static string FormatSize(long bytes)
    {
        var c = Loc.I.Culture;
        string human = bytes switch
        {
            < 1024 => Loc.Plural("hex.bytes", bytes),
            < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", c) + " KB",
            < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("0.##", c) + " MB",
            _ => (bytes / 1024.0 / 1024 / 1024).ToString("0.##", c) + " GB",
        };
        return bytes < 1024 ? human : $"{human}  ({Loc.Plural("hex.bytes", bytes)})";
    }

    private static string FormatDate(DateTime t) => t.ToString("f", Loc.I.Culture);

    private static string FormatValue(MetadataEntry e)
    {
        switch (e.Kind)
        {
            case MetadataValueKind.Boolean:
                return Loc.T(e.Value == "true" ? "info.yes" : "info.no");
            case MetadataValueKind.DateTime:
                return DateTime.TryParse(e.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToString("f", Loc.I.Culture) : e.Value;
            default:
                return e.Value;
        }
    }

    /// <summary>meta.* keys have PL / EN labels for the common fields; anything else shows its raw name.</summary>
    private static string LabelOf(MetadataEntry e) =>
        Loc.AllEntries.ContainsKey(e.LabelKey) ? Loc.T(e.LabelKey) : e.RawName ?? e.Key;

    private static string GroupLabel(MetadataGroup g) => Loc.AllEntries.ContainsKey(g.LabelKey) ? Loc.T(g.LabelKey) : g.Id.ToString();

    // ───────────── hashes ─────────────

    private void FillHashes()
    {
        foreach (var (kind, box) in _hashBoxes)
        {
            box.Text = _hashes?.GetValueOrDefault(kind) ?? (_hashing ? Loc.T("info.computing") : "—");
            box.SetResourceReference(Control.ForegroundProperty, _hashes is null ? "Brush.TextFaint" : "Brush.Text");
        }
    }

    private void OnHashButton()
    {
        if (_hashes is { } h)
        {
            try
            {
                Clipboard.SetText($"SHA-256  {h[HashAlgorithmKind.Sha256]}{Environment.NewLine}MD5      {h[HashAlgorithmKind.Md5]}");
                MainWindow.Of(this)?.Toasts.Show(Loc.T("info.copied"), ToastKind.Success);
            }
            catch (Exception ex)
            {
                MainWindow.Of(this)?.Toasts.Show(ex.Message, ToastKind.Error);
            }
            return;
        }
        _ = ComputeHashesAsync();
    }

    internal async Task ComputeHashesAsync()
    {
        if (_hashing || _hashes is not null) return;
        _hashing = true;
        _cts = new CancellationTokenSource();
        Rebuild();
        var progress = new Progress<double>(p =>
        {
            if (_progressFill is not null && _progressTrack is not null) _progressFill.Width = Math.Clamp(p, 0, 1) * _progressTrack.ActualWidth;
            if (_hashBoxes.Count > 0) foreach (var box in _hashBoxes.Values) box.Text = Loc.T("info.computingPct", Math.Round(p * 100));
        });
        try
        {
            var all = await FileHasher.HashAsync(Path, [HashAlgorithmKind.Sha256, HashAlgorithmKind.Md5], progress, _cts.Token);
            _hashes = new Dictionary<HashAlgorithmKind, string>(all);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"info: hash of {Path} failed: {ex.Message}");
            MainWindow.Of(this)?.Toasts.Show(Loc.T("info.hashFailed", ex.Message), ToastKind.Error);
        }
        finally
        {
            _hashing = false;
        }
        Rebuild();
    }

    // ───────────── metadata ─────────────

    private async Task LoadMetadataAsync()
    {
        if (!File.Exists(Path))
        {
            _metaError = Loc.T("info.missing");
            FillMetadata();
            return;
        }
        _metaLoading = true;
        FillMetadata();
        try
        {
            _report = await MetadataReader.ReadAsync(Path);
            _metaError = null;
        }
        catch (Exception ex)
        {
            Log.Warn($"info: metadata of {Path}: {ex.Message}");
            _report = null;
            _metaError = Loc.T("info.metaFailed", ex.Message);
        }
        finally
        {
            _metaLoading = false;
        }
        Rebuild();
    }

    private void FillMetadata()
    {
        _metaHost.Children.Clear();
        if (_metaLoading || (_report is null && _metaError is null))
        {
            _metaHost.Children.Add(Note(Loc.T("info.metaLoading")));
            return;
        }
        if (_metaError is not null)
        {
            _metaHost.Children.Add(Note(_metaError));
            return;
        }
        // file times are in the "File" card already; empty (editable) fields are not shown in this read-only list
        var groups = _report!.Groups
            .Select(g => (Group: g, Entries: g.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Value) && (g.Id != MetadataGroupId.File || e.Key is not ("file.Created" or "file.Modified" or "file.Accessed"))).ToList()))
            .Where(x => x.Entries.Count > 0)
            .ToList();
        if (groups.Count == 0)
        {
            _metaHost.Children.Add(Note(Loc.T("info.metaNone")));
            return;
        }
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var (group, entries) = groups[gi];
            var title = Text(GroupLabel(group), 12, "Brush.AccentText", FontWeights.SemiBold);
            title.Margin = new Thickness(0, gi == 0 ? 12 : 18, 0, 2);
            _metaHost.Children.Add(title);
            for (var i = 0; i < entries.Count; i++)
                Row(_metaHost, LabelOf(entries[i]), FormatValue(entries[i]), selectable: true, last: gi == groups.Count - 1 && i == entries.Count - 1);
        }
    }

    private static TextBlock Note(string text)
    {
        var t = Text(text, 12.5, "Brush.TextFaint");
        t.Margin = new Thickness(0, 12, 0, 12);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    private async Task StripAsync()
    {
        if (MainWindow.Of(this) is not { } w || !File.Exists(Path)) return;
        var answer = await w.DialogLayer.Show(
            Loc.T("info.stripTitle"), Loc.T("info.stripText", _doc.Title),
            [
                new DialogButton("cancel", Loc.T("info.cancel"), DialogButtonKind.Ghost, IsCancel: true),
                new DialogButton("strip", Loc.T("info.stripConfirm"), DialogButtonKind.Danger, IsDefault: true),
            ],
            glyph: Glyphs.Delete);
        if (answer != "strip") return;
        try
        {
            var result = await MetadataWriter.StripAsync(Path);
            var count = result.Removed.Count + (result.ZoneRemoved && !result.Removed.Contains("file.Zone") ? 1 : 0);
            if (count == 0 && !result.ContentChanged) w.Toasts.Show(Loc.T("info.strippedNothing"), ToastKind.Info);
            else w.Toasts.Show(Loc.Plural("info.stripped", Math.Max(1, count)), ToastKind.Success);
            Stripped?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"info: strip {Path}: {ex.Message}");
            w.Toasts.Show(Loc.T("info.stripFailed", ex.Message), ToastKind.Error);
        }
        _hashes = null; // the content may have changed
        _report = null;
        await LoadMetadataAsync();
    }

    // ───────────── IDocumentView ─────────────

    public void OnActivated() { }

    public void OnDeactivated() { }

    public void CaptureState() { }

    public void FocusContent() => Focus();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
