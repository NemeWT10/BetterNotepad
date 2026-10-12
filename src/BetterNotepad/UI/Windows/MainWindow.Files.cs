using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Diff;
using BetterNotepad.Documents.Files;
using BetterNotepad.Documents.Session;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.UI.Controls;
using Microsoft.Win32;

namespace BetterNotepad.UI.Windows;

public partial class MainWindow
{
    private static readonly Stack<string> RecentlyClosed = new();
    private readonly Queue<Document> _externalQueue = new();
    private bool _externalPromptOpen;

    // ───────────── new / open ─────────────

    public TextDocument NewTab(TabGroup? group = null, string? content = null, string? languageId = null)
    {
        // a pinned window shows one tab: new tabs and opened files go to the main window
        if (Model.Pinned && App.Current.MainAppWindow is { } main && main != this)
        {
            main.BringToFront();
            return main.NewTab(null, content, languageId);
        }
        var doc = DocumentManager.I.NewUntitled(content, languageId);
        (group ?? Model.ActiveGroup).Add(doc);
        FocusActiveSoon();
        return doc;
    }

    public void OpenFile(string path, TabGroup? group = null)
    {
        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path)) return;
        if (Model.Pinned && App.Current.MainAppWindow is { } main && main != this)
        {
            main.OpenFile(path);
            if (DocumentManager.I.FindByPath(path) is { } opened && Workspace.I.Locate(opened) is { Window.IsMain: true }) main.BringToFront();
            return;
        }
        if (!File.Exists(path))
        {
            Toasts.Show(Loc.T("file.notFound", Path.GetFileName(path)), ToastKind.Warning);
            RecentFiles.I.Remove(path);
            return;
        }
        var existing = DocumentManager.I.FindByPath(path);
        if (existing is not null && Workspace.I.Locate(existing) is { } where)
        {
            where.Group.Active = existing;
            var w = App.Current.WindowFor(where.Window);
            if (w is not null && w != this) w.BringToFront();
            if (where.Window == Model) Model.ActiveGroup = where.Group;
            FocusActiveSoon();
            return;
        }

        var doc = DocumentManager.I.Open(path);
        if (FileStateStore.I.IsLocked(path) || IsReadOnlyAttribute(path)) doc.IsLocked = true;
        // replace a lone empty untitled tab (fresh start) instead of piling up tabs
        var target = group ?? Model.ActiveGroup;
        var lone = target.Tabs.Count == 1 && target.Tabs[0] is TextDocument { IsUntitled: true, IsDirty: false } t && (!t.HasBuffer || t.Buffer.TextLength == 0) ? t : null;
        target.Add(doc);
        if (lone is not null && lone != doc)
        {
            target.Remove(lone);
            if (!Workspace.I.IsOpenAnywhere(lone)) DocumentManager.I.Close(lone);
        }
        RecentFiles.I.Add(path);
        FileWatchService.I.Sync();
        FocusActiveSoon();
    }

    private static bool IsReadOnlyAttribute(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly); }
        catch { return false; }
    }

    public void OpenDialog()
    {
        var files = SettingsService.I.Get<FilesSettings>();
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = Loc.T("dialog.openTitle"),
            Filter = Loc.T("dialog.filterAll") + "|*.*",
            InitialDirectory = CurrentFolder() ?? files.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return;
        files.LastFolder = Path.GetDirectoryName(dlg.FileNames[0]);
        foreach (var f in dlg.FileNames) OpenFile(f);
    }

    private string? CurrentFolder()
    {
        var p = ActiveDocument?.FilePath;
        return p is null ? null : Path.GetDirectoryName(p);
    }

    public void ReopenClosed()
    {
        while (RecentlyClosed.Count > 0)
        {
            var p = RecentlyClosed.Pop();
            if (File.Exists(p))
            {
                OpenFile(p);
                return;
            }
        }
        Toasts.Show(Loc.T("tab.nothingToReopen"));
    }

    private void FocusActiveSoon() => Dispatcher.BeginInvoke(() => ActiveView?.FocusContent(), System.Windows.Threading.DispatcherPriority.Input);

    // ───────────── save ─────────────

    public async Task<bool> SaveAsync(Document? doc = null, bool saveAs = false)
    {
        doc ??= ActiveDocument;
        if (doc is not TextDocument text) return false;
        if (!text.IsLoaded) await text.LoadAsync();
        ActiveView?.CaptureState();

        if (text.IsLocked && !saveAs)
        {
            Toasts.Show(Loc.T("save.locked"), ToastKind.Warning, Loc.T("save.unlock"), () => SetLocked(text, false));
            return false;
        }

        var path = text.FilePath;
        if (path is null || saveAs)
        {
            path = AskSavePath(text);
            if (path is null) return false;
        }

        if (!TextFileIO.CanEncode(text.Buffer.Text, text.Encoding))
        {
            var choice = await Dialogs.Show(Loc.T("save.encodingTitle"), Loc.T("save.encodingText", text.EncodingLabel),
            [
                new DialogButton("utf8", Loc.T("save.encodingUtf8"), DialogButtonKind.Primary, IsDefault: true),
                new DialogButton("keep", Loc.T("save.encodingKeep")),
                new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
            ], glyph: Glyphs.Warning);
            if (choice is null or "cancel") return false;
            if (choice == "utf8")
            {
                text.Encoding = TextFileIO.Utf8NoBom;
                text.HasBom = false;
            }
        }

        try
        {
            text.SaveTo(path);
        }
        catch (UnauthorizedAccessException)
        {
            return await OfferElevatedSave(text, path);
        }
        catch (IOException ex)
        {
            Toasts.Show(Loc.T("save.failed", Path.GetFileName(path), ex.Message), ToastKind.Error);
            return false;
        }

        BackupStore.Delete(text.Id);
        RecentFiles.I.Add(path);
        FileWatchService.I.Sync();
        Toasts.Show(Loc.T("save.done", text.Title), ToastKind.Success, seconds: 1.6);
        UpdateTitle();
        return true;
    }

    private string? AskSavePath(TextDocument text)
    {
        var files = SettingsService.I.Get<FilesSettings>();
        var lang = Languages.Get(text.LanguageId);
        var name = text.Title;
        if (!Path.HasExtension(name)) name += lang.DefaultExtension;
        var dlg = new SaveFileDialog
        {
            Title = Loc.T("dialog.saveTitle"),
            FileName = name,
            Filter = (lang.Id == Languages.PlainText ? "" : $"{lang.Name}|{string.Join(";", lang.Extensions.Select(e => "*" + e))}|") + Loc.T("dialog.filterAll") + "|*.*",
            InitialDirectory = text.FilePath is { } p ? Path.GetDirectoryName(p) : CurrentFolder() ?? files.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = false,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return null;
        files.LastFolder = Path.GetDirectoryName(dlg.FileName);
        return dlg.FileName;
    }

    /// <summary>Protected file (e.g. hosts): write a temp copy and let an elevated copy of the app move it in place.</summary>
    private async Task<bool> OfferElevatedSave(TextDocument text, string path)
    {
        var choice = await Dialogs.Show(Loc.T("save.deniedTitle"), Loc.T("save.deniedText", Path.GetFileName(path)),
        [
            new DialogButton("admin", Loc.T("save.asAdmin"), DialogButtonKind.Primary, IsDefault: true),
            new DialogButton("saveas", Loc.T("save.saveAsElsewhere")),
            new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
        ], glyph: Glyphs.Shield);
        if (choice == "saveas") return await SaveAsync(text, saveAs: true);
        if (choice != "admin") return false;

        var temp = Path.Combine(Path.GetTempPath(), "BetterNotepad-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            TextFileIO.Write(temp, text.Buffer.Text, text.Encoding, text.HasBom);
            var ok = await Task.Run(() => ElevatedSave.Run(temp, path));
            if (!ok)
            {
                Toasts.Show(Loc.T("save.adminFailed"), ToastKind.Error);
                return false;
            }
            text.SaveToCompleted(path);
            Toasts.Show(Loc.T("save.done", text.Title), ToastKind.Success, seconds: 1.6);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("elevated save failed", ex);
            Toasts.Show(Loc.T("save.adminFailed"), ToastKind.Error);
            return false;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    public async Task SaveAllAsync()
    {
        foreach (var d in DocumentManager.I.Documents.ToList())
            if (d.IsDirty && d.CanSave) await SaveAsync(d);
    }

    // ───────────── close ─────────────

    /// <summary>Closes a tab; asks to save only when this was the last view of an unsaved document.</summary>
    public async Task<bool> CloseTabAsync(TabGroup group, Document doc)
    {
        var elsewhere = Workspace.I.Windows.SelectMany(w => w.Groups).Any(g => g != group && g.Tabs.Contains(doc));
        if (!elsewhere && doc.IsDirty)
        {
            group.Active = doc;
            var name = doc.Title;
            var choice = await Dialogs.Show(Loc.T("close.saveTitle", name), Loc.T("close.saveText"),
            [
                new DialogButton("save", Loc.T("close.save"), DialogButtonKind.Primary, IsDefault: true),
                new DialogButton("discard", Loc.T("close.discard"), DialogButtonKind.Danger),
                new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
            ], glyph: Glyphs.Save);
            if (choice is null or "cancel") return false;
            if (choice == "save" && !await SaveAsync(doc)) return false;
        }

        group.Remove(doc);
        if (!Workspace.I.IsOpenAnywhere(doc))
        {
            if (doc.FilePath is not null) RecentlyClosed.Push(doc.FilePath);
            BackupStore.Delete(doc.Id);
            DocumentManager.I.Close(doc);
            FileWatchService.I.Sync();
        }
        CleanupGroup(group);
        FocusActiveSoon();
        return true;
    }

    public async Task CloseOthersAsync(TabGroup group, Document keep)
    {
        foreach (var d in group.Tabs.Where(t => t != keep && !t.IsPinned).ToList())
            if (!await CloseTabAsync(group, d)) return;
    }

    public async Task CloseToRightAsync(TabGroup group, Document from)
    {
        var index = group.Tabs.IndexOf(from);
        foreach (var d in group.Tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList())
            if (!await CloseTabAsync(group, d)) return;
    }

    public async Task CloseSavedAsync(TabGroup group)
    {
        foreach (var d in group.Tabs.Where(t => !t.IsDirty && !t.IsPinned).ToList())
            await CloseTabAsync(group, d);
    }

    public async Task CloseAllAsync(TabGroup group)
    {
        foreach (var d in group.Tabs.Where(t => !t.IsPinned).ToList())
            if (!await CloseTabAsync(group, d)) return;
    }

    /// <summary>Empty secondary group → unsplit; empty torn-off window → close it.</summary>
    private void CleanupGroup(TabGroup group)
    {
        if (group.Tabs.Count > 0) return;
        if (Model.Groups.Count > 1)
        {
            Model.RemoveGroup(group);
            return;
        }
        if (!Model.IsMain) Close();
    }

    // ───────────── rename ─────────────

    public void Rename(Document doc, string newName, TabChip? chip = null)
    {
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            chip?.ShakeRename();
            Toasts.Show(Loc.T("rename.invalid"), ToastKind.Warning);
            return;
        }
        if (doc.IsUntitled)
        {
            doc.UntitledName = newName;
            return;
        }
        var oldPath = doc.FilePath!;
        var target = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
        if (string.Equals(target, oldPath, StringComparison.Ordinal)) return;
        if (File.Exists(target) && !string.Equals(target, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            chip?.ShakeRename();
            Toasts.Show(Loc.T("rename.exists", newName), ToastKind.Warning);
            return;
        }
        try
        {
            File.Move(oldPath, target);
            doc.ChangePath(target);
            if (FileStateStore.I.IsLocked(oldPath))
            {
                FileStateStore.I.SetLocked(oldPath, false);
                FileStateStore.I.SetLocked(target, true);
            }
            RecentFiles.I.Remove(oldPath);
            RecentFiles.I.Add(target);
            FileWatchService.I.Sync();
            Toasts.Show(Loc.T("rename.done", newName), ToastKind.Success, seconds: 1.6);
        }
        catch (Exception ex)
        {
            chip?.ShakeRename();
            Toasts.Show(Loc.T("rename.failed", ex.Message), ToastKind.Error);
        }
    }

    public void BeginRename(Document? doc = null)
    {
        doc ??= ActiveDocument;
        if (doc is null) return;
        var loc = Workspace.I.Locate(doc);
        if (loc is null) return;
        var view = App.Current.WindowFor(loc.Value.Window)?._groupViews.GetValueOrDefault(loc.Value.Group);
        view?.ChipFor(doc)?.BeginRename();
    }

    // ───────────── lock (read-only) ─────────────

    public async void SetLocked(Document doc, bool locked)
    {
        if (!locked && doc.FilePath is { } path && IsReadOnlyAttribute(path))
        {
            var choice = await Dialogs.Show(Loc.T("lock.attrTitle"), Loc.T("lock.attrText", doc.Title),
            [
                new DialogButton("remove", Loc.T("lock.attrRemove"), DialogButtonKind.Primary, IsDefault: true),
                new DialogButton("app", Loc.T("lock.attrAppOnly")),
                new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
            ], glyph: Glyphs.Unlock);
            if (choice is null or "cancel") return;
            if (choice == "remove")
            {
                try { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); }
                catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); return; }
            }
        }
        doc.IsLocked = locked;
        if (doc.FilePath is not null) FileStateStore.I.SetLocked(doc.FilePath, locked);
        Toasts.Show(Loc.T(locked ? "lock.on" : "lock.off", doc.Title), ToastKind.Info, seconds: 1.8);
    }

    // ───────────── moving tabs between groups and windows ─────────────

    public void MoveTab(TabGroup source, TabGroup target, Document doc, int index = -1)
    {
        if (source == target)
        {
            if (index >= 0) target.Move(doc, index);
            target.Active = doc;
            return;
        }
        if (!target.Tabs.Contains(doc)) target.Add(doc, index);
        else target.Active = doc;
        source.Remove(doc);
        var sourceWindow = Workspace.I.Windows.FirstOrDefault(w => w.Groups.Contains(source));
        var targetWindow = Workspace.I.Windows.FirstOrDefault(w => w.Groups.Contains(target));
        if (targetWindow is not null)
        {
            targetWindow.ActiveGroup = target;
            App.Current.WindowFor(targetWindow)?.BringToFront();
        }
        if (sourceWindow is not null) App.Current.WindowFor(sourceWindow)?.CleanupGroup(source);
    }

    /// <summary>Tab dropped outside every tab strip: it becomes its own window under the cursor.</summary>
    public void TearOff(TabGroup source, Document doc, Point deviceCursor)
    {
        var dip = DeviceToDip(deviceCursor);
        var model = new WindowModel();
        model.Bounds = new Rect(dip.X - 140, dip.Y - 20, Math.Min(ActualWidth, 980), Math.Min(ActualHeight, 680));
        model.Groups[0].Add(doc);
        source.Remove(doc);
        Workspace.I.Windows.Add(model);
        CleanupGroup(source);
    }

    public void MoveToNewWindow(TabGroup source, Document doc)
    {
        var p = new Point(Left + 80, Top + 80);
        var model = new WindowModel { Bounds = new Rect(p.X, p.Y, Math.Min(ActualWidth, 980), Math.Min(ActualHeight, 680)) };
        model.Groups[0].Add(doc);
        source.Remove(doc);
        Workspace.I.Windows.Add(model);
        CleanupGroup(source);
    }

    /// <summary>Split the window (VS Code style: the active document also opens in the new group).</summary>
    public void Split(Orientation orientation, bool moveInsteadOfCopy = false)
    {
        if (Model.Pinned) return; // one tab, edge to edge
        var doc = ActiveDocument;
        Model.SplitOrientation = orientation;
        var target = Model.Groups.Count > 1 ? Model.Groups.First(g => g != Model.ActiveGroup) : Model.AddGroup();
        if (doc is not null)
        {
            target.Add(doc);
            if (moveInsteadOfCopy) Model.ActiveGroup.Remove(doc);
        }
        else
        {
            target.Add(DocumentManager.I.NewUntitled());
        }
        Model.ActiveGroup = target;
        FocusActiveSoon();
    }

    public void Unsplit()
    {
        if (Model.Groups.Count < 2) return;
        var keep = Model.Groups[0];
        foreach (var g in Model.Groups.Skip(1).ToList())
        {
            foreach (var d in g.Tabs.ToList())
            {
                if (!keep.Tabs.Contains(d)) keep.Add(d, activate: false);
                g.Remove(d);
            }
            Model.RemoveGroup(g);
        }
        Model.ActiveGroup = keep;
    }

    public void FocusOtherGroup()
    {
        if (Model.Groups.Count < 2) return;
        Model.ActiveGroup = Model.Groups.First(g => g != Model.ActiveGroup);
        FocusActiveSoon();
    }

    // ───────────── changes made by other programs ─────────────

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        PinnedWindows.Touch(Model);
        if (!Model.IsMain && !IsActive) return;
        CheckExternalChanges(Model.AllDocuments);
    }

    /// <summary>Called by the watcher (any window) and on activation: marks documents and queues prompts.</summary>
    public void CheckExternalChanges(IEnumerable<Document> docs)
    {
        foreach (var d in docs.OfType<TextDocument>().ToList())
        {
            if (!d.IsLoaded || d.FilePath is null) continue;
            if (!d.DiskDiffers(out var deleted)) continue;
            if (d.TailMode && !deleted && !d.IsDirty)
            {
                _ = ReloadTail(d);
                continue;
            }
            var change = deleted ? ExternalChange.Deleted : ExternalChange.Modified;
            if (d.External != change) d.External = change;
            if (IsActive && Model.AllDocuments.Contains(d)) QueueExternalPrompt(d);
        }
    }

    private async Task ReloadTail(TextDocument d)
    {
        await d.ReloadAsync();
        foreach (var w in App.Current.OpenWindows)
            foreach (var g in w.Model.Groups)
                if (g.Active == d && w.ViewOf(g)?.ActiveView?.Editor is { } ed)
                    ed.ScrollToEnd();
    }

    internal TabGroupView? ViewOf(TabGroup g) => _groupViews.GetValueOrDefault(g);

    public void QueueExternalPrompt(Document d)
    {
        if (_externalQueue.Contains(d)) return;
        _externalQueue.Enqueue(d);
        if (!_externalPromptOpen) _ = PumpExternalPrompts();
    }

    private async Task PumpExternalPrompts()
    {
        _externalPromptOpen = true;
        try
        {
            while (_externalQueue.Count > 0)
            {
                var d = _externalQueue.Dequeue();
                if (d is not TextDocument t || d.External == ExternalChange.None || !DocumentManager.I.Documents.Contains(d)) continue;
                if (t.External == ExternalChange.Deleted) await PromptDeleted(t);
                else await PromptModified(t);
            }
        }
        finally
        {
            _externalPromptOpen = false;
        }
    }

    private async Task PromptModified(TextDocument t)
    {
        var loc = Workspace.I.Locate(t);
        if (loc is { } l && l.Window == Model) l.Group.Active = t;
        var choice = await Dialogs.Show(Loc.T("external.modifiedTitle", t.Title), Loc.T("external.modifiedText"),
        [
            new DialogButton("reload", Loc.T("external.reload"), DialogButtonKind.Primary, IsDefault: true),
            new DialogButton("keep", Loc.T("external.keep")),
            new DialogButton("compare", Loc.T("external.compare"), DialogButtonKind.Ghost),
        ], glyph: Glyphs.Refresh);
        switch (choice)
        {
            case "reload":
                await t.ReloadAsync();
                Toasts.Show(Loc.T("external.reloaded", t.Title), ToastKind.Info, seconds: 1.8);
                break;
            case "compare":
                await OpenCompareWithDisk(t);
                t.KeepMine();
                break;
            default:
                t.KeepMine();
                break;
        }
    }

    private async Task PromptDeleted(TextDocument t)
    {
        var loc = Workspace.I.Locate(t);
        if (loc is { } l && l.Window == Model) l.Group.Active = t;
        var choice = await Dialogs.Show(Loc.T("external.deletedTitle", t.Title), Loc.T("external.deletedText"),
        [
            new DialogButton("keep", Loc.T("external.keepInApp"), DialogButtonKind.Primary, IsDefault: true),
            new DialogButton("close", Loc.T("external.closeTab"), DialogButtonKind.Danger),
        ], glyph: Glyphs.Warning);
        if (choice == "close" && Workspace.I.Locate(t) is { } where)
        {
            t.External = ExternalChange.None;
            t.MarkClean();
            where.Group.Remove(t);
            DocumentManager.I.Close(t);
            CleanupGroup(where.Group);
            return;
        }
        if (!t.IsLoaded) await t.LoadAsync();
        t.External = ExternalChange.None;
        t.MarkForcedDirty();
    }

    /// <summary>Unified diff: disk version vs. the version in the app, in a new tab.</summary>
    public async Task OpenCompareWithDisk(TextDocument t)
    {
        if (t.FilePath is null) return;
        string disk;
        try
        {
            disk = (await Task.Run(() => TextFileIO.Read(t.FilePath))).Text;
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastKind.Error);
            return;
        }
        var mine = t.HasBuffer ? t.Buffer.Text : "";
        var diff = LineDiff.Unified(disk, mine, Loc.T("compare.diskSide", t.Title), Loc.T("compare.appSide", t.Title));
        var tab = NewTab(content: diff, languageId: "diff");
        tab.UntitledName = Loc.T("compare.tabName", t.Title);
    }

    public async Task CompareTabs(Document a, Document b)
    {
        if (a is not TextDocument ta || b is not TextDocument tb) return;
        if (!ta.IsLoaded) await ta.LoadAsync();
        if (!tb.IsLoaded) await tb.LoadAsync();
        var diff = LineDiff.Unified(ta.Buffer.Text, tb.Buffer.Text, ta.Title, tb.Title);
        var tab = NewTab(content: diff, languageId: "diff");
        tab.UntitledName = Loc.T("compare.tabName2", ta.Title, tb.Title);
    }

    // ───────────── misc file actions ─────────────

    public void RevealInExplorer(Document doc)
    {
        if (doc.FilePath is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{doc.FilePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    public void CopyPath(Document doc, bool nameOnly)
    {
        var text = nameOnly ? doc.Title : doc.FilePath ?? doc.Title;
        try
        {
            Clipboard.SetText(text);
            Toasts.Show(Loc.T("tab.copied", text), ToastKind.Success, seconds: 1.4);
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    public async Task ReopenWithEncoding(TextDocument doc, Encoding enc)
    {
        if (doc.FilePath is null)
        {
            doc.Encoding = enc;
            return;
        }
        if (doc.IsDirty)
        {
            var choice = await Dialogs.Show(Loc.T("encoding.reopenTitle"), Loc.T("encoding.reopenDirty"),
            [
                new DialogButton("reopen", Loc.T("encoding.reopen"), DialogButtonKind.Primary, IsDefault: true),
                new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
            ], glyph: Glyphs.Warning);
            if (choice != "reopen") return;
        }
        await doc.ReloadAsync(enc);
        doc.HasBom = false;
    }
}
