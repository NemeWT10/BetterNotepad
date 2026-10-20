# BetterNotepad

BetterNotepad is a fast, light notepad and code editor for Windows 11. It remembers everything you were working on and handles code, Markdown, PDF and binary files in one tabbed window. It ships as a single self-contained `.exe` and makes no network connections except an optional check for updates.

## Install

1. Download `BetterNotepad.exe` from [Releases](https://github.com/NemeWT10/BetterNotepad/releases).
2. Run it and choose **Install** (per-user, no administrator rights) or **Run without installing** to use it as a portable app.

The executable is not code-signed, so Windows SmartScreen may show a warning. Choose **More info**, then **Run anyway**.

## Features

- **Tabs and sessions:** tabs with pinning and reordering; the whole session, including unsaved changes and untitled tabs, is restored after a restart.
- **Folder tree:** open a folder in the Explorer side panel (Ctrl+Shift+E) to browse, open, create, rename and delete files, or paste files and pictures from the clipboard; it follows the active tab and updates live.
- **Syntax highlighting:** 41 languages with per-theme palettes and language detection from content.
- **Editing:** multi-cursor (next occurrence, all occurrences, cursors above and below), line move/duplicate/delete, code folding, bookmarks, bracket matching, auto-indent and as-you-type completion.
- **Copy for documents:** paste code with syntax colors into Word or Google Docs (Ctrl+Shift+C), copy it as a thesis-style listing with a numbered caption, or copy it as a PNG image.
- **Find and replace:** incremental search with regex, case and whole-word options.
- **Command palette:** every command and quick file opening from the keyboard, with remappable shortcuts.
- **Markdown:** live preview with a table of contents, split or single view, and clickable task lists.
- **HTML and SVG:** live preview next to the code with local styles, scripts and images; content from the internet is blocked unless you allow it.
- **Images:** view (zoom, animated GIF, rotate, save as another format) and make simple edits such as crop, shapes, text, pen and blur; saving an edited image removes EXIF/GPS data.
- **CSV and TSV:** a table view with separator and header detection, sorting, filtering and cell editing that writes straight into the file.
- **PDF viewer:** thumbnails, zoom, text search, text selection across pages, password-protected files, printing, and conversion to Word.
- **Pin on top:** keep any tab in a compact always-on-top window, with a translucent click-through ghost mode (Win+Shift+G).
- **Hex view:** read-only hex view for binary files.
- **File info:** size, dates, SHA-256 and MD5 checksums, and metadata viewing and cleaning for images, PDF and Office files.
- **Themes:** Aurora, Graphite, Daylight, Neon and Sunset, or follow the system setting.
- **System integration:** tray icon, global hotkey (Win+Shift+N by default), start with Windows, single instance, and file associations with an "Open in BetterNotepad" context-menu entry.
- **Updates:** optional automatic update from GitHub Releases, with SHA-256 verification.
- **Languages:** Polish and English interface.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
dotnet build BetterNotepad.sln
dotnet test tests\BetterNotepad.Tests
dotnet publish src\BetterNotepad -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o artifacts\publish
```

## License

[MIT](LICENSE)
