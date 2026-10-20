#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Session;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Explorer;

/// <summary>
/// Self-test of the folder tree on its own fixture under the test data folder: Ctrl+Shift+E, order and filtering, expand /
/// collapse, opening, following the active tab, the watcher (also while hidden), new file / folder, rename (open tabs
/// follow), delete (permanent in test mode), session round trip, a big folder, a folder that is gone.
/// </summary>
internal static class ExplorerSelfTest
{
    public static async Task Run(MainWindow w, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add("explorer: " + what);
        }

        static async Task<bool> WaitFor(Func<bool> probe, int timeoutMs = 5000)
        {
            for (var waited = 0; waited < timeoutMs; waited += 40)
            {
                if (probe()) return true;
                await Task.Delay(40);
            }
            return probe();
        }

        var root = Path.Combine(AppEnv.LocalDir, "explorer-selftest");
        string P(params string[] parts) => Path.Combine([root, .. parts]);
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            foreach (var d in new[] { P("alpha", "nested", "deep"), P("beta"), P(".git") }) Directory.CreateDirectory(d);
            foreach (var f in new[] { P("file10.txt"), P("file2.txt"), P("File1.txt"), P("alpha", "one.cs"), P("alpha", "nested", "deep", "target.txt"), P("beta", "b.md"), P(".git", "HEAD") })
                File.WriteAllText(f, Path.GetFileName(f));
            File.WriteAllText(P("hidden.txt"), "h");
            File.SetAttributes(P("hidden.txt"), FileAttributes.Hidden);

            // ── Ctrl+Shift+E toggles the panel ──
            var toggle = Keymap.Match(KeyChord.Parse("Ctrl+Shift+E")!.Value, w.Context());
            Check(toggle?.Id == "view.toggleExplorer", "Ctrl+Shift+E is " + (toggle?.Id ?? "nothing"));
            var before = w.ExplorerShown;
            toggle?.Execute(w.Context());
            Check(w.ExplorerShown != before, "Ctrl+Shift+E did not toggle the panel");
            toggle?.Execute(w.Context());
            Check(w.ExplorerShown == before, "Ctrl+Shift+E did not toggle back");

            // ── open the folder: folders first, natural order, hidden entries and .git left out ──
            w.OpenFolder(root);
            var panel = w.Explorer;
            Check(panel is not null && w.ExplorerShown && w.Model.ExplorerRoot == root, "open folder: panel / root");
            if (panel is null) return;
            await panel.Ready;
            var tree = panel.Tree!;
            Check(tree.IsWatching, "the folder is not watched");
            var names = string.Join(",", tree.Rows.Select(r => r.Name));
            Check(names == "alpha,beta,File1.txt,file2.txt,file10.txt", "top-level order: " + names);

            // ── width: the remembered one, at most half the window, at least 170 ──
            var width = w.Model.ExplorerWidth;
            w.Model.ExplorerWidth = 5000;
            var half = Math.Max(ExplorerPanel.MinPanelWidth, w.ActualWidth * 0.5);
            Check(Math.Abs(panel.Width - half) < 1 && Math.Abs(w.ExplorerSlot.Width - half) < 1, $"wide panel {panel.Width} / slot {w.ExplorerSlot.Width}, half the window {half}");
            w.Model.ExplorerWidth = 100;
            Check(Math.Abs(panel.Width - ExplorerPanel.MinPanelWidth) < 1, "narrow panel " + panel.Width);
            w.Model.ExplorerWidth = width;
            Check(Math.Abs(panel.Width - Math.Min(width, half)) < 1, "panel width restored " + panel.Width);

            // ── expand / collapse ──
            var alpha = tree.Find(P("alpha"))!;
            await panel.ExpandAsync(alpha);
            names = string.Join(",", tree.Rows.Select(r => r.Name));
            Check(names == "alpha,nested,one.cs,beta,File1.txt,file2.txt,file10.txt", "expanded alpha: " + names);
            panel.Collapse(alpha);
            Check(tree.Rows.Count == 5, "collapse alpha: rows " + tree.Rows.Count);
            await panel.ExpandAsync(alpha);
            Check(tree.Rows.Count == 7, "expand alpha again: rows " + tree.Rows.Count);

            // ── a click opens the file in a tab; its row gets the open dot ──
            var one = tree.Find(P("alpha", "one.cs"))!;
            panel.OpenNode(one, focus: false);
            Check(FileUtil.PathEquals(w.ActiveDocument?.FilePath, P("alpha", "one.cs")), "click did not open one.cs");
            Check(await WaitFor(() => one.IsOpen), "open file has no marker");

