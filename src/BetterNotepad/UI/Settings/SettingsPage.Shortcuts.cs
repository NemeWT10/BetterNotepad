using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.UI;

/// <summary>
/// Shortcuts page: every command grouped by category, searchable; click a shortcut to record a new one.
/// Ctrl+Alt+letter (AltGr on Polish keyboards) is refused with a shake; conflicts offer replace / keep both.
/// Also the recorder for the global hotkeys on the System page.
/// </summary>
public sealed partial class SettingsPage
{
    private const string UndoGlyph = "\uE7A7";

    private readonly List<ShortcutRow> _shortcutRows = new();
    private readonly List<(TextBlock Title, SettingsCard Card)> _shortcutGroups = new();
    private readonly Dictionary<string, Action> _hotkeyRecorders = new(StringComparer.Ordinal);
    private TextBox? _shortcutSearch;
    private FrameworkElement? _shortcutEmpty;
    private TextBlock? _modifiedLabel;
    private Button? _resetAllButton;
    private string _shortcutQuery = "";
    private Capture? _capture;
    private PendingConflict? _pending;

    /// <summary>What the recorder does with the next chord.</summary>
    private sealed record Capture(ChordBox Box, Action<KeyChord> Offer, Action Clear, Action Cancel);

    private sealed record PendingConflict(ShortcutRow Row, KeyChord Chord, List<AppCommand> Others);

    private sealed class ShortcutRow
    {
        public required AppCommand Command { get; init; }
        public required SettingRow Row { get; init; }
        public required ChordBox Box { get; init; }
        public required Button Reset { get; init; }
        public required Icon Conflict { get; init; }
        public required FrameworkElement Modified { get; init; }
        public string Haystack { get; set; } = "";
        public KeyChord? Chord { get; set; }
    }

    // ───────────── page ─────────────

