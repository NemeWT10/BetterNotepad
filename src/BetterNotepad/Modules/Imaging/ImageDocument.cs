using System.IO;
using System.Windows.Media.Imaging;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Imaging;
using BetterNotepad.UI;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Imaging;

/// <summary>
/// An image tab. The edits live here, shared by every view of the picture (a split shows the same edits): the tab is unsaved
/// while they differ from the file, Ctrl+S writes the picture with them — pixels only, EXIF / GPS dropped, the orientation in
/// the pixels. From the first edit on, the file as it was is kept as a byte copy next to the session backups (the "base"):
/// the edits always apply to it, so another program changing the file meanwhile breaks nothing, undo keeps working after a
/// save, and the edit list (with undo) comes back after a restart — like every unsaved text in this app.
/// </summary>
public sealed class ImageDocument : Document, ISaveableDocument
{
    /// <summary>Files up to this size are copied as the base right at the first edit; larger ones in the background.</summary>
    private const long SyncCopyLimit = 24L << 20;

    private readonly Debouncer _journal;
    private readonly List<ImageDocumentView> _views = [];
    private bool _forcedDirty;
    private long _diskLength;
    private DateTime _diskWriteUtc;
    private string? _basePath;
    private bool _baseCopying;
    private int _baseGeneration;
    private bool _disposed;

    public ImageDocument(string path, Guid? id = null) : base(id)
    {
        FilePath = path;
        IsLoaded = true;
        _journal = new Debouncer(TimeSpan.FromMilliseconds(300), WriteJournal);
        RefreshDiskStamp();
        RestoreBackup();
        Edits.Changed += OnEditsChanged;
        Edits.SavedChanged += OnSavedChanged;
    }

    public override DocumentKind Kind => DocumentKind.Image;

    public override bool IsDirty => Edits.IsModified || _forcedDirty || External != ExternalChange.None;

    public override bool CanSave => true;

    /// <summary>The edit list (crop, drawings…) with undo / redo, shown by every view of this picture.</summary>
    public EditDocument Edits { get; } = new();

    /// <summary>Where the pixels come from: the base copy once editing began, else the file itself.</summary>
    public string SourcePath => _basePath ?? FilePath!;

    /// <summary>The base copy (null while there is none).</summary>
    internal string? BasePath => _basePath;

    /// <summary>Size of the file on disk (status bar).</summary>
    public long FileLength => _diskLength;

    /// <summary>The file was read again (the edits dropped): views decode it anew.</summary>
    public event Action? Reloaded;

    /// <summary>The base copy is ready (the same picture): views read their pixels from it from now on.</summary>
    public event Action? SourceChanged;

    /// <summary>The file on disk changed its size or name (saved): the status bar.</summary>
    public event Action? FileChanged;

    internal void Attach(ImageDocumentView v) => _views.Add(v);

    internal void Detach(ImageDocumentView v) => _views.Remove(v);

    /// <summary>Journal writes still waiting (shutdown flushes them).</summary>
    internal void FlushBackup() => _journal.Flush();

    // ───────────── edits → dirty, base copy, backup ─────────────

    private void OnEditsChanged()
    {
        if (Edits.HasHistory) EnsureBase();
        RaiseDirtyChanged();
        _journal.Trigger();
    }

    private void OnSavedChanged()
    {
        RaiseDirtyChanged();
        _journal.Trigger();
    }