            // ── following the active tab: a deep file opened elsewhere is expanded to and highlighted ──
            w.OpenFile(P("alpha", "nested", "deep", "target.txt"));
            Check(await WaitFor(() => FileUtil.PathEquals(panel.Selected?.FullPath, P("alpha", "nested", "deep", "target.txt"))), "active file not revealed: " + panel.Selected?.FullPath);
            Check(tree.Find(P("alpha", "nested")) is { IsExpanded: true }, "ancestors of the active file not expanded");
            Check(w.Model.ExplorerExpanded.Contains(Path.Combine("alpha", "nested")), "expanded folders not in the window model: " + string.Join("|", w.Model.ExplorerExpanded));

            // ── the watcher: changes by other programs show up, in expanded folders too ──
            File.WriteAllText(P("external.txt"), "x");
            Check(await WaitFor(() => tree.Rows.Any(r => r.Name == "external.txt")), "external create not picked up");
            File.WriteAllText(P("alpha", "two.cs"), "x");
            Check(await WaitFor(() => tree.Rows.Any(r => r.Name == "two.cs")), "external create in an expanded folder not picked up");
            File.Delete(P("external.txt"));
            Check(await WaitFor(() => tree.Rows.All(r => r.Name != "external.txt")), "external delete not picked up");
            Check(FileUtil.PathEquals(panel.Selected?.FullPath, P("alpha", "nested", "deep", "target.txt")), "selection lost by a refresh");

            // while hidden nothing is applied (only queued), it lands when the panel shows again
            w.HideExplorer(animate: false);
            File.WriteAllText(P("while-hidden.txt"), "x");
            await Task.Delay(700);
            Check(tree.Rows.All(r => r.Name != "while-hidden.txt"), "a hidden panel applied watcher changes");
            Check(tree.HasDeferredChanges, "a hidden panel did not keep the change for later");
            w.ShowExplorer(animate: false);
            Check(await WaitFor(() => tree.Rows.Any(r => r.Name == "while-hidden.txt")), "the deferred change was not applied on show");

            // ── new file: name checks as you type, then created and opened ──
            panel.Select(null);
            await panel.BeginNewItemAsync(folder: false);
            var box = panel.EditorBox;
            Check(box is not null, "new file: no name box");
            if (box is not null)
            {
                box.Text = "con.txt";
                Check(panel.NameProblem is not null, "reserved name accepted");
                box.Text = "a:b";
                Check(panel.NameProblem is not null, "invalid character accepted");
                box.Text = "FILE2.TXT";
                Check(panel.NameProblem?.Contains("FILE2.TXT") == true, "existing name accepted: " + panel.NameProblem);
                Check(!await panel.CommitEditAsync() && panel.EditorBox is not null, "an existing name was committed");
                box.Text = "created.md";
                Check(panel.NameProblem is null, "valid name rejected: " + panel.NameProblem);
                Check(await panel.CommitEditAsync(), "create failed");
                Check(File.Exists(P("created.md")), "created.md not on disk");
                Check(FileUtil.PathEquals(w.ActiveDocument?.FilePath, P("created.md")), "new file not opened in a tab");
                Check(tree.Find(P("created.md")) is { IsSelected: true }, "new file not selected");
            }

            // Esc-like cancel leaves nothing behind
            await panel.BeginNewItemAsync(folder: true);
            if (panel.EditorBox is { } cancelled) cancelled.Text = "never";
            panel.CancelEdit();
            Check(!Directory.Exists(P("never")) && tree.Rows.All(r => r.Role != NodeRole.NewItem), "cancelled new folder left something");

            // ── new folder ──
            await panel.BeginNewItemAsync(folder: true);
            if (panel.EditorBox is { } folderBox) folderBox.Text = "newdir";
            Check(await panel.CommitEditAsync() && Directory.Exists(P("newdir")) && tree.Find(P("newdir")) is { IsDirectory: true }, "new folder");

            // ── rename: the open tab follows the file ──
            var created = tree.Find(P("created.md"));
            var createdDoc = DocumentManager.I.FindByPath(P("created.md"));
            if (created is not null)
            {
                panel.BeginRename(created);
                if (panel.EditorBox is { } rbox) rbox.Text = "renamed.md";
                Check(await panel.CommitEditAsync(), "rename failed");
                Check(File.Exists(P("renamed.md")) && !File.Exists(P("created.md")), "rename on disk");
                Check(created.Name == "renamed.md" && FileUtil.PathEquals(created.FullPath, P("renamed.md")), "renamed node: " + created.FullPath);
                Check(createdDoc is not null && FileUtil.PathEquals(createdDoc.FilePath, P("renamed.md")), "open tab did not follow the rename: " + createdDoc?.FilePath);
                // Esc keeps the name
                panel.BeginRename(created);
                if (panel.EditorBox is { } ebox) ebox.Text = "zzz.md";
                panel.CancelEdit();
                Check(created.Name == "renamed.md" && !File.Exists(P("zzz.md")), "cancelled rename changed something");
            }

