using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Files;
using BetterNotepad.Documents.Session;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Themes;

namespace BetterNotepad.UI.Windows;

/// <summary>Title-bar menu (File, Edit, View, Tools), tab context menu and the status-bar pop-up menus.</summary>
public partial class MainWindow
{
    /// <summary>What a menu item needs refreshed each time its menu opens.</summary>
    private sealed record MenuTag(string? CommandId = null, Func<bool>? Checked = null);

    private static AppearanceSettings Appearance => SettingsService.I.Get<AppearanceSettings>();

    // ───────────── title-bar menu ─────────────

    /// <summary>Rebuilds the menu and the shortcut hint of the palette pill (language or keymap changed).</summary>
    private void BuildMenu()
    {
        TopMenu.Items.Clear();
        TopMenu.Items.Add(TopItem("menu.file", FileMenu()));
        TopMenu.Items.Add(TopItem("menu.edit", EditMenu()));
        TopMenu.Items.Add(TopItem("menu.view", ViewMenu()));
        TopMenu.Items.Add(TopItem("menu.tools", ToolsMenu()));

        // the pill's shortcut text lives inside its template
        PalettePill.ApplyTemplate();
        if (PalettePill.Template.FindName("PillKeys", PalettePill) is TextBlock keys)
            keys.Text = Keymap.GestureText("palette.files");
        _explorer?.RefreshTexts(); // its tooltips and key chips name the toggle's shortcut
    }

    /// <summary>Called by settings after shortcuts changed.</summary>
    public void RefreshChrome() => BuildMenu();

