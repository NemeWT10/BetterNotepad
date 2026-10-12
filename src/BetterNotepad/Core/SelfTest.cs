#if DEBUG
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Settings;
using BetterNotepad.UI.Themes;
using BetterNotepad.Documents;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Core;

/// <summary>
/// Debug-only smoke test of the windowed core, started with BETTERNOTEPAD_SELFTEST=1 together with --test-mode
/// (window off-screen, nothing shown, no popup is ever opened). Writes "SELFTEST OK" or the failures to app.log, then quits.
/// </summary>
internal static class SelfTest
{
    private static int _tries;

    public static bool Requested => AppEnv.TestMode && Environment.GetEnvironmentVariable("BETTERNOTEPAD_SELFTEST") == "1";

    public static void Start()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += async (_, _) =>
        {
            var w = App.Current.MainAppWindow;
            if (w?.ActiveView?.Editor is null && ++_tries < 40) return;
            timer.Stop();
            var failures = new List<string>();
            try
            {
                if (w is null) failures.Add("no main window");
                else await Run(w, failures);
            }
            catch (Exception ex)
            {
                failures.Add("exception: " + ex);
            }
            if (failures.Count == 0) Log.Info("SELFTEST OK");
            else foreach (var f in failures) Log.Error("SELFTEST FAIL: " + f);
            App.Current.Quit();
        };
        timer.Start();
    }

    private static void Check(List<string> failures, bool ok, string what)
    {
        if (!ok) failures.Add(what);
    }

    private static async Task Run(MainWindow w, List<string> failures)
    {
        var group = w.Model.ActiveGroup;
        var doc = w.ActiveDocument;
        Check(failures, doc is not null, "active document");
        if (doc is null) return;
        Log.Info($"selftest: active tab '{doc.Title}', tabs={group.Tabs.Count}");

        if (Program.Args.Length > 0)
        {
            Check(failures, doc.FilePath is not null && FileUtil.PathEquals(doc.FilePath, Program.Args[0]), "file argument is the active tab");
            Check(failures, w.ActiveView?.Editor?.Document.Text.Contains("zażółć") == true, "file argument content loaded");
        }

        // menus and their templates
        foreach (var top in w.TopMenu.Items.OfType<MenuItem>().ToList())
        {
            top.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.SubmenuOpenedEvent, top));
            Touch(top);
        }
        var tab = w.BuildTabMenu(group, doc);
        foreach (var i in tab.Items.OfType<MenuItem>()) Touch(i);
        foreach (var m in new[] { w.BuildEncodingMenu(), w.BuildEolMenu(), w.BuildIndentMenu() })
        {
            Check(failures, m is not null, "status menu null");
            if (m is null) continue;
            m.ApplyTemplate();
            foreach (var i in m.Items.OfType<MenuItem>()) Touch(i);
        }

        // every registered command has strings and a valid, conflict-free default shortcut (never Ctrl+Alt+letter)
        var seen = new Dictionary<KeyChord, string>();
        foreach (var cmd in CommandRegistry.All)
        {
            Check(failures, Loc_Has(cmd.TitleKey), "missing title " + cmd.TitleKey);
            Check(failures, Loc_Has(cmd.CategoryKey), "missing category " + cmd.CategoryKey);
            if (cmd.DefaultGesture is null) continue;
            if (KeyChord.Parse(cmd.DefaultGesture) is not { } chord)
            {
                failures.Add($"unparseable shortcut '{cmd.DefaultGesture}' of {cmd.Id}");
                continue;
            }
            Check(failures, !chord.IsAltGrConflict, $"AltGr conflict: {cmd.Id} {chord}");
            Check(failures, chord.ToString() == cmd.DefaultGesture, $"shortcut text '{cmd.DefaultGesture}' of {cmd.Id} reads back as '{chord}'");
            Check(failures, !seen.TryGetValue(chord, out var other), $"duplicate shortcut {chord}: {cmd.Id} / {other}");
            seen[chord] = cmd.Id;
        }

        // live switches: interface language rebuilds the menu, theme swaps the palette
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        appearance.Language = "pl";
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        Check(failures, w.TopMenu.Items.OfType<MenuItem>().First().Header as string == "Plik", "menu did not switch to Polish");
        appearance.Language = "en";
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        Check(failures, w.TopMenu.Items.OfType<MenuItem>().First().Header as string == "File", "menu did not switch to English");
        // toggle templates: every part named by Motion.Toggle resolves (else the change would not animate)
        foreach (var (key, control, parts) in new (string, System.Windows.Controls.Primitives.ToggleButton, int)[]
                 { ("Switch", new CheckBox(), 3), ("Segment", new RadioButton(), 1), ("NavItem", new RadioButton(), 2) })
        {
            control.Style = (System.Windows.Style)System.Windows.Application.Current.FindResource(key);
            control.ApplyTemplate();
            Check(failures, BetterNotepad.UI.Motion.ToggleParts(control).Count == parts, $"{key}: Motion.Toggle parts {BetterNotepad.UI.Motion.ToggleParts(control).Count}/{parts}");
        }

        // a numbered untitled tab is named in the current UI language ("nowy 1" / "new 1"); a custom name stays
        var untitled = DocumentManager.I.NewUntitled();
        appearance.Language = "pl";
        Check(failures, untitled.Title.StartsWith("nowy ", StringComparison.Ordinal), "untitled title in Polish: " + untitled.Title);
        appearance.Language = "en";
        Check(failures, untitled.Title.StartsWith("new ", StringComparison.Ordinal), "untitled title in English: " + untitled.Title);
        untitled.UntitledName = "scratch.txt";
        appearance.Language = "pl";
        Check(failures, untitled.Title == "scratch.txt" && untitled.UntitledNumber == 0, "custom untitled name kept: " + untitled.Title);
        DocumentManager.I.Close(untitled);
        appearance.Language = "system";
        foreach (var id in new[] { "daylight", "neon", "sunset", "graphite", "aurora" })
        {
            appearance.Theme = id;
            Check(failures, ThemeManager.I.Current.Id == id, "theme " + id);
        }
        appearance.Theme = "system";

        // editor commands on a scratch tab
        var scratch = w.NewTab(content: "pear\napple\nfig\napple\n");
        var ed = w.ActiveView?.Editor;
        Check(failures, ed is not null, "scratch editor");
        if (ed is null) return;
        string Text() => ed.Document.Text.Replace("\r\n", "\n");
        w.RunCommand("tools.sortAsc");
        Check(failures, Text() == "\napple\napple\nfig\npear", "sortAsc gave: " + Text().Replace("\n", "|"));
        w.RunCommand("tools.uniqueLines");
        Check(failures, Text() == "\napple\nfig\npear", "unique gave: " + Text().Replace("\n", "|"));
        ed.CaretOffset = 1;
        w.RunCommand("editor.moveLineDown");
        Check(failures, Text() == "\nfig\napple\npear", "moveLineDown gave: " + Text().Replace("\n", "|"));
        w.RunCommand("editor.deleteLine");
        Check(failures, Text() == "\nfig\npear", "deleteLine gave: " + Text().Replace("\n", "|"));
        w.RunCommand("tools.upper");
        Check(failures, Text() == "\nFIG\nPEAR", "upper gave: " + Text().Replace("\n", "|"));
        w.RunCommand("edit.undo");
        Check(failures, Text() == "\nfig\npear", "undo gave: " + Text().Replace("\n", "|"));

        if (scratch is TextDocument t)
        {
            t.LanguageId = "json";
            ed.Document.Text = "{\"a\":[1,2],\"b\":{\"c\":null}}";
            w.RunCommand("tools.formatDocument");
            Check(failures, Text().Contains("\n") && Text().Contains("  \"a\""), "json format gave: " + Text());
            w.RunCommand("tools.minify");
            Check(failures, Text() == "{\"a\":[1,2],\"b\":{\"c\":null}}", "json minify gave: " + Text());
            ed.Document.Text = "{ broken";
            w.RunCommand("tools.formatDocument");
            Check(failures, Text() == "{ broken", "broken json must stay unchanged");
        }

        w.RunCommand("view.zoomIn");
        w.RunCommand("view.zoomReset");
        w.RunCommand("editor.find");
        w.RunCommand("palette.commands");
        w.RunCommand("app.settings");
        w.Execute("tabs.next");
        w.Execute("tabs.prev");

        // closing the scratch tab asks nothing when it has no content to lose; ours has some, so just drop it
        group.Remove(scratch);
        DocumentManager.I.Close(scratch);

        await FileFlow(w, failures);
        await BetterNotepad.UI.Palette.PaletteSelfTest.Run(w, failures);
        await BetterNotepad.Editor.EditorSelfTest.Run(w, failures);
        await BetterNotepad.UI.SettingsSelfTest.Run(w, failures);
        await BetterNotepad.Modules.Markdown.MarkdownSelfTest.Run(w, failures);
        await BetterNotepad.Modules.Sys.SystemSelfTest.Run(w, failures);
        await BetterNotepad.Modules.Pdf.PdfSelfTest.Run(w, failures);
        await PinSelfTest.Run(w, failures);
    }

    /// <summary>Open → external change → keep mine → save → external change → reload → rename → close.</summary>
    private static async Task FileFlow(MainWindow w, List<string> failures)
    {
        var path = Path.Combine(AppEnv.LocalDir, "selftest-flow.txt");
        var renamed = Path.Combine(AppEnv.LocalDir, "selftest-renamed.txt");
        File.Delete(renamed);
        File.WriteAllText(path, "first version\r\n");
        w.OpenFile(path);
        if (w.ActiveDocument is not TextDocument t || !FileUtil.PathEquals(t.FilePath, path))
        {
            failures.Add("flow: file not opened as active tab");
            return;
        }
        await t.LoadAsync();
        Check(failures, !t.IsDirty, "flow: freshly opened file is dirty");

        File.WriteAllText(path, "changed by another program\r\n");
        w.CheckExternalChanges([t]);
        Check(failures, t.External == ExternalChange.Modified && t.IsDirty, "flow: external change not noticed");

        t.KeepMine();
        Check(failures, t.External == ExternalChange.None && t.IsDirty, "flow: keep mine should leave the tab unsaved");
        Check(failures, await w.SaveAsync(t), "flow: save failed");
        Check(failures, !t.IsDirty && File.ReadAllText(path) == "first version\r\n", "flow: save did not write our version");

        File.WriteAllText(path, "third and longest version\r\n");
        w.CheckExternalChanges([t]);
        await t.ReloadAsync();
        Check(failures, t.Buffer.Text == "third and longest version\r\n" && !t.IsDirty, "flow: reload from disk");

        w.Rename(t, "selftest-renamed.txt");
        Check(failures, File.Exists(renamed) && !File.Exists(path) && t.Title == "selftest-renamed.txt", "flow: rename");

        Check(failures, await w.CloseTabAsync(w.Model.ActiveGroup, t), "flow: close tab");
        File.Delete(renamed);
    }

    private static bool Loc_Has(string key) => Localization.Loc.AllEntries.ContainsKey(key);

    /// <summary>Forces templates / styles of a menu item (and its sub-items) to build.</summary>
    private static void Touch(MenuItem item)
    {
        item.ApplyTemplate();
        foreach (var i in item.Items.OfType<MenuItem>()) Touch(i);
    }
}
#endif