            // a folder with open files inside: every tab follows
            var oneDoc = DocumentManager.I.FindByPath(P("alpha", "one.cs"));
            var deepDoc = DocumentManager.I.FindByPath(P("alpha", "nested", "deep", "target.txt"));
            panel.BeginRename(alpha);
            if (panel.EditorBox is { } abox) abox.Text = "gamma";
            Check(await panel.CommitEditAsync(), "folder rename failed");
            Check(Directory.Exists(P("gamma")) && !Directory.Exists(P("alpha")), "folder rename on disk");
            Check(oneDoc is not null && FileUtil.PathEquals(oneDoc.FilePath, P("gamma", "one.cs")), "tab inside the renamed folder: " + oneDoc?.FilePath);
            Check(deepDoc is not null && FileUtil.PathEquals(deepDoc.FilePath, P("gamma", "nested", "deep", "target.txt")), "deep tab inside the renamed folder: " + deepDoc?.FilePath);
            Check(tree.Find(P("gamma", "nested", "deep", "target.txt")) is not null, "renamed folder's loaded subtree did not move");
            // a case-only rename of a folder
            if (tree.Find(P("beta")) is { } beta)
            {
                panel.BeginRename(beta);
                if (panel.EditorBox is { } bbox) bbox.Text = "Beta";
                Check(await panel.CommitEditAsync(), "case-only folder rename failed");
                Check(new DirectoryInfo(P("Beta")).Parent!.GetDirectories("Beta").Any(d => d.Name == "Beta") && beta.Name == "Beta", "case-only folder rename");
            }

            // ── delete (test mode: permanent, own fixture only): the clean open tab closes ──
            if (tree.Find(P("renamed.md")) is { } renamed)
            {
                Check(await panel.DeleteAsync(renamed, confirm: false), "delete failed");
                Check(!File.Exists(P("renamed.md")) && tree.Find(P("renamed.md")) is null, "deleted file still there");
                Check(DocumentManager.I.FindByPath(P("renamed.md")) is null, "tab of the deleted file still open");
            }
            if (tree.Find(P("newdir")) is { } newdir)
                Check(await panel.DeleteAsync(newdir, confirm: false) && !Directory.Exists(P("newdir")), "delete folder");

            // ── copy path (never the real clipboard in test mode) ──
            if (tree.Find(P("gamma", "one.cs")) is { } oneNode)
            {
                panel.CopyPath(oneNode, relative: true);
                Check(ExplorerFileOps.LastCopied == Path.Combine("gamma", "one.cs"), "relative path: " + ExplorerFileOps.LastCopied);
            }

