using BetterNotepad.Core.Settings;

namespace BetterNotepad.Modules.Html;

/// <summary>Settings of the HTML / SVG preview (settings.json section "htmlPreview").</summary>
public sealed class HtmlPreviewSettings : SettingsSection, ISettingsSection
{
    public static string Key => "htmlPreview";

    private bool _allowRemoteContent;

    /// <summary>
    /// Load styles, fonts, scripts and images from the internet (CDNs) in the preview. Off by default: the preview then makes no
    /// network connection of its own; files next to the page always load.
    /// </summary>
    public bool AllowRemoteContent { get => _allowRemoteContent; set => Set(ref _allowRemoteContent, value); }
}