    private MenuItem TopItem(string titleKey, IEnumerable<Control> items)
    {
        var top = new MenuItem { Header = Loc.T(titleKey) };
        foreach (var i in items) top.Items.Add(i);
        top.SubmenuOpened += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, top)) RefreshMenuState(top);
        };
        return top;
    }

    /// <summary>Enabled / checked state of command items, evaluated when the menu opens.</summary>
    private void RefreshMenuState(ItemsControl parent)
    {
        var ctx = PaletteContext();
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (item.Tag is MenuTag tag)
            {
                if (tag.CommandId is not null && CommandRegistry.Get(tag.CommandId) is { } cmd)
                    item.IsEnabled = cmd.CanExecute?.Invoke(ctx) ?? true;
                if (tag.Checked is not null) item.IsChecked = tag.Checked();
            }
            if (item.HasItems) RefreshMenuState(item);
        }
    }

    private MenuItem Cmd(string id, string? glyph = null, string? gesture = null, Func<bool>? isChecked = null)
    {
        var cmd = CommandRegistry.Get(id);
        var item = new MenuItem
        {
            Header = cmd?.Title ?? id,
            InputGestureText = gesture ?? Keymap.GestureText(id),
            Tag = new MenuTag(id, isChecked),
        };
        if (glyph is not null) item.Icon = new Icon { Glyph = glyph, FontSize = glyph == Glyphs.Close ? 10 : 13 }; // the X glyph fills its box
        item.Click += (_, _) => RunCommand(id);
        return item;
    }

    private static MenuItem Item(string header, string? glyph, Action click, bool enabled = true, string? gesture = null, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "", IsChecked = isChecked };
        if (glyph is not null) item.Icon = new Icon { Glyph = glyph, FontSize = glyph == Glyphs.Close ? 10 : 13 }; // the X glyph fills its box
        item.Click += (_, _) => click();
        return item;
    }

    private static MenuItem Choice(string header, Func<bool> isChecked, Action apply)
    {
        var item = new MenuItem { Header = header, Tag = new MenuTag(Checked: isChecked) };
        item.Click += (_, _) => apply();
        return item;
    }

    private IEnumerable<Control> FileMenu()
    {
        yield return Cmd("file.new", Glyphs.Add);
        yield return Cmd("file.open", Glyphs.OpenFolder);
        yield return Cmd("file.openFolder", Modules.Explorer.ExplorerLook.OpenFolderGlyph);
        yield return RecentMenu();
        yield return new Separator();
        yield return Cmd("file.save", Glyphs.Save);
        yield return Cmd("file.saveAs", Glyphs.SaveAs);
        yield return Cmd("file.saveAll", Glyphs.SaveAll);
        yield return new Separator();
        yield return Cmd("file.rename", Glyphs.Rename);
        yield return Cmd("file.toggleLock", Glyphs.Lock);
        yield return Cmd("file.reveal", Glyphs.Folder);
        yield return new Separator();
        yield return Cmd("file.close", Glyphs.Close);
        yield return Cmd("file.closeAll", Glyphs.CloseAll);
        yield return Cmd("file.reopenClosed", Glyphs.Undo);
        yield return Cmd("file.closeFolder", Modules.Explorer.ExplorerLook.CloseFolderGlyph);
        yield return new Separator();
        yield return Cmd("app.settings", Glyphs.Settings);
        yield return Cmd("app.quit", Glyphs.Power);
    }

    private IEnumerable<Control> EditMenu()
    {
        // every item of a menu has an icon, or none does (Tools: none)
        yield return Cmd("edit.undo", Glyphs.Undo, "Ctrl+Z");
        yield return Cmd("edit.redo", Glyphs.Redo, "Ctrl+Y");
        yield return new Separator();
        yield return Cmd("edit.cut", Glyphs.Cut, "Ctrl+X");
        yield return Cmd("edit.copy", Glyphs.Copy, "Ctrl+C");
        yield return Cmd("edit.paste", Glyphs.Paste, "Ctrl+V");
        yield return Cmd("edit.selectAll", Glyphs.Selection, "Ctrl+A");
        yield return Cmd("editor.copyFormatted", Glyphs.Copy);
        yield return Cmd("editor.copyAsListing", Glyphs.Code);
        yield return Cmd("editor.copyAsImage", Glyphs.Image);
        yield return new Separator();
        yield return Cmd("editor.find", Glyphs.Search);
        yield return Cmd("editor.replace", Glyphs.Replace);
        yield return Cmd("editor.findNext", Glyphs.ArrowDown);
        yield return Cmd("editor.findPrev", Glyphs.ArrowUp);
        yield return Cmd("editor.goToLine", Glyphs.GoTo);
        yield return new Separator();

        var lines = new MenuItem { Header = Loc.T("menu.lines"), Icon = new Icon { Glyph = Glyphs.List, FontSize = 13 } };
        foreach (var id in new[]
        {
            "editor.moveLineUp", "editor.moveLineDown", "editor.copyLineUp", "editor.copyLineDown",
            "editor.deleteLine", "editor.insertLineBelow", "editor.insertLineAbove", "editor.selectLine",
        })
            lines.Items.Add(Cmd(id));
        yield return lines;
        yield return Cmd("editor.toggleComment", Glyphs.Comment);
        yield return Cmd("editor.blockComment", Glyphs.Code);
        yield return Cmd("editor.indent", Glyphs.Indent);
        yield return Cmd("editor.outdent", Glyphs.Outdent);
    }

    private IEnumerable<Control> ViewMenu()
    {
        yield return Cmd("view.toggleExplorer", Modules.Explorer.ExplorerLook.PanelGlyph, isChecked: () => ExplorerShown);
        yield return new Separator();
        yield return Cmd("view.splitRight", Glyphs.SplitRight);
        yield return Cmd("view.splitDown", Glyphs.SplitDown);
        yield return Cmd("view.unsplit", Glyphs.Merge);
        yield return Cmd("view.focusOtherGroup", Glyphs.Switch);
        yield return Cmd("view.moveToNewWindow", Glyphs.NewWindow);
        yield return Cmd("view.pinOnTop", Glyphs.Pinned);
        yield return Cmd("view.toggleGhost", Glyphs.Ghost);
        yield return new Separator();
        yield return Cmd("view.wordWrap", Glyphs.WordWrap, isChecked: () => AppCommands.IsWordWrapOn(ActiveDocument as TextDocument));
        yield return Cmd("view.whitespace", Glyphs.Whitespace, isChecked: () => SettingsService.I.Get<EditorSettings>().ShowWhitespace);
        yield return Cmd("view.lineNumbers", Glyphs.LineNumbers, isChecked: () => AppCommands.IsLineNumbersOn(ActiveDocument as TextDocument));
        yield return Cmd("view.cyclePreview", Glyphs.View);
        yield return new Separator();
        yield return Cmd("view.zoomIn", Glyphs.ZoomIn);
        yield return Cmd("view.zoomOut", Glyphs.ZoomOut);
        yield return Cmd("view.zoomReset", Glyphs.Zoom);
        yield return new Separator();
        yield return ThemeMenu();
        yield return LanguageMenu();
    }

    private IEnumerable<Control> ToolsMenu()
    {
        yield return Cmd("tools.formatDocument");
        yield return Cmd("tools.minify");
        yield return new Separator();
        yield return Cmd("tools.sortAsc");
        yield return Cmd("tools.sortDesc");
        yield return Cmd("tools.uniqueLines");
        yield return Cmd("tools.trimTrailing");
        yield return new Separator();
        yield return Cmd("tools.upper");
        yield return Cmd("tools.lower");
        yield return Cmd("tools.title");
        yield return new Separator();
        yield return Cmd("tools.base64Encode");
        yield return Cmd("tools.base64Decode");
        yield return Cmd("tools.urlEncode");
        yield return Cmd("tools.urlDecode");
        yield return new Separator();
        yield return Cmd("file.compareWithDisk");
        yield return Cmd("file.compareTabs");
        yield return new Separator();
        yield return Cmd("palette.commands");
        yield return Cmd("palette.files");
        yield return Cmd("palette.symbols");
        yield return Cmd("editor.changeLanguage");
    }

    private MenuItem ThemeMenu()
    {
        var menu = new MenuItem { Header = Loc.T("menu.theme"), Icon = new Icon { Glyph = Glyphs.Color, FontSize = 13 } };
        menu.Items.Add(Choice(Loc.T("menu.themeSystem"), () => Appearance.Theme == "system", () => Appearance.Theme = "system"));
        foreach (var p in Palettes.All)
        {
            var id = p.Id;
            menu.Items.Add(Choice(Loc.T(p.NameKey), () => Appearance.Theme == id, () => Appearance.Theme = id));
        }
        return menu;
    }

    private MenuItem LanguageMenu()
    {
        var menu = new MenuItem { Header = Loc.T("menu.language"), Icon = new Icon { Glyph = Glyphs.Globe, FontSize = 13 } };
        menu.Items.Add(Choice(Loc.T("menu.languageSystem"), () => Appearance.Language == "system", () => Appearance.Language = "system"));
        menu.Items.Add(Choice(Loc.T("menu.languagePl"), () => Appearance.Language == "pl", () => Appearance.Language = "pl"));
        menu.Items.Add(Choice(Loc.T("menu.languageEn"), () => Appearance.Language == "en", () => Appearance.Language = "en"));
        return menu;
    }

    private MenuItem RecentMenu()
    {
        var menu = new MenuItem { Header = Loc.T("menu.recent"), Icon = new Icon { Glyph = Glyphs.History, FontSize = 13 } };
        FillRecent(menu);
        menu.SubmenuOpened += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, menu)) FillRecent(menu);
        };
        return menu;
    }

    private void FillRecent(MenuItem menu)
    {
        menu.Items.Clear();
        var files = RecentFiles.I.Items.Take(15).ToList();
        if (files.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Loc.T("menu.recentEmpty"), IsEnabled = false });
            return;
        }
        foreach (var p in files)
        {
            var path = p;
            // a TextBlock header: file names may contain '_' (an access-key marker in plain headers)
            var item = new MenuItem
            {
                Header = new TextBlock { Text = Path.GetFileName(path) },
                InputGestureText = ShortDirectory(path),
                ToolTip = path,
            };
            item.Click += (_, _) => OpenFile(path);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("menu.recentClear"), null, () => RecentFiles.I.Clear()));
    }

    private static string ShortDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        return dir.Length <= 32 ? dir : "…" + dir[^31..];
    }

    // ───────────── tab context menu ─────────────

    /// <summary>Right-click in a text editor: clipboard, rich copy for documents, select all.</summary>
    public void FillEditorMenu(BetterNotepad.Editor.CodeEditor editor, ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Cmd("edit.cut", Glyphs.Cut, "Ctrl+X"));
        menu.Items.Add(Cmd("edit.copy", Glyphs.Copy, "Ctrl+C"));
        menu.Items.Add(Cmd("edit.paste", Glyphs.Paste, "Ctrl+V"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("editor.copyFormatted", Glyphs.Copy));
        menu.Items.Add(Cmd("editor.copyAsListing", Glyphs.Code));
        menu.Items.Add(Cmd("editor.copyAsImage", Glyphs.Image));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("edit.selectAll", Glyphs.Selection, "Ctrl+A"));
        RefreshMenuState(menu);
    }

    public ContextMenu BuildTabMenu(TabGroup group, Document doc)
    {
        var menu = new ContextMenu();
        var text = doc as TextDocument;
        var hasPath = doc.FilePath is not null;
        var others = group.Tabs.Count > 1;

        menu.Items.Add(Item(Loc.T("tabmenu.close"), Glyphs.Close, () => Fire(CloseTabAsync(group, doc)), gesture: Keymap.GestureText("file.close")));
        menu.Items.Add(Item(Loc.T("tabmenu.closeOthers"), Glyphs.CloseAll, () => Fire(CloseOthersAsync(group, doc)), enabled: others));
        menu.Items.Add(Item(Loc.T("tabmenu.closeRight"), Glyphs.CloseRight, () => Fire(CloseToRightAsync(group, doc)), enabled: group.Tabs.IndexOf(doc) < group.Tabs.Count - 1));
        menu.Items.Add(Item(Loc.T("tabmenu.closeSaved"), Glyphs.Save, () => Fire(CloseSavedAsync(group)), enabled: group.Tabs.Any(t => !t.IsDirty)));
        menu.Items.Add(Item(Loc.T("tabmenu.closeAll"), Glyphs.CloseThin, () => Fire(CloseAllAsync(group))));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item(Loc.T(doc.IsPinned ? "tabmenu.unpin" : "tabmenu.pin"), doc.IsPinned ? Glyphs.Unpin : Glyphs.Pin, () =>
        {
            doc.IsPinned = !doc.IsPinned;
            group.Normalize(doc);
        }));
        menu.Items.Add(Item(Loc.T(doc.IsLocked ? "tabmenu.unlock" : "tabmenu.lock"), doc.IsLocked ? Glyphs.Unlock : Glyphs.Lock, () => SetLocked(doc, !doc.IsLocked)));
        menu.Items.Add(Item(Loc.T("tabmenu.rename"), Glyphs.Rename, () => BeginRename(doc), gesture: Keymap.GestureText("file.rename")));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item(Loc.T("tabmenu.copyPath"), Glyphs.Copy, () => CopyPath(doc, nameOnly: false), enabled: hasPath));
        menu.Items.Add(Item(Loc.T("tabmenu.copyName"), Glyphs.Text, () => CopyPath(doc, nameOnly: true)));
        menu.Items.Add(Item(Loc.T("tabmenu.reveal"), Glyphs.Folder, () => RevealInExplorer(doc), enabled: hasPath && File.Exists(doc.FilePath)));
        menu.Items.Add(new Separator());

        if (Model.Groups.Count < 2)
        {
            menu.Items.Add(Item(Loc.T("tabmenu.splitRight"), Glyphs.SplitRight, () => Split(Orientation.Horizontal), gesture: Keymap.GestureText("view.splitRight")));
            menu.Items.Add(Item(Loc.T("tabmenu.splitDown"), Glyphs.SplitDown, () => Split(Orientation.Vertical)));
        }
        else
        {
            var other = Model.Groups.First(g => g != group);
            menu.Items.Add(Item(Loc.T("tabmenu.moveToOtherGroup"), Glyphs.Switch, () => MoveTab(group, other, doc)));
            menu.Items.Add(Item(Loc.T("tabmenu.openInOtherGroup"), Glyphs.OpenWith, () =>
            {
                other.Add(doc);
                Model.ActiveGroup = other;
            }, enabled: !other.Tabs.Contains(doc)));
        }
        menu.Items.Add(Item(Loc.T("tabmenu.moveToNewWindow"), Glyphs.NewWindow, () => MoveToNewWindow(group, doc)));
        menu.Items.Add(Item(Loc.T("tabmenu.pinOnTop"), Glyphs.Pinned, () => PinOnTop(group, doc), enabled: !Model.Pinned));

        if (text is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Loc.T("tabmenu.compareDisk"), Glyphs.Compare, () => Fire(OpenCompareWithDisk(text)), enabled: hasPath));
            var compare = new MenuItem { Header = Loc.T("tabmenu.compareTab"), Icon = new Icon { Glyph = Glyphs.Columns, FontSize = 13 } };
            var candidates = Model.AllDocuments.OfType<TextDocument>().Where(d => d != text).Take(15).ToList();
            foreach (var other in candidates)
            {
                var target = other;
                var item = new MenuItem { Header = new TextBlock { Text = target.Title }, ToolTip = target.ToolTipText };
                item.Click += (_, _) => Fire(CompareTabs(text, target));
                compare.Items.Add(item);
            }
            compare.IsEnabled = candidates.Count > 0;
            if (candidates.Count == 0) compare.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
            menu.Items.Add(compare);
        }
        return menu;
    }

    // ───────────── status-bar menus ─────────────

    public ContextMenu? BuildEncodingMenu()
    {
        if (ActiveDocument is not TextDocument text) return null;
        var menu = new ContextMenu();
        var reopen = new MenuItem { Header = Loc.T("menu.encodingReopen"), IsEnabled = text.FilePath is not null };
        var save = new MenuItem { Header = Loc.T("menu.encodingSave") };
        foreach (var choice in TextFileIO.Choices)
        {
            var enc = choice.Get();
            var bom = choice.Bom;
            var label = choice.Label;
            var current = text.Encoding.CodePage == enc.CodePage && (enc.CodePage != 65001 || text.HasBom == bom);
            if (choice.Id != "utf-8-bom")
            {
                var item = new MenuItem { Header = label, IsChecked = current };
                item.Click += (_, _) => Fire(ReopenWithEncoding(text, enc));
                reopen.Items.Add(item);
            }
            var saveItem = new MenuItem { Header = label, IsChecked = current };
            saveItem.Click += (_, _) =>
            {
                text.Encoding = enc;
                text.HasBom = bom;
                text.MarkForcedDirty();
                Toasts.Show(Loc.T("encoding.changed", label), ToastKind.Info);
            };
            save.Items.Add(saveItem);
        }
        menu.Items.Add(reopen);
        menu.Items.Add(save);
        return menu;
    }

    public ContextMenu? BuildEolMenu()
    {
        if (ActiveDocument is not TextDocument text) return null;
        var menu = new ContextMenu();
        foreach (var (eol, key) in new[] { (LineEnding.CRLF, "eol.crlf"), (LineEnding.LF, "eol.lf"), (LineEnding.CR, "eol.cr") })
        {
            var target = eol;
            menu.Items.Add(Item(Loc.T(key), null, () =>
            {
                text.ConvertLineEndings(target);
                Toasts.Show(Loc.T("eol.changed", target.Label()), ToastKind.Info, seconds: 1.8);
            }, isChecked: text.Eol == eol));
        }
        return menu;
    }

    public ContextMenu BuildIndentMenu()
    {
        var s = SettingsService.I.Get<EditorSettings>();
        var menu = new ContextMenu();
        menu.Items.Add(Item(Loc.T("indent.spaces"), null, () => s.InsertSpaces = true, isChecked: s.InsertSpaces));
        menu.Items.Add(Item(Loc.T("indent.tabs"), null, () => s.InsertSpaces = false, isChecked: !s.InsertSpaces));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Loc.T("indent.width"), IsEnabled = false });
        foreach (var size in new[] { 2, 3, 4, 8 })
        {
            var n = size;
            menu.Items.Add(Item(n.ToString(), null, () => s.TabSize = n, isChecked: s.TabSize == n));
        }
        return menu;
    }
}
