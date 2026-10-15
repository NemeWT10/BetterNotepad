using System.Globalization;
using System.IO;

namespace BetterNotepad.Modules.Explorer;

/// <summary>Icon family of a tree entry (the panel maps it to a glyph and a subtle tint).</summary>
public enum FileIconKind
{
    Folder,
    File,
    Text,
    Markdown,
    Code,
    Markup,
    Style,
    Data,
    Config,
    Table,
    Script,
    Image,
    Pdf,
    Document,
    Archive,
    Binary,
    Audio,
    Video,
    Font,
}

/// <summary>Why a typed file or folder name cannot be used (the existence check is the caller's: it needs the disk).</summary>
public enum NameIssue
{
    None,
    Empty,
    DotName,
    InvalidCharacters,
    ReservedName,
    TrailingDotOrSpace,
    TooLong,
}

/// <summary>
/// Pure rules of the folder tree (no WPF, no disk access): what is shown, in which order, names, paths, type-ahead
/// and which loaded folders a batch of watcher events touches. Linked into the unit tests.
/// </summary>
public static class ExplorerRules
{
    /// <summary>Version-control folders are never listed (like VS Code's default files.exclude).</summary>
    public static bool IsVcsFolder(ReadOnlySpan<char> name)
        => name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".svn", StringComparison.OrdinalIgnoreCase)
           || name.Equals(".hg", StringComparison.OrdinalIgnoreCase);

    /// <summary>An entry shows unless it is hidden / a system file or a VCS folder.</summary>
    public static bool ShouldShow(ReadOnlySpan<char> name, FileAttributes attributes, bool isDirectory)
    {
        if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
        return !(isDirectory && IsVcsFolder(name));
    }

    /// <summary>Watcher noise from inside a VCS folder (every git command touches .git): never shown, never refreshed.</summary>
    public static bool IsInsideVcsFolder(string path)
    {
        foreach (var vcs in VcsSegments)
            for (var i = path.IndexOf(vcs, StringComparison.OrdinalIgnoreCase); i >= 0; i = path.IndexOf(vcs, i + 1, StringComparison.OrdinalIgnoreCase))
                if (i + vcs.Length == path.Length || path[i + vcs.Length] is '\\' or '/') return true;
        return false;
    }

    private static readonly string[] VcsSegments = [@"\.git", @"\.svn", @"\.hg"];

    // ───────────── order ─────────────

    private static readonly CompareInfo Collation = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Natural order, case-insensitive: "file2" &lt; "file10", "a" &lt; "ą" &lt; "B"; a total order (ordinal tiebreak).</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        int i = 0, j = 0, tie = 0;
        while (i < a.Length && j < b.Length)
        {
            var da = char.IsAsciiDigit(a[i]);
            var db = char.IsAsciiDigit(b[j]);
            if (da != db) return da ? -1 : 1; // digits before letters (punctuation runs are handled below)
            int si = i, sj = j;
            if (da)
            {
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length < nb.Length ? -1 : 1;
                var c = na.SequenceCompareTo(nb);
                if (c != 0) return Math.Sign(c);
                if (tie == 0 && i - si != j - sj) tie = i - si < j - sj ? -1 : 1; // "7" before "007"
                continue;
            }

            while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
            while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;
            var ra = a.AsSpan(si, i - si);
            var rb = b.AsSpan(sj, j - sj);
            var cmp = Collation.Compare(ra, rb, CompareOptions.IgnoreCase);
            if (cmp == 0) continue;
            if (ra.Length != rb.Length)
            {
                // "file.txt" vs "file1.txt": the shorter run is a prefix that a number follows; the longer run's next
                // character decides (punctuation and spaces sort before digits, letters after them)
                var aShort = ra.Length < rb.Length;
                var shortRun = aShort ? ra : rb;
                var longRun = aShort ? rb : ra;
                var numberFollows = aShort ? i < a.Length : j < b.Length;
                if (numberFollows && longRun.StartsWith(shortRun, StringComparison.OrdinalIgnoreCase))
                {
                    var shortFirst = char.IsLetter(longRun[shortRun.Length]);
                    return aShort == shortFirst ? -1 : 1;
                }
            }
            return Math.Sign(cmp);
        }
        if (i < a.Length) return 1;
        if (j < b.Length) return -1;
        if (tie != 0) return tie;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    /// <summary>Folders first, then files; each in natural order.</summary>
    public static int CompareEntries(string nameA, bool dirA, string nameB, bool dirB)
        => dirA != dirB ? (dirA ? -1 : 1) : NaturalCompare(nameA, nameB);

    // ───────────── paths ─────────────

    /// <summary>Full path without a trailing separator ("C:\" stays a root).</summary>
    public static string NormalizeDir(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && full.Length <= root.Length) return root;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it (case-insensitive, whole segments).</summary>
    public static bool IsSameOrUnder(string folder, string path)
    {
        var f = folder.TrimEnd('\\', '/');
        var p = path.TrimEnd('\\', '/');
        if (f.Length == 0 || p.Length < f.Length || !p.StartsWith(f, StringComparison.OrdinalIgnoreCase)) return false;
        return p.Length == f.Length || p[f.Length] is '\\' or '/';
    }

    /// <summary>The path moved along with a renamed folder (or file): null when it is not under <paramref name="oldBase"/>.</summary>
    public static string? Rebase(string path, string oldBase, string newBase)
    {
        if (!IsSameOrUnder(oldBase, path)) return null;
        var rest = path.TrimEnd('\\', '/')[oldBase.TrimEnd('\\', '/').Length..].TrimStart('\\', '/');
        return rest.Length == 0 ? newBase : Path.Combine(newBase, rest);
    }

    /// <summary>Path relative to the open folder ("src\app.cs"); the folder itself is ".".</summary>
    public static string RelativePath(string root, string path) => Path.GetRelativePath(root, path);

    /// <summary>Name shown for the open folder: its own name, or the drive ("D:\").</summary>
    public static string DisplayName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    // ───────────── names ─────────────

    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Windows rules for one path segment typed in the tree (no separators: one level at a time).</summary>
    public static NameIssue ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return NameIssue.Empty;
        if (name is "." or "..") return NameIssue.DotName;
        foreach (var ch in name)
            if (ch < 32 || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
                return NameIssue.InvalidCharacters;
        if (name[^1] is '.' or ' ') return NameIssue.TrailingDotOrSpace;
        if (name.Length > 255) return NameIssue.TooLong;
        var stem = name.Split('.')[0].TrimEnd(' ');
        foreach (var r in Reserved)
            if (stem.Equals(r, StringComparison.OrdinalIgnoreCase)) return NameIssue.ReservedName;
        return NameIssue.None;
    }

    /// <summary>Selected part of a name in the inline rename box: a file's name without its extension, a folder's whole name.</summary>
    public static (int Start, int Length) RenameSelection(string name, bool isDirectory)
    {
        if (isDirectory) return (0, name.Length);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? (0, dot) : (0, name.Length);
    }

    // ───────────── watcher batches ─────────────

    /// <summary>
    /// The loaded folders whose listing a batch of changed paths can affect: the parent of every changed entry
    /// (created, deleted, renamed — old and new names). Folders that are not loaded are skipped (they list when expanded).
    /// </summary>
    public static HashSet<string> FoldersToRefresh(IEnumerable<string> changedPaths, Func<string, bool> isLoaded)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in changedPaths)
        {
            var trimmed = p.TrimEnd('\\', '/');
            var parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(parent) && isLoaded(parent)) result.Add(parent);
        }
        return result;
    }

    // ───────────── type-ahead, session ─────────────

    /// <summary>Next row (from <paramref name="start"/>, wrapping) whose name starts with <paramref name="prefix"/>; -1 when none.</summary>
    public static int FindByPrefix(IReadOnlyList<string?> names, int start, string prefix)
    {
        if (names.Count == 0 || prefix.Length == 0) return -1;
        start = ((start % names.Count) + names.Count) % names.Count;
        for (var k = 0; k < names.Count; k++)
        {
            var i = (start + k) % names.Count;
            if (names[i] is { } n && n.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>Distinct entries in their order, at most <paramref name="max"/> (the session keeps a bounded list).</summary>
    public static List<string> Cap(IEnumerable<string> items, int max)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var i in items)
        {
            if (list.Count >= max) break;
            if (!string.IsNullOrWhiteSpace(i) && seen.Add(i)) list.Add(i);
        }
        return list;
    }

    // ───────────── icons ─────────────

    private static readonly Dictionary<string, FileIconKind> ByExtension = Build();

    private static Dictionary<string, FileIconKind> Build()
    {
        var map = new Dictionary<string, FileIconKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileIconKind kind, string list)
        {
            foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map["." + e] = kind;
        }
        Add(FileIconKind.Code, "cs csx vb fs fsx c h cpp cc cxx hpp hh hxx ino java kt kts scala groovy go rs py pyw pyi rb php js mjs cjs jsx ts mts cts tsx " +
                               "lua swift dart r pl pm asm s inc nasm sql m mm zig hs ex exs erl clj v vhd vhdl sv d nim jl f90 pas");
        Add(FileIconKind.Markup, "html htm xhtml xml xaml axaml vue svelte razor cshtml jsp csproj vbproj fsproj vcxproj props targets resx nuspec manifest plist");
        Add(FileIconKind.Style, "css scss sass less styl");
        Add(FileIconKind.Data, "json jsonc json5 yaml yml toml sln lock");
        Add(FileIconKind.Config, "ini cfg conf config env properties editorconfig gitignore gitattributes gitmodules npmrc prettierrc eslintrc reg");
        Add(FileIconKind.Table, "csv tsv tab");
        Add(FileIconKind.Script, "ps1 psm1 psd1 bat cmd sh bash zsh fish vbs");
        Add(FileIconKind.Markdown, "md markdown mdx rst adoc");
        Add(FileIconKind.Text, "txt text log nfo diff patch srt");
        Add(FileIconKind.Image, "png jpg jpeg jfif gif bmp ico cur tif tiff webp avif heic svg psd");
        Add(FileIconKind.Pdf, "pdf");
        Add(FileIconKind.Document, "doc docx docm dot dotx odt rtf xls xlsx xlsm ods ppt pptx pptm odp epub");
        Add(FileIconKind.Archive, "zip 7z rar tar gz tgz bz2 xz zst cab iso nupkg jar war apk vsix whl");
        Add(FileIconKind.Binary, "exe dll bin obj o so dylib pdb lib a class pyc wasm dat db sqlite msi sys hex elf img");
        Add(FileIconKind.Audio, "mp3 wav flac ogg oga m4a aac wma mid midi opus");
        Add(FileIconKind.Video, "mp4 mkv avi mov webm wmv m4v mpg mpeg");
        Add(FileIconKind.Font, "ttf otf woff woff2 fon");
        return map;
    }

    public static FileIconKind IconFor(string name, bool isDirectory)
    {
        if (isDirectory) return FileIconKind.Folder;
        var ext = Path.GetExtension(name);
        if (ext.Length == 0)
        {
            // LICENSE, README, Makefile, Dockerfile…
            return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
                ? FileIconKind.Script : FileIconKind.Text;
        }
        // dot files without another dot (".gitignore", ".env") are named by their "extension"
        return ByExtension.TryGetValue(ext, out var kind) ? kind : FileIconKind.File;
    }
}
