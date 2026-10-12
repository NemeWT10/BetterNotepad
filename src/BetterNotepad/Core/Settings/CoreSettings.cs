namespace BetterNotepad.Core.Settings;

public sealed class AppearanceSettings : SettingsSection, ISettingsSection
{
    public static string Key => "appearance";

    private string _theme = "system";
    private string _language = "system";
    private bool _animations = true;
    private bool _mica = true;

    /// <summary>"system" or a theme id (aurora, graphite, daylight, neon, sunset).</summary>
    public string Theme { get => _theme; set => Set(ref _theme, value); }

    /// <summary>"system", "pl" or "en".</summary>
    public string Language { get => _language; set => Set(ref _language, value); }

    public bool Animations { get => _animations; set => Set(ref _animations, value); }

    /// <summary>Windows 11 Mica backdrop behind the chrome.</summary>
    public bool Mica { get => _mica; set => Set(ref _mica, value); }
}

public sealed class EditorSettings : SettingsSection, ISettingsSection
{
    public static string Key => "editor";

    private string _fontFamily = "Cascadia Mono";
    private double _fontSize = 11;
    private double _zoom = 1.0;
    private int _tabSize = 4;
    private bool _insertSpaces = true;
    private bool _wordWrapCode;
    private bool _wordWrapText = true;
    private bool _lineNumbersCode = true;
    private bool _lineNumbersText;
    private bool _highlightCurrentLine = true;
    private bool _showWhitespace;
    private bool _indentGuides = true;
    private bool _autoCloseBrackets = true;
    private bool _highlightOccurrences = true;
    private bool _colorPreview = true;
    private bool _completion = true;
    private int _completionChars = 2;
    private bool _spellCheck = true;
    private bool _folding = true;
    private string _minimapMode = "auto";
    private double _minimapThresholdScreens = 3;

    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }

    /// <summary>In points.</summary>
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 6, 48)); }

    public double Zoom { get => _zoom; set => Set(ref _zoom, Math.Clamp(value, 0.5, 4)); }
    public int TabSize { get => _tabSize; set => Set(ref _tabSize, Math.Clamp(value, 1, 16)); }
    public bool InsertSpaces { get => _insertSpaces; set => Set(ref _insertSpaces, value); }
    public bool WordWrapCode { get => _wordWrapCode; set => Set(ref _wordWrapCode, value); }
    public bool WordWrapText { get => _wordWrapText; set => Set(ref _wordWrapText, value); }
    public bool LineNumbersCode { get => _lineNumbersCode; set => Set(ref _lineNumbersCode, value); }
    public bool LineNumbersText { get => _lineNumbersText; set => Set(ref _lineNumbersText, value); }
    public bool HighlightCurrentLine { get => _highlightCurrentLine; set => Set(ref _highlightCurrentLine, value); }
    public bool ShowWhitespace { get => _showWhitespace; set => Set(ref _showWhitespace, value); }
    public bool IndentGuides { get => _indentGuides; set => Set(ref _indentGuides, value); }
    public bool AutoCloseBrackets { get => _autoCloseBrackets; set => Set(ref _autoCloseBrackets, value); }
    public bool HighlightOccurrences { get => _highlightOccurrences; set => Set(ref _highlightOccurrences, value); }
    public bool ColorPreview { get => _colorPreview; set => Set(ref _colorPreview, value); }
    public bool Completion { get => _completion; set => Set(ref _completion, value); }

    /// <summary>Letters typed before suggestions open on their own (Notepad++ style); 0 = only Ctrl+Space.</summary>
    public int CompletionChars { get => _completionChars; set => Set(ref _completionChars, Math.Clamp(value, 0, 9)); }
    public bool SpellCheck { get => _spellCheck; set => Set(ref _spellCheck, value); }
    public bool Folding { get => _folding; set => Set(ref _folding, value); }

    /// <summary>"auto", "always", "never".</summary>
    public string MinimapMode { get => _minimapMode; set => Set(ref _minimapMode, value); }

    public double MinimapThresholdScreens { get => _minimapThresholdScreens; set => Set(ref _minimapThresholdScreens, Math.Clamp(value, 1, 50)); }
}

