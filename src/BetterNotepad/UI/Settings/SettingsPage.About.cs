using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI;

/// <summary>About page: identity and version, what's new, data folders, licenses.</summary>
public sealed partial class SettingsPage
{
    /// <summary>Changelog per version, newest first; each item is a localization key.</summary>
    private static readonly (string Version, string[] Items)[] WhatsNew =
    [
        ("0.7.0",
        [
            "settings.about.news.070.paste",
            "settings.about.news.070.html",
            "settings.about.news.070.images",
            "settings.about.news.070.csv",
        ]),
        ("0.6.0",
        [
            "settings.about.news.060.copy",
            "settings.about.news.060.listing",
            "settings.about.news.060.image",
            "settings.about.news.060.menu",
            "settings.about.news.060.explorer",
            "settings.about.news.060.untitled",
            "settings.about.news.060.tray",
        ]),
        ("0.5.0",
        [
            "settings.about.news.050.ide",
            "settings.about.news.050.md",
            "settings.about.news.050.pdf",
            "settings.about.news.050.system",
            "settings.about.news.050.pin",
        ]),
        ("0.1.0",
        [
            "settings.about.news.010.tabs",
            "settings.about.news.010.editor",
            "settings.about.news.010.external",
            "settings.about.news.010.themes",
            "settings.about.news.010.settings",
        ]),
    ];

    /// <summary>Third-party components (name, SPDX license, what it does).</summary>
    private static readonly (string Name, string License, string DescKey)[] Licenses =
    [
        (AppInfo.ProductName, "MIT", "settings.about.lic.app"),
        ("AvalonEdit", "MIT", "settings.about.lic.avalonedit"),
        (".NET", "MIT", "settings.about.lic.dotnet"),
        ("Markdig", "BSD-2-Clause", "settings.about.lic.markdig"),
        ("PDFium", "BSD-3-Clause / Apache-2.0", "settings.about.lic.pdfium"),
        ("DocumentFormat.OpenXml", "MIT", "settings.about.lic.openxml"),
        ("Microsoft Edge WebView2 SDK", "BSD-3-Clause", "settings.about.lic.webview2"),
    ];

    private static ImageSource? _largeIcon;

    private void BuildAbout(StackPanel panel)
    {
        panel.Children.Add(AboutHero());

        var news = Group(panel, Loc.T("settings.about.whatsNew"));
        foreach (var (version, items) in WhatsNew) news.Add(NewsEntry(version, items));

        var data = Group(panel, Loc.T("settings.about.dataGroup"));
        foreach (var (titleKey, dir) in new[]
        {
            ("settings.about.dataSettings", AppEnv.RoamingDir),
            ("settings.about.dataSession", AppEnv.LocalDir),
            ("settings.about.dataLogs", AppEnv.LogDir),
        })
        {
            var path = dir;
            var row = data.Add(new SettingRow(Glyphs.Folder, Loc.T(titleKey), path,
                SettingsUi.Button(Loc.T("settings.about.openFolder"), (_, _) => OpenFolder(path), glyph: Glyphs.OpenFolder, id: "about.open." + titleKey[(titleKey.LastIndexOf('.') + 1)..])));
            row.DescriptionBlock.TextWrapping = TextWrapping.NoWrap;
            row.DescriptionBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            row.DescriptionBlock.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            row.DescriptionBlock.FontSize = 11.5;
            row.DescriptionBlock.ToolTip = path;
        }

        var lic = Group(panel, Loc.T("settings.about.licensesGroup"));
        foreach (var (name, license, descKey) in Licenses)
            lic.Add(new SettingRow(null, name, Loc.T(descKey), SettingsUi.Chip(license, "Brush.BgAlt", "Brush.TextDim", mono: true, size: 11), vpad: 10));

        var copyright = typeof(SettingsPage).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? AppInfo.ProductName;
        var foot = SettingsUi.Label(Loc.T("settings.about.footer", copyright), 11.5, "Brush.TextFaint");
        foot.HorizontalAlignment = HorizontalAlignment.Center;
        foot.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(foot);
    }

