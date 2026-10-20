using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BetterNotepad.Core.Localization;
using BetterNotepad.Html;
using BetterNotepad.Modules.Html;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.UI;

/// <summary>Files page: the HTML / SVG preview group (internet content, the preview engine).</summary>
public sealed partial class SettingsPage
{
    /// <summary>Automation id of the "Allow internet content in the HTML preview" switch (the preview's notice links to it).</summary>
    internal const string HtmlRemoteId = "html.allowRemote";

    private void BuildHtmlPreview(StackPanel panel)
    {
        var s = Section<HtmlPreviewSettings>();
        var group = Group(panel, Loc.T("settings.html.group"));
        group.Add(new SettingRow(Glyphs.Globe, Loc.T("settings.html.allowRemote"), Loc.T("settings.html.allowRemoteDesc"),
            Switch(HtmlRemoteId, () => s.AllowRemoteContent, v => s.AllowRemoteContent = v)));

        // which engine draws the preview, and that it only runs while needed (or where to get it when it is missing)
        var version = WebViewRuntime.AvailableVersion();
        UIElement status;
        if (version is not null)
        {
            status = SettingsUi.Chip(version, "Brush.BgAlt", "Brush.TextDim", mono: true, size: 11);
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(SettingsUi.Chip(Loc.T("settings.html.engineMissing"), "Brush.WarningSoft", "Brush.Warning"));
            var get = SettingsUi.SmallButton(Loc.T("settings.html.engineGet"), (_, _) => HtmlModule.Options.OpenExternal(HtmlModule.Options.RuntimeDownloadUri), id: "html.getRuntime");
            get.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(get);
            status = row;
        }
        group.Add(new SettingRow(Glyphs.Code, Loc.T("settings.html.engine"), Loc.T("settings.html.engineDesc"), status));
    }

    /// <summary>
    /// Scrolls to the control with this automation id and gives it a small nudge, with the keyboard on it (e.g. the
    /// preview's "blocked content" notice opening its setting). Safe to call right after <see cref="Show"/>.
    /// </summary>
    internal void Reveal(string automationId)
    {
        if (FindById<FrameworkElement>(automationId) is not { } control) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !control.IsLoaded) return;
            // the whole group (its card) in view, with a little air around it
            var card = FindAncestor<SettingsCard>(control) ?? (FrameworkElement?)FindAncestor<SettingRow>(control) ?? control;
            card.BringIntoView(new Rect(0, -40, card.ActualWidth, card.ActualHeight + 64));
            control.Focus();
            Motion.Bounce(control, 1.12);
        }, DispatcherPriority.Loaded);
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : FrameworkElement
    {
        for (var p = d; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (p is T hit) return hit;
        return null;
    }

    /// <summary>For tests: the id of the control that has the keyboard (Reveal moved it there).</summary>
    internal static string? FocusedAutomationId() => Keyboard.FocusedElement is DependencyObject d ? AutomationProperties.GetAutomationId(d) : null;
}