            // ── paste into a folder (the clipboard itself is never read in test mode): copy with " (2)", move, picture ──
            if (tree.Find(P("gamma")) is { } gammaNode && (tree.Find(P("Beta")) ?? tree.Find(P("beta"))) is { } betaNode)
            {
                var src = Path.Combine(AppEnv.LocalDir, "explorer-paste-src");
                Directory.CreateDirectory(Path.Combine(src, "sub"));
                File.WriteAllText(Path.Combine(src, "pic.txt"), "a");
                File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "b");
                var copied = await panel.PasteFilesAsync(gammaNode, [Path.Combine(src, "pic.txt"), Path.Combine(src, "sub")], move: false);
                Check(copied.Count == 2 && File.Exists(P("gamma", "pic.txt")) && File.Exists(P("gamma", "sub", "inner.txt")) && File.Exists(Path.Combine(src, "pic.txt")),
                    "paste copies files and folders: " + string.Join("|", copied));
                var again = await panel.PasteFilesAsync(gammaNode, [Path.Combine(src, "pic.txt")], move: false);
                Check(again.Count == 1 && FileUtil.PathEquals(again[0], P("gamma", "pic (2).txt")), "existing name gets (2): " + string.Join("|", again));
                Check(tree.Find(P("gamma", "pic (2).txt")) is { IsSelected: true }, "pasted file not shown and selected");
                var movedPaths = await panel.PasteFilesAsync(betaNode, [P("gamma", "pic (2).txt")], move: true);
                Check(movedPaths.Count == 1 && !File.Exists(P("gamma", "pic (2).txt")) && File.Exists(Path.Combine(betaNode.FullPath, "pic (2).txt")), "cut + paste moves the file");
                var none = await panel.PasteFilesAsync(gammaNode, [P("gamma")], move: false);
                Check(none.Count == 0, "a folder was pasted into itself");
                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
                var png = await panel.PasteImageAsync(gammaNode, bmp);
                Check(png is not null && File.Exists(png) && png.EndsWith(".png", StringComparison.Ordinal), "clipboard picture saved as PNG: " + png);
                Directory.Delete(src, recursive: true);
            }
            else Check(false, "paste: fixture folders missing");

            // ── session: the window's explorer state round-trips, older sessions still load ──
            await Task.Delay(50);
            var file = SessionService.I.Capture();
            var sw = file.Windows.FirstOrDefault(x => x.Id == w.Model.Id);
            Check(sw is { ExplorerVisible: true } && sw.ExplorerRoot == root, "session: root / visible");
            Check(sw?.ExplorerExpanded?.Contains("gamma") == true, "session: expanded " + string.Join("|", sw?.ExplorerExpanded ?? []));
            var back = JsonSerializer.Deserialize<SessionFile>(JsonSerializer.Serialize(file))!.Windows.First(x => x.Id == w.Model.Id);
            var restored = new WindowModel();
            SessionService.ApplyExplorer(back, restored);
            Check(restored.ExplorerRoot == root && restored.ExplorerVisible && Math.Abs(restored.ExplorerWidth - w.Model.ExplorerWidth) < 0.2
                  && restored.ExplorerExpanded.SequenceEqual(w.Model.ExplorerExpanded), "session round trip");
            var legacy = JsonSerializer.Deserialize<SessionWindow>("{\"Id\":\"8d3c2c4e-0000-4000-8000-000000000001\",\"Main\":false}")!;
            var legacyModel = new WindowModel();
            SessionService.ApplyExplorer(legacy, legacyModel);
            Check(legacyModel.ExplorerRoot is null && !legacyModel.ExplorerVisible && legacyModel.ExplorerWidth == WindowModel.DefaultExplorerWidth, "old session defaults");
            Check(!new WindowModel().ExplorerVisible, "a torn-off window must start without the panel");

            // a restored panel opens the remembered folders again
            panel.SetRoot(root, sw?.ExplorerExpanded);
            await panel.Ready;
            tree = panel.Tree!;
            Check(tree.Find(P("gamma")) is { IsExpanded: true } && tree.Find(P("gamma", "nested")) is { IsExpanded: true }, "restored expansion");

            // ── a big folder stays quick (listed off the UI thread, one Reset for the rows) ──
            var big = P("big");
            Directory.CreateDirectory(big);
            for (var i = 0; i < 3000; i++) File.WriteAllText(Path.Combine(big, $"item{i}.txt"), "");
            await panel.RefreshAsync();
            if (tree.Find(big) is { } bigNode)
            {
                var sw2 = Stopwatch.StartNew();
                await panel.ExpandAsync(bigNode);
                sw2.Stop();
                var rows = tree.Rows.Count(r => r.Parent == bigNode);
                Check(rows == 3000, "big folder rows: " + rows);
                Check(sw2.ElapsedMilliseconds < 3000, $"big folder took {sw2.ElapsedMilliseconds} ms");
                var i = tree.Rows.IndexOf(bigNode);
                Check(tree.Rows[i + 1].Name == "item0.txt" && tree.Rows[i + 3].Name == "item2.txt" && tree.Rows[i + 11].Name == "item10.txt", "big folder natural order");
                Log.Info($"selftest: explorer listed 3000 entries in {sw2.ElapsedMilliseconds} ms");
                panel.Collapse(bigNode);
                Check(bigNode.Children is null, "a big collapsed folder keeps its entries in memory");
            }
            else failures.Add("explorer: big folder not listed after refresh");

            // ── close the folder: watcher gone, empty state ──
            var closing = panel.Tree!;
            w.CloseFolder();
            Check(panel.Tree is null && !closing.IsWatching && w.Model.ExplorerRoot is null, "close folder");

            // a folder that is gone (old session): quietly the empty state
            panel.SetRoot(P("does-not-exist"), null, quietIfMissing: true);
            await panel.Ready;
            Check(await WaitFor(() => panel.Tree is null, 2000), "missing folder did not fall back to the empty state");
        }
        catch (Exception ex)
        {
            failures.Add("explorer: exception " + ex);
        }
        finally
        {
            w.CloseFolder();
            w.HideExplorer(animate: false);
            foreach (var d in DocumentManager.I.Documents.ToList())
            {
                if (d.FilePath is not { } p || !ExplorerRules.IsSameOrUnder(root, p)) continue;
                if (d is TextDocument t && t.IsDirty) t.MarkClean();
                if (Workspace.I.Locate(d) is { } where) where.Group.Remove(d);
                DocumentManager.I.Close(d);
            }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
#endif
