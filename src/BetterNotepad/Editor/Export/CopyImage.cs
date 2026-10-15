using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.UI;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Editing;

namespace BetterNotepad.Editor.Export;

/// <summary>
/// "Copy as image" and "Save code as PNG": the selection (or the whole file) as a <see cref="CodeImage"/> card in the
/// active theme's colors (option: always light), at 2×. The clipboard gets PNG with alpha plus an opaque bitmap for
/// older programs; the toast offers "Save as PNG…". No default shortcut (palette + context menu).
/// </summary>
public static class CopyImage
{
    /// <summary>A rendered image ready for the clipboard or a file.</summary>
    internal sealed record Snapshot(BitmapSource Image, byte[] Png, int Lines, int Total, bool Whole, bool Truncated,
        int FirstLine, string FileName, string? Folder, Rect Card);

    /// <summary>The last image made (test mode only; the self-test reads it).</summary>
    internal static Snapshot? LastTest { get; private set; }

    /// <summary>File name the last save would have used (test mode only — no dialog, nothing written).</summary>
    internal static string? LastTestSave { get; private set; }

    public static void Copy(CodeEditor ed)
    {
        if (Make(ed) is not { } snap) return;
        if (!Put(snap)) return;
        var w = RichCopy.MainOf(ed);
        var text = snap.Truncated
            ? Loc.T("copyImage.doneTruncated", snap.Lines, snap.Total)
            : Loc.Plural(snap.Whole ? "copyImage.doneFile" : "copyImage.done", snap.Lines);
        // the toast keeps only the PNG bytes alive, not the bitmap
        var (png, name, folder) = (snap.Png, snap.FileName, snap.Folder);
        w?.Toasts.Show(text, snap.Truncated ? ToastKind.Warning : ToastKind.Success, Loc.T("copyImage.saveAction"), () => SaveFile(png, name, folder, w), seconds: 6);
    }

    public static void Save(CodeEditor ed)
    {
        if (Make(ed) is not { } snap) return;
        var w = RichCopy.MainOf(ed);
        if (snap.Truncated) w?.Toasts.Show(Loc.T("copyImage.truncated", snap.Lines, snap.Total), ToastKind.Warning, seconds: 5);
        SaveFile(snap.Png, snap.FileName, snap.Folder, w);
    }

    /// <summary>Captures and renders; null (after a toast) when there is nothing to draw or rendering failed.</summary>
    internal static Snapshot? Make(CodeEditor ed)
    {
        var w = RichCopy.MainOf(ed);
        var s = SettingsService.I.Get<CopySettings>();
        var palette = s.ImageLightTheme ? Palettes.Daylight : ThemeManager.I.Current;
        var (block, whole, total, first, limited) = Capture(ed, ThemeManager.SyntaxFor(palette));
        if (block.Lines.Count == 0)
        {
            w?.Toasts.Show(Loc.T("copy.empty"), ToastKind.Info);
            return null;
        }

        var model = ed.Model;
        var title = model is not null && (model.FilePath is not null || model.UntitledNumber == 0) ? model.Title : null;
        var language = model is null ? null : Languages.Get(model.LanguageId);
        var editor = SettingsService.I.Get<EditorSettings>();
        try
        {
            var started = Environment.TickCount64;
            var result = CodeImage.Render(block, new CodeImageOptions
            {
                Palette = palette,
                Title = title,
                Language = language is null || language.Id == Languages.PlainText ? null : language.Name,
                LineNumbers = s.ImageLineNumbers,
                FirstLineNumber = first,
                FontFamily = editor.FontFamily,
                FontSize = Math.Clamp(Math.Round(editor.FontSize * 96.0 / 72.0 * 2) / 2, 13, 16),
                Scale = 2,
            });
            var png = CodeImage.Png(result.Image);
            Log.Info($"copy image: {result.Lines} lines → {result.Image.PixelWidth}×{result.Image.PixelHeight} px, {png.Length / 1024} KB, {Environment.TickCount64 - started} ms");
            var snap = new Snapshot(result.Image, png, result.Lines, total, whole, result.Truncated || limited, first,
                FileName(title, language), model?.FilePath is { } path ? Path.GetDirectoryName(path) : null, result.Card);
            if (AppEnv.TestMode) LastTest = snap;
            return snap;
        }
        catch (Exception ex)
        {
            Log.Error("copy image: render failed", ex);
            w?.Toasts.Show(Loc.T("copyImage.renderFailed", ex.Message), ToastKind.Error);
            return null;
        }
    }