    /// <summary>The first edit: keep the file as it is now, so the edits stay valid whatever happens to the file.</summary>
    private void EnsureBase()
    {
        if (_basePath is not null || _baseCopying || FilePath is not { } file || !File.Exists(file)) return;
        long length;
        try
        {
            length = new FileInfo(file).Length;
        }
        catch
        {
            return;
        }
        var generation = _baseGeneration;
        if (length <= SyncCopyLimit)
        {
            if (ImageBackup.CopyBase(file, Id)) UseBase();
            return;
        }
        _baseCopying = true;
        Task.Run(() => ImageBackup.CopyBase(file, Id)).ContinueWith(t =>
        {
            _baseCopying = false;
            if (t.Result && generation == _baseGeneration && !_disposed && Edits.HasHistory) UseBase();
            else if (t.Result && (generation != _baseGeneration || _disposed)) ImageBackup.DeleteBase(Id);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UseBase()
    {
        _basePath = ImageBackup.BaseFile(Id);
        SourceChanged?.Invoke();
        _journal.Trigger();
    }

    private void DropBase()
    {
        _baseGeneration++;
        if (_basePath is null) return;
        _basePath = null;
        ImageBackup.DeleteBase(Id);
    }

    private void BackupNow()
    {
        _journal.Cancel();
        WriteJournal();
    }

    /// <summary>Unsaved edits (or a kept deleted file) on disk; nothing to keep → the journal goes.</summary>
    private void WriteJournal()
    {
        if (_disposed || FilePath is null) return;
        if (!Edits.IsModified && !_forcedDirty)
        {
            ImageBackup.DeleteJournal(Id);
            return;
        }
        try
        {
            ImageBackup.Write(Id, new ImageJournal(FilePath, _basePath is not null, _diskLength, _diskWriteUtc, _forcedDirty, Edits.Capture()));
        }
        catch (Exception ex)
        {
            Log.Error($"image backup: write failed for {Id}", ex);
        }
    }

    /// <summary>A session restore: the unsaved edits of last time (and the stamp of the file as edited, so a change is noticed).</summary>
    private void RestoreBackup()
    {
        var j = ImageBackup.Read(Id);
        if (j is null)
        {
            ImageBackup.DeleteBase(Id); // a base without its journal is of no use
            return;
        }
        try
        {
            Edits.Restore(j.Edits);
        }
        catch (Exception ex)
        {
            Log.Warn($"image backup: cannot restore the edits of {FilePath}: {ex.Message}");
            ImageBackup.Delete(Id);
            return;
        }
        if (j.HasBase && File.Exists(ImageBackup.BaseFile(Id))) _basePath = ImageBackup.BaseFile(Id);
        _diskLength = j.Length;
        _diskWriteUtc = j.WriteUtc;
        _forcedDirty = j.Forced;
        Log.Info($"image: restored {j.Edits.Current.Count} unsaved edits of {Path.GetFileName(FilePath)}");
    }

    // ───────────── changes on disk ─────────────

    public void RefreshDiskStamp()
    {
        try
        {
            if (FilePath is null || !File.Exists(FilePath)) return;
            var fi = new FileInfo(FilePath);
            _diskLength = fi.Length;
            _diskWriteUtc = fi.LastWriteTimeUtc;
        }
        catch
        {
            // keep the old stamp
        }
        FileChanged?.Invoke();
    }

    public bool DiskDiffers(out bool deleted)
    {
        deleted = false;
        if (FilePath is null) return false;
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists)
            {
                deleted = true;
                return true;
            }
            return fi.Length != _diskLength || fi.LastWriteTimeUtc != _diskWriteUtc;
        }
        catch
        {
            return false;
        }
    }

    public Task ReloadAsync()
    {
        _forcedDirty = false;
        External = ExternalChange.None;
        Edits.Reset();
        DropBase();
        RefreshDiskStamp();
        RaiseDirtyChanged();
        BackupNow();
        Reloaded?.Invoke();
        return Task.CompletedTask;
    }

    public void KeepMine()
    {
        var deleted = External == ExternalChange.Deleted || FilePath is { } p && !File.Exists(p);
        if (deleted && _basePath is null) KeepPictureInMemory();
        RefreshDiskStamp();
        External = ExternalChange.None;
        if (deleted) _forcedDirty = true; // saving writes the file again
        RaiseDirtyChanged();
        BackupNow();
    }

    /// <summary>The file is gone and nothing was edited yet: the decoded picture of a view becomes the base.</summary>
    private void KeepPictureInMemory()
    {
        foreach (var v in _views)
        {
            if (v.CaptureBase() is not { } picture) continue;
            try
            {
                Directory.CreateDirectory(AppEnv.BackupDir);
                var target = ImageBackup.BaseFile(Id);
                using (var fs = File.Create(target))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(picture));
                    enc.Save(fs);
                }
                UseBase();
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"image: keeping {FilePath} in memory failed: {ex.Message}");
            }
        }
    }

    // ───────────── save ─────────────

    /// <summary>Formats offered by "Save as" (in this order), the ones Windows can write.</summary>
    internal static IReadOnlyList<ImageFileFormat> SaveFormats() =>
        new[] { ImageFileFormat.Png, ImageFileFormat.Jpeg, ImageFileFormat.Webp, ImageFileFormat.Bmp, ImageFileFormat.Gif, ImageFileFormat.Tiff, ImageFileFormat.Ico }
            .Where(ImageCodecs.CanEncode).ToArray();

    internal static string FormatName(ImageFileFormat f) => f switch
    {
        ImageFileFormat.Jpeg => "JPEG",
        ImageFileFormat.Webp => "WebP",
        ImageFileFormat.Ico => Loc.T("img.fmt.ico") + " (ICO)",
        _ => f.ToString().ToUpperInvariant(),
    };

    public (string FileName, string Filter, int FilterIndex) SaveAsDialog()
    {
        var formats = SaveFormats();
        var current = ImageCodecs.FromExtension(FilePath ?? "") is { } f && formats.Contains(f) ? f : ImageFileFormat.Png;
        var filter = string.Join("|", formats.Select(x =>
        {
            var patterns = string.Join(";", ImageCodecs.Extensions(x).Select(e => "*" + e));
            return $"{FormatName(x)} ({patterns})|{patterns}";
        }));
        var stem = Path.GetFileNameWithoutExtension(FilePath ?? Title);
        var keepExt = ImageCodecs.FromExtension(FilePath ?? "") == current ? Path.GetExtension(FilePath) : ImageCodecs.Extension(current);
        return (stem + keepExt, filter, formats.ToList().IndexOf(current) + 1);
    }

    public async Task<bool> SaveToAsync(string path, int filterIndex = 0)
    {
        if (FilePath is null) return false;
        var w = WindowOf();
        var formats = SaveFormats();
        // the format: the name's extension when it is one we write, else the filter picked (its extension added)
        ImageFileFormat format;
        if (ImageCodecs.FromExtension(path) is { } byName && formats.Contains(byName))
        {
            format = byName;
        }
        else if (filterIndex >= 1 && filterIndex <= formats.Count)
        {
            format = formats[filterIndex - 1];
            // "photo.webp" with no WebP encoder, PNG picked → "photo.png"; "notes.v2" → "notes.v2.png"
            path = ImageCodecs.FromExtension(path) is not null ? Path.ChangeExtension(path, ImageCodecs.Extension(format)) : path + ImageCodecs.Extension(format);
        }
        else
        {
            var what = ImageCodecs.FromExtension(path) is { } f ? FormatName(f) : Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            w?.Toasts.Show(Loc.T("img.save.cantWrite", what), ToastKind.Warning, Loc.T("cmd.file.saveAs"), () => _ = w.SaveAsync(this, saveAs: true));
            return false;
        }

        var same = FileUtil.PathEquals(path, FilePath);
        if (same && !IsDirty) return true; // nothing changed: the file already holds it
        var source = SourcePath;
        if (!File.Exists(source))
        {
            w?.Toasts.Show(Loc.T("img.save.noSource"), ToastKind.Error);
            return false;
        }
        ImageInfo? info = null;
        try
        {
            info = await ImageExport.ProbeAsync(source);
        }
        catch (Exception ex)
        {
            Log.Warn($"image: probe {source}: {ex.Message}");
        }
        var ops = Edits.Ops;
        // no edits, same format: the file's own bytes (lossless, its metadata kept — nothing was edited)
        var copy = ops.Count == 0 && info is not null && ImageCodecs.FromFormatName(info.Format) == format;
        if (copy)
        {
            if (!FileUtil.PathEquals(source, path)) await Task.Run(() => CopyAtomic(source, path));
        }
        else
        {
            if (same && info is { Frames: > 1 } && w is not null)
            {
                var answer = await w.DialogLayer.Show(Loc.T("img.save.framesTitle"), Loc.T("img.save.framesText", Title),
                [
                    new DialogButton("saveas", Loc.T("img.save.framesSaveAs"), DialogButtonKind.Primary, IsDefault: true),
                    new DialogButton("one", Loc.T("img.save.framesOne")),
                    new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
                ], glyph: BetterNotepad.UI.Controls.Glyphs.Warning);
                if (answer == "saveas")
                {
                    _ = w.Dispatcher.BeginInvoke(() => _ = w.SaveAsync(this, saveAs: true));
                    return false;
                }
                if (answer != "one") return false;
            }
            var settings = SettingsService.I.Get<ImagingSettings>();
            var quality = settings.JpegQuality;
            if (format == ImageFileFormat.Jpeg && !same && w is not null && !AppEnv.TestMode)
            {
                if (await ImagingDialogs.AskJpegQuality(w, quality) is not { } q) return false;
                quality = settings.JpegQuality = q;
            }
            await ImageExport.SaveAsync(source, ops, path, format, quality);
            if (format == ImageFileFormat.Ico) ReportIconSize(w, path);
        }

        if (!same) ChangePath(path);
        Edits.MarkSaved();
        _forcedDirty = false;
        External = ExternalChange.None;
        RefreshDiskStamp();
        RaiseDirtyChanged();
        BackupNow();
        if (!copy && info is { HasMetadata: true }) ImagingDialogs.MetadataNoticeOnce(w);
        return true;
    }

    /// <summary>Test hook: <see cref="SaveToAsync"/> with an explicit JPEG quality (no dialog).</summary>
    internal async Task<bool> SaveWithQualityAsync(string path, int quality)
    {
        var s = SettingsService.I.Get<ImagingSettings>();
        var before = s.JpegQuality;
        s.JpegQuality = quality;
        try
        {
            return await SaveToAsync(path);
        }
        finally
        {
            s.JpegQuality = before;
        }
    }

    private static void ReportIconSize(MainWindow? w, string path)
    {
        try
        {
            var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth >= ImageCodecs.MaxIconSide || frame.PixelHeight >= ImageCodecs.MaxIconSide)
                w?.Toasts.Show(Loc.T("img.save.iconScaled", frame.PixelWidth, frame.PixelHeight), ToastKind.Info, seconds: 4);
        }
        catch
        {
            // the toast is only a courtesy
        }
    }

    private static void CopyAtomic(string from, string to)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(to))!, "." + Path.GetFileName(to) + ".bn-tmp");
        try
        {
            File.Copy(from, tmp, overwrite: true);
            File.Move(tmp, to, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    internal MainWindow? WindowOf() =>
        Workspace.I.Locate(this) is { } l && App.Current?.WindowFor(l.Window) is { } w ? w : App.Current?.MainAppWindow;

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _journal.Cancel();
        Edits.Changed -= OnEditsChanged;
        Edits.SavedChanged -= OnSavedChanged;
        _baseGeneration++;
        ImageBackup.Delete(Id); // closed: the user chose (save or discard)
    }
}