    private void BuildShortcuts(StackPanel panel)
    {
        // search + modified count + reset all
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var search = new Grid();
        _shortcutSearch = new TextBox { Height = 36, Padding = new Thickness(34, 0, 10, 0), Text = _shortcutQuery };
        _shortcutSearch.SetResourceReference(StyleProperty, "Field");
        AutomationProperties.SetAutomationId(_shortcutSearch, "shortcuts.search");
        AutomationProperties.SetName(_shortcutSearch, Loc.T("settings.keys.search"));
        var glass = SettingsUi.Glyph(Glyphs.Search, 13, "Brush.TextFaint");
        glass.HorizontalAlignment = HorizontalAlignment.Left;
        glass.Margin = new Thickness(13, 0, 0, 0);
        var placeholder = SettingsUi.Label(Loc.T("settings.keys.search"), 13, "Brush.TextFaint");
        placeholder.Margin = new Thickness(35, 0, 0, 0);
        placeholder.IsHitTestVisible = false;
        placeholder.Visibility = _shortcutQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _shortcutSearch.TextChanged += (_, _) =>
        {
            _shortcutQuery = _shortcutSearch.Text;
            placeholder.Visibility = _shortcutQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyShortcutFilter();
        };
        search.Children.Add(_shortcutSearch);
        search.Children.Add(glass);
        search.Children.Add(placeholder);
        bar.Children.Add(search);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _modifiedLabel = SettingsUi.Label("", 12, "Brush.TextDim");
        _modifiedLabel.Margin = new Thickness(0, 0, 12, 0);
        right.Children.Add(_modifiedLabel);
        _resetAllButton = SettingsUi.Button(Loc.T("settings.keys.resetAll"), (_, _) => AskResetAllShortcuts(), glyph: UndoGlyph, id: "shortcuts.resetAll");
        right.Children.Add(_resetAllButton);
        Grid.SetColumn(right, 1);
        bar.Children.Add(right);
        panel.Children.Add(bar);

        var note = SettingsUi.Notice("info", Loc.T("settings.keys.altGrNote"));
        note.Margin = new Thickness(0, 0, 0, 22);
        panel.Children.Add(note);

        // groups in a reading order; categories of modules (unknown here) go before General / Application
        foreach (var category in CommandRegistry.All.GroupBy(c => c.CategoryKey).OrderBy(g => CategoryRank(g.Key)))
        {
            var title = new TextBlock { Text = Loc.T(category.Key) };
            title.SetResourceReference(StyleProperty, "SectionTitle");
            if (_shortcutGroups.Count > 0) title.Margin = new Thickness(4, 10, 0, 8);
            panel.Children.Add(title);
            var card = new SettingsCard();
            panel.Children.Add(card);
            foreach (var cmd in category) card.Add(MakeShortcutRow(cmd).Row);
            _shortcutGroups.Add((title, card));
        }

        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0), Visibility = Visibility.Collapsed };
        var emptyIcon = SettingsUi.Glyph(Glyphs.Search, 26, "Brush.TextFaint");
        emptyIcon.Margin = new Thickness(0, 0, 0, 10);
        empty.Children.Add(emptyIcon);
        empty.Children.Add(SettingsUi.Label("", 13, "Brush.TextDim"));
        _shortcutEmpty = empty;
        panel.Children.Add(empty);

        _refresh.Add(RefreshShortcutRows);
        RefreshShortcutRows();
        ApplyShortcutFilter();
    }

    private static readonly string[] CategoryOrder =
        ["cmd.cat.file", "cmd.cat.edit", "cmd.cat.view", "cmd.cat.go", "cmd.cat.tabs", "cmd.cat.tools", "cmd.cat.general", "cmd.cat.app"];

    private static int CategoryRank(string key)
    {
        var i = Array.IndexOf(CategoryOrder, key);
        return i >= 0 ? i * 2 : Array.IndexOf(CategoryOrder, "cmd.cat.general") * 2 - 1;
    }

    private ShortcutRow MakeShortcutRow(AppCommand cmd)
    {
        var box = new ChordBox("shortcuts.chord." + cmd.Id);
        var conflict = SettingsUi.Glyph(Glyphs.Warning, 13, "Brush.Warning");
        conflict.Margin = new Thickness(0, 0, 6, 0);
        conflict.IsHitTestVisible = true;
        conflict.Visibility = Visibility.Collapsed;
        var edit = SettingsUi.IconButton(Glyphs.Edit, Loc.T("settings.keys.change"), (_, _) => { }, 12, "shortcuts.edit." + cmd.Id);
        edit.Margin = new Thickness(4, 0, 0, 0);
        var reset = SettingsUi.IconButton(UndoGlyph, "", (_, _) => { }, 12, "shortcuts.reset." + cmd.Id);
        reset.Visibility = Visibility.Hidden;
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(conflict);
        controls.Children.Add(box);
        controls.Children.Add(edit);
        controls.Children.Add(reset);

        var row = new SettingRow(null, cmd.Title, null, controls, vpad: 6, minHeight: 32);
        row.TitleBlock.ToolTip = cmd.Id;
        ToolTipService.SetInitialShowDelay(row.TitleBlock, 900);

        // title line: name, "changed" dot, editor / file-kind tags
        var line = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Texts.Children.Remove(row.TitleBlock);
        row.TitleBlock.Margin = new Thickness(0, 0, 8, 0);
        line.Children.Add(row.TitleBlock);
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(-2, 1, 8, 0), Visibility = Visibility.Collapsed, ToolTip = Loc.T("settings.keys.changedTip") };
        dot.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        line.Children.Add(dot);
        if (cmd.Scope == CommandScope.Editor) line.Children.Add(TagChip(Loc.T("settings.keys.scopeEditor")));
        foreach (var kind in cmd.Kinds ?? []) line.Children.Add(TagChip(Loc.T("settings.kind." + kind.ToString().ToLowerInvariant())));
        row.Texts.Children.Insert(0, line);

        var sr = new ShortcutRow { Command = cmd, Row = row, Box = box, Reset = reset, Conflict = conflict, Modified = dot };
        box.Activated += () => BeginCapture(sr);
        edit.Click += (_, _) => BeginCapture(sr);
        reset.Click += (_, _) =>
        {
            EndCapture();
            ApplyCommandChord(sr, KeyChord.Parse(cmd.DefaultGesture));
        };
        _shortcutRows.Add(sr);
        return sr;
    }

    private static Border TagChip(string text)
    {
        var tag = SettingsUi.Chip(text, "Brush.SurfaceHover", "Brush.TextDim", size: 10.5);
        tag.Margin = new Thickness(0, 1, 6, 1);
        return tag;
    }

    private void RefreshShortcutRows()
    {
        if (_shortcutRows.Count == 0) return;
        Keymap.Invalidate(); // settings change notifications arrive before Keymap drops its cache
        var overrides = Section<KeymapSettings>().Overrides;
        foreach (var r in _shortcutRows)
        {
            var chord = Keymap.GestureFor(r.Command.Id);
            r.Chord = chord;
            var modified = overrides.ContainsKey(r.Command.Id);
            if (!r.Box.IsCapturing && _pending?.Row != r) r.Box.ShowChord(chord, Loc.T("settings.keys.unassigned"));
            r.Modified.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
            r.Reset.Visibility = modified ? Visibility.Visible : Visibility.Hidden;
            r.Reset.ToolTip = Loc.T("settings.keys.resetOne", KeyChord.Parse(r.Command.DefaultGesture)?.ToString() ?? Loc.T("settings.keys.unassigned"));
            var others = chord is { } c ? Keymap.Conflicts(r.Command.Id, c).ToList() : [];
            r.Conflict.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            r.Conflict.ToolTip = others.Count > 0 ? Loc.T("settings.keys.sharedWith", string.Join(", ", others.Select(o => o.Title))) : null;
            r.Haystack = $"{r.Command.Title} {r.Command.Category} {r.Command.Id} {r.Command.SearchTerms}".ToLowerInvariant();
        }
        var count = overrides.Keys.Count(id => CommandRegistry.Get(id) is not null);
        if (_modifiedLabel is not null)
        {
            _modifiedLabel.Text = count > 0 ? Loc.Plural("settings.keys.modified", count) : "";
            _modifiedLabel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_resetAllButton is not null) _resetAllButton.IsEnabled = count > 0;
    }

    private void ApplyShortcutFilter()
    {
        var q = _shortcutQuery.Trim().ToLowerInvariant();
        var asChord = q.Contains('+') ? KeyChord.Parse(q) : null;
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var any = false;
        foreach (var r in _shortcutRows)
        {
            var match = asChord is { } c
                ? r.Chord == c
                : tokens.All(t => r.Haystack.Contains(t, StringComparison.Ordinal) || (r.Chord?.ToString().ToLowerInvariant().Contains(t, StringComparison.Ordinal) ?? false));
            r.Row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            any |= match;
        }
        foreach (var (title, card) in _shortcutGroups)
        {
            card.UpdateSeparators();
            var visible = card.AnyVisible ? Visibility.Visible : Visibility.Collapsed;
            title.Visibility = visible;
            card.Visibility = visible;
        }
        if (_shortcutEmpty is StackPanel empty)
        {
            empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)empty.Children[1]).Text = Loc.T("settings.keys.noResults", _shortcutQuery.Trim());
        }
    }

    // ───────────── recording a command shortcut ─────────────

    private void BeginCapture(ShortcutRow row)
    {
        EndCapture();
        CancelPendingConflict();
        LastWarning = null;
        row.Box.BeginCapture(Loc.T("settings.keys.press"));
        row.Row.SetNotice(SettingsUi.Notice("accent", Loc.T("settings.keys.captureHint")));
        _capture = new Capture(row.Box,
            chord => OfferForCommand(row, chord),
            () =>
            {
                _capture = null;
                ApplyCommandChord(row, null);
            },
            () =>
            {
                row.Row.SetNotice(null);
                row.Box.ShowChord(row.Chord, Loc.T("settings.keys.unassigned"));
            });
        if (!IsKeyboardFocusWithin) Focus();
    }

    private void OfferForCommand(ShortcutRow row, KeyChord chord)
    {
        if (ValidateInApp(chord) is { } problem)
        {
            Refuse(row.Row, row.Box, problem);
            return;
        }
        _capture = null;
        if (Keymap.GestureFor(row.Command.Id) == chord)
        {
            row.Row.SetNotice(null);
            row.Box.ShowChord(chord, "");
            return;
        }
        var others = Keymap.Conflicts(row.Command.Id, chord).ToList();
        if (others.Count > 0)
        {
            ShowConflict(row, chord, others);
            return;
        }
        ApplyCommandChord(row, chord);
    }

    private void Refuse(SettingRow row, ChordBox box, string problem)
    {
        LastWarning = problem;
        box.Shake();
        row.SetNotice(SettingsUi.Notice("warning", problem));
    }

    private void ShowConflict(ShortcutRow row, KeyChord chord, List<AppCommand> others)
    {
        _pending = new PendingConflict(row, chord, others);
        row.Box.ShowChord(chord, "", warn: true);
        var names = string.Join(", ", others.Select(o => $"„{o.Title}”"));
        row.Row.SetNotice(SettingsUi.Notice("warning", Loc.T("settings.keys.conflict", chord, names),
            SettingsUi.SmallButton(Loc.T("settings.keys.replace"), (_, _) => ResolveConflict("replace"), "PrimaryButton", "shortcuts.conflict.replace"),
            SettingsUi.SmallButton(Loc.T("settings.keys.keepBoth"), (_, _) => ResolveConflict("keep"), id: "shortcuts.conflict.keep"),
            SettingsUi.SmallButton(Loc.T("common.cancel"), (_, _) => ResolveConflict("cancel"), "GhostButton", "shortcuts.conflict.cancel")));
        row.Box.Shake();
    }

    /// <summary>"replace" unbinds the other commands, "keep" binds both, "cancel" drops the new chord.</summary>
    internal void ResolveConflict(string choice)
    {
        if (_pending is not { } p) return;
        _pending = null;
        switch (choice)
        {
            case "replace":
                foreach (var o in p.Others) Keymap.SetGesture(o.Id, null);
                ApplyCommandChord(p.Row, p.Chord);
                break;
            case "keep":
                ApplyCommandChord(p.Row, p.Chord);
                break;
            default:
                p.Row.Row.SetNotice(null);
                p.Row.Box.ShowChord(p.Row.Chord, Loc.T("settings.keys.unassigned"));
                break;
        }
    }

    private void CancelPendingConflict()
    {
        if (_pending is not null) ResolveConflict("cancel");
    }

    private void ApplyCommandChord(ShortcutRow row, KeyChord? chord)
    {
        Keymap.SetGesture(row.Command.Id, chord);
        RefreshShortcutRows();
        row.Row.SetNotice(null);
        row.Box.ShowChord(Keymap.GestureFor(row.Command.Id), Loc.T("settings.keys.unassigned"));
        Motion.Bounce(row.Box, 1.08);
        RefreshAllChrome();
    }

    private async void AskResetAllShortcuts()
    {
        var answer = await _window.DialogLayer.Show(Loc.T("settings.keys.resetAllTitle"), Loc.T("settings.keys.resetAllText"),
            [
                new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
                new DialogButton("reset", Loc.T("settings.keys.resetAllConfirm"), DialogButtonKind.Primary, IsDefault: true),
            ], glyph: UndoGlyph);
        if (answer == "reset") ResetAllShortcuts();
    }

    internal void ResetAllShortcuts()
    {
        EndCapture();
        CancelPendingConflict();
        Keymap.ResetAll();
        RefreshShortcutRows();
        RefreshAllChrome();
        _window.Toasts.Show(Loc.T("settings.keys.resetDone"), ToastKind.Success);
    }

    // ───────────── recording a global hotkey (System page) ─────────────

    private SettingRow HotkeyRow(SettingsCard card, string id, string? glyph, string title, string description,
        Func<string> get, Action<string> set, string defaultChord, Func<string?> other)
    {
        var box = new ChordBox(id);
        var edit = SettingsUi.IconButton(Glyphs.Edit, Loc.T("settings.keys.change"), (_, _) => { }, 12, id + ".edit");
        edit.Margin = new Thickness(4, 0, 0, 0);
        var reset = SettingsUi.IconButton(UndoGlyph, Loc.T("settings.keys.resetOne", defaultChord), (_, _) => { }, 12, id + ".reset");
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(box);
        controls.Children.Add(edit);
        controls.Children.Add(reset);
        var row = card.Add(new SettingRow(glyph, title, description, controls, indent: glyph is null));

        void Show()
        {
            if (box.IsCapturing) return;
            box.ShowChord(KeyChord.Parse(get()), Loc.T("settings.keys.unassigned"));
            reset.Visibility = string.Equals(get(), defaultChord, StringComparison.OrdinalIgnoreCase) ? Visibility.Hidden : Visibility.Visible;
        }

        void Begin()
        {
            EndCapture();
            CancelPendingConflict();
            LastWarning = null;
            box.BeginCapture(Loc.T("settings.keys.press"));
            row.SetNotice(SettingsUi.Notice("accent", Loc.T("settings.keys.captureHintGlobal")));
            _capture = new Capture(box,
                chord =>
                {
                    var problem = ValidateGlobal(chord)
                        ?? (KeyChord.Parse(other()) == chord ? Loc.T("settings.keys.globalTaken", chord) : null);
                    if (problem is not null)
                    {
                        Refuse(row, box, problem);
                        return;
                    }
                    _capture = null;
                    set(chord.ToString());
                    row.SetNotice(null);
                    box.ShowChord(chord, "");
                    Show();
                    Motion.Bounce(box, 1.08);
                },
                () =>
                {
                    _capture = null;
                    set("");
                    row.SetNotice(null);
                    box.ShowChord(null, "");
                    Show();
                },
                () =>
                {
                    row.SetNotice(null);
                    box.ShowChord(null, "");
                    Show();
                });
            if (!IsKeyboardFocusWithin) Focus();
        }

        box.Activated += Begin;
        edit.Click += (_, _) => Begin();
        reset.Click += (_, _) =>
        {
            EndCapture();
            set(defaultChord);
            Show();
            Motion.Bounce(box, 1.08);
        };
        _hotkeyRecorders[id] = Begin;
        Bind(Show);
        return row;
    }

    // ───────────── rules ─────────────

    /// <summary>Why a chord can't be an in-app shortcut (null = fine).</summary>
    internal static string? ValidateInApp(KeyChord c)
    {
        if (c.IsAltGrConflict) return Loc.T("settings.keys.altGr", c);
        if (c.Modifiers.HasFlag(ModifierKeys.Windows)) return Loc.T("settings.keys.winReserved");
        var fKey = c.Key is >= Key.F1 and <= Key.F24;
        if ((c.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 && !fKey) return Loc.T("settings.keys.needsModifier");
        return null;
    }

    /// <summary>Why a chord can't be a global hotkey (null = fine).</summary>
    internal static string? ValidateGlobal(KeyChord c)
    {
        if (c.IsAltGrConflict) return Loc.T("settings.keys.altGr", c);
        if (c is { Modifiers: ModifierKeys.Control | ModifierKeys.Shift, Key: Key.N }) return Loc.T("settings.keys.globalBrowser");
        var count = new[] { ModifierKeys.Control, ModifierKeys.Shift, ModifierKeys.Alt, ModifierKeys.Windows }.Count(m => c.Modifiers.HasFlag(m));
        if (!c.Modifiers.HasFlag(ModifierKeys.Windows) && count < 2) return Loc.T("settings.keys.globalWeak");
        return null;
    }

    // ───────────── key routing while recording ─────────────

    private void HandleCaptureKey(KeyEventArgs e, KeyChord chord)
    {
        e.Handled = true;
        if (_capture is not { } capture) return;
        if (chord.IsModifierOnly)
        {
            capture.Box.ShowLive(Keyboard.Modifiers);
            return;
        }
        if (chord.Modifiers == ModifierKeys.None && chord.Key == Key.Escape)
        {
            EndCapture();
            return;
        }
        if (chord.Modifiers == ModifierKeys.None && chord.Key == Key.Back)
        {
            capture.Clear();
            return;
        }
        capture.Offer(chord);
    }

    private void OnCapturePreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_capture is not { } capture) return;
        e.Handled = true; // e.g. a lone Alt must not open the window menu while recording
        capture.Box.ShowLive(Keyboard.Modifiers);
    }

    /// <summary>Stops recording without changing anything.</summary>
    private void EndCapture()
    {
        if (_capture is not { } c) return;
        _capture = null;
        c.Cancel();
    }

    // ───────────── test / automation hooks ─────────────

    internal bool IsCapturing => _capture is not null;

    internal bool HasPendingConflict => _pending is not null;

    /// <summary>The last refusal shown while recording (null after a successful recording starts).</summary>
    internal string? LastWarning { get; private set; }

    internal void BeginShortcutCapture(string commandId)
    {
        if (_shortcutRows.FirstOrDefault(r => r.Command.Id == commandId) is { } row) BeginCapture(row);
    }

    internal void BeginHotkeyCapture(string id)
    {
        if (_hotkeyRecorders.TryGetValue(id, out var begin)) begin();
    }

    internal void OfferChord(KeyChord chord) => _capture?.Offer(chord);

    internal void ClearCaptured() => _capture?.Clear();

    internal void CancelCapture() => EndCapture();

    internal void SetShortcutSearch(string text)
    {
        if (_shortcutSearch is not null) _shortcutSearch.Text = text;
    }

    internal int VisibleShortcutRows => _shortcutRows.Count(r => r.Row.Visibility == Visibility.Visible);

    internal int ShortcutRowCount => _shortcutRows.Count;
}