    private FrameworkElement AboutHero()
    {
        var hero = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 24) };
        hero.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        hero.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        // a soft accent wash from the left edge
        var mask = new LinearGradientBrush(Colors.Black, Colors.Transparent, new Point(0, 0.5), new Point(0.8, 0.5));
        mask.Freeze();
        var wash = new Border { CornerRadius = new CornerRadius(15), Opacity = 0.11, OpacityMask = mask, IsHitTestVisible = false };
        wash.SetResourceReference(Border.BackgroundProperty, "Brush.AccentGradient");

        var grid = new Grid { Margin = new Thickness(24, 22, 22, 22) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Source = LargeAppIcon(), Width = 68, Height = 68, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        icon.Loaded += (_, _) => Motion.Bounce(icon, 1.12);
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = SettingsUi.Label(AppInfo.ProductName, 24, "Brush.Text", FontWeights.SemiBold);
        name.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        texts.Children.Add(name);
        var tagline = SettingsUi.Label(Loc.T("settings.about.tagline"), 13, "Brush.TextDim", wrap: true);
        tagline.Margin = new Thickness(0, 2, 0, 12);
        texts.Children.Add(tagline);
        var chips = new WrapPanel();
        var version = SettingsUi.Chip(Loc.T("settings.about.version", AppInfo.Version), "Brush.AccentSoft", "Brush.AccentText", size: 11.5);
        ((TextBlock)version.Child).FontWeight = FontWeights.SemiBold;
        chips.Children.Add(version);
        chips.Children.Add(SettingsUi.Chip(Loc.T("settings.about.commit", AppInfo.Commit), "Brush.SurfaceHover", "Brush.TextDim", mono: true, size: 11));
        chips.Children.Add(SettingsUi.Chip(Loc.T("settings.about.built", AppInfo.BuildDateText), "Brush.SurfaceHover", "Brush.TextDim", size: 11));
        foreach (FrameworkElement c in chips.Children) c.Margin = new Thickness(0, 0, 6, 4);
        texts.Children.Add(chips);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        var check = SettingsUi.Button(Loc.T("settings.about.checkUpdates"), (_, _) => CheckForUpdates(), glyph: Glyphs.Refresh, id: "about.checkUpdates");
        check.HorizontalAlignment = HorizontalAlignment.Stretch;
        actions.Children.Add(check);
        var repo = SettingsUi.Button(Loc.T("settings.about.repository"), (_, _) => OpenRepository(), "GhostButton", Glyphs.Globe, "about.repository");
        repo.Margin = new Thickness(0, 6, 0, 0);
        repo.HorizontalAlignment = HorizontalAlignment.Stretch;
        actions.Children.Add(repo);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        hero.Child = new Grid { Children = { wash, grid } };
        return hero;
    }

    private static FrameworkElement NewsEntry(string version, string[] items)
    {
        var entry = new StackPanel { Margin = new Thickness(0, 14, 0, 14) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var v = SettingsUi.Chip(version, "Brush.AccentSoft", "Brush.AccentText", size: 11.5);
        ((TextBlock)v.Child).FontWeight = FontWeights.SemiBold;
        head.Children.Add(v);
        var label = SettingsUi.Label(Loc.T("settings.about.devVersion"), 12, "Brush.TextDim");
        label.Margin = new Thickness(10, 0, 0, 0);
        head.Children.Add(label);
        entry.Children.Add(head);
        foreach (var key in items)
        {
            var line = new Grid { Margin = new Thickness(2, 0, 0, 6) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new Ellipse { Width = 5, Height = 5, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 7, 0, 0) };
            dot.SetResourceReference(Shape.FillProperty, "Brush.AccentGradient");
            line.Children.Add(dot);
            var text = SettingsUi.Label(Loc.T(key), 13, "Brush.Text", wrap: true);
            text.LineHeight = 19;
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            entry.Children.Add(line);
        }
        return entry;
    }

    /// <summary>The largest frame of the app icon (the window uses a 32 px one).</summary>
    private static ImageSource LargeAppIcon()
    {
        if (_largeIcon is not null) return _largeIcon;
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/BetterNotepad;component/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            _largeIcon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch
        {
            _largeIcon = MainWindow.AppIconSource;
        }
        return _largeIcon;
    }

    private void OpenFolder(string dir)
    {
        if (AppEnv.TestMode) return; // a test instance never opens windows of other programs
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _window.Toasts.Show(Loc.T("settings.about.openFailed", ex.Message), ToastKind.Error);
        }
    }

    private void OpenRepository()
    {
        if (AppEnv.TestMode) return;
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _window.Toasts.Show(Loc.T("settings.about.openFailed", ex.Message), ToastKind.Error);
        }
    }
}