    /// <summary>
    /// <see cref="RichCopy.Capture"/> of the selection or the whole file. A huge range is first narrowed to the lines an
    /// image can show (so a 50 MB log is not styled line by line for nothing); the selection is put back right after.
    /// </summary>
    private static (CodeBlock Block, bool Whole, int Total, int FirstLine, bool Limited) Capture(CodeEditor ed,
        IReadOnlyDictionary<SyntaxToken, SyntaxStyle> syntax)
    {
        const int limit = CodeImage.MaxRows + 40;
        var area = ed.TextArea;
        var doc = ed.Document;
        var saved = area.Selection;
        var whole = saved.IsEmpty;
        Selection? narrowed = null;
        var total = 0;
        if (whole && doc.LineCount > limit)
        {
            narrowed = Selection.Create(area, 0, doc.GetLineByNumber(limit).EndOffset);
            total = doc.LineCount;
        }
        else if (!whole && saved is not RectangleSelection && saved.Segments.Count() == 1)
        {
            var seg = saved.SurroundingSegment;
            var a = doc.GetLineByOffset(seg.Offset).LineNumber;
            var b = doc.GetLineByOffset(seg.EndOffset).LineNumber;
            if (b - a + 1 > limit)
            {
                narrowed = Selection.Create(area, seg.Offset, doc.GetLineByNumber(a + limit - 1).EndOffset);
                total = b - a + 1;
            }
        }

        if (narrowed is not null) area.Selection = narrowed;
        try
        {
            var block = RichCopy.Capture(ed, syntax, dedent: true, out _);
            return (block, whole, narrowed is null ? block.Lines.Count : total, FirstLine(ed, block), narrowed is not null);
        }
        finally
        {
            if (narrowed is not null) area.Selection = saved;
        }
    }

    /// <summary>
    /// Number of the first captured line in the file, so the image's line numbers match the editor. Falls back to 1
    /// when the captured lines are not one contiguous range (several carets) or do not add up.
    /// </summary>
    private static int FirstLine(CodeEditor ed, CodeBlock block)
    {
        var doc = ed.Document;
        var sel = ed.TextArea.Selection;
        var rows = new List<(int Line, string Text)>();
        if (sel.IsEmpty)
        {
            for (var l = doc.GetLineByNumber(1); l is not null; l = l.NextLine) rows.Add((l.LineNumber, doc.GetText(l)));
        }
        else if (sel is RectangleSelection)
        {
            foreach (var seg in sel.Segments) rows.Add((doc.GetLineByOffset(seg.StartOffset).LineNumber, doc.GetText(seg)));
        }
        else
        {
            var segments = sel.Segments.ToList();
            if (segments.Count != 1) return 1;
            var (start, end) = (segments[0].StartOffset, segments[0].EndOffset);
            for (var l = doc.GetLineByOffset(start); l is not null && l.Offset <= end; l = l.NextLine)
            {
                if (l.Offset == end && end > start) break; // the selection ends at the start of this line
                var a = Math.Max(start, l.Offset);
                var b = Math.Min(end, l.EndOffset);
                rows.Add((l.LineNumber, doc.GetText(a, b - a)));
            }
        }
        var first = rows.FindIndex(r => !string.IsNullOrWhiteSpace(r.Text));
        var last = rows.FindLastIndex(r => !string.IsNullOrWhiteSpace(r.Text));
        if (first < 0 || rows[last].Line - rows[first].Line + 1 != block.Lines.Count) return 1;
        return rows[first].Line;
    }

    /// <summary>"parser.cs.png"; an untitled tab: "kod.cs.png" / "code.cs.png".</summary>
    private static string FileName(string? title, LanguageInfo? language)
    {
        var name = title ?? Loc.T("copyImage.defaultName") + (language is { IsCode: true } ? language.DefaultExtension : "");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name + ".png";
    }

    /// <summary>PNG (alpha) + an opaque bitmap in one clipboard entry, no text (a chat would paste the text instead). Test mode never touches the clipboard.</summary>
    private static bool Put(Snapshot snap)
    {
        if (AppEnv.TestMode) return true;
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(snap.Png), false);
        data.SetData(DataFormats.Bitmap, CodeImage.Flatten(snap.Image, Colors.White), true);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (ExternalException) when (attempt < 4)
            {
                Thread.Sleep(60); // another program holds the clipboard for a moment
            }
            catch (Exception ex)
            {
                Log.Error("copy image: clipboard unavailable", ex);
                RichCopy.MainOf(null)?.Toasts.Show(Loc.T("copy.clipboardBusy"), ToastKind.Error);
                return false;
            }
        }
    }

    private static void SaveFile(byte[] png, string fileName, string? folder, UI.Windows.MainWindow? w)
    {
        if (AppEnv.TestMode)
        {
            LastTestSave = fileName;
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("copyImage.saveTitle"),
            Filter = Loc.T("copyImage.filter") + " (*.png)|*.png",
            FileName = fileName,
            DefaultExt = ".png",
            AddExtension = true,
        };
        if (folder is not null && Directory.Exists(folder)) dlg.InitialDirectory = folder;
        if (dlg.ShowDialog(w) != true) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, png);
            var saved = dlg.FileName;
            w?.Toasts.Show(Loc.T("copyImage.saved", Path.GetFileName(saved)), ToastKind.Success, Loc.T("copyImage.openFolder"), () => Reveal(saved), seconds: 5);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("copy image: save failed: " + ex.Message);
            w?.Toasts.Show(Loc.T("copyImage.saveFailed", ex.Message), ToastKind.Error);
        }
    }

    private static void Reveal(string file)
    {
        if (AppEnv.TestMode) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("copy image: reveal: " + ex.Message); }
    }
}