public sealed class FilesSettings : SettingsSection, ISettingsSection
{
    public static string Key => "files";

    private string _defaultEncoding = "utf-8";
    private bool _defaultBom;
    private string _defaultEol = "CRLF";
    private int _largeFileMb = 50;
    private bool _autosave;
    private int _autosaveSeconds = 30;
    private bool _restoreSession = true;
    private string? _lastFolder;
    private bool _previewOnLeft = true;
    private Dictionary<string, string> _defaultViews = new();

    public string DefaultEncoding { get => _defaultEncoding; set => Set(ref _defaultEncoding, value); }
    public bool DefaultBom { get => _defaultBom; set => Set(ref _defaultBom, value); }

    /// <summary>"CRLF" or "LF".</summary>
    public string DefaultEol { get => _defaultEol; set => Set(ref _defaultEol, value); }

    public int LargeFileMb { get => _largeFileMb; set => Set(ref _largeFileMb, Math.Clamp(value, 5, 2048)); }
    public bool Autosave { get => _autosave; set => Set(ref _autosave, value); }
    public int AutosaveSeconds { get => _autosaveSeconds; set => Set(ref _autosaveSeconds, Math.Clamp(value, 2, 3600)); }
    public bool RestoreSession { get => _restoreSession; set => Set(ref _restoreSession, value); }
    public string? LastFolder { get => _lastFolder; set => Set(ref _lastFolder, value); }

    /// <summary>Markdown/HTML layout: preview on the left (user's sketch) or on the right.</summary>
    public bool PreviewOnLeft { get => _previewOnLeft; set => Set(ref _previewOnLeft, value); }

    /// <summary>Default view per document kind: "split", "preview", "edit" (CSV: "table" / "text").</summary>
    public Dictionary<string, string> DefaultViews { get => _defaultViews; set => Set(ref _defaultViews, value); }
}

public sealed class SystemSettings : SettingsSection, ISettingsSection
{
    public static string Key => "system";

    private bool _autostart;
    private bool _trayEnabled = true;
    private bool _closeToTray = true;
    private bool _closeToTrayHintShown;
    private bool _globalHotkeyEnabled = true;
    private string _globalHotkey = "Win+Shift+N";
    private string _ghostHotkey = "Win+Shift+G";
    private bool _installPromptShown;

    public bool Autostart { get => _autostart; set => Set(ref _autostart, value); }
    public bool TrayEnabled { get => _trayEnabled; set => Set(ref _trayEnabled, value); }
    public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }
    public bool CloseToTrayHintShown { get => _closeToTrayHintShown; set => Set(ref _closeToTrayHintShown, value); }
    public bool GlobalHotkeyEnabled { get => _globalHotkeyEnabled; set => Set(ref _globalHotkeyEnabled, value); }
    public string GlobalHotkey { get => _globalHotkey; set => Set(ref _globalHotkey, value); }
    public string GhostHotkey { get => _ghostHotkey; set => Set(ref _ghostHotkey, value); }
    public bool InstallPromptShown { get => _installPromptShown; set => Set(ref _installPromptShown, value); }
}

public sealed class UpdateSettings : SettingsSection, ISettingsSection
{
    public static string Key => "updates";

    private string _mode = "ask";
    private DateTime? _lastCheckUtc;
    private string? _skippedVersion;

    /// <summary>"auto", "ask", "off".</summary>
    public string Mode { get => _mode; set => Set(ref _mode, value); }

    public DateTime? LastCheckUtc { get => _lastCheckUtc; set => Set(ref _lastCheckUtc, value); }
    public string? SkippedVersion { get => _skippedVersion; set => Set(ref _skippedVersion, value); }
}

public sealed class KeymapSettings : SettingsSection, ISettingsSection
{
    public static string Key => "keymap";

    private Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

    /// <summary>Command id → gesture text ("" = unbound). Only differences from the defaults are stored.</summary>
    public Dictionary<string, string> Overrides { get => _overrides; set => Set(ref _overrides, value); }
}
