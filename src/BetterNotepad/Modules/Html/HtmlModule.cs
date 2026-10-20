using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Html;
using BetterNotepad.UI;
using BetterNotepad.UI.Windows;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.Modules.Html;

/// <summary>
/// The HTML / SVG module: a browser-exact live preview (WebView2, created only when a preview is first shown, released when no
/// tab needs it) next to the editor — Preview | Split | Edit like Markdown. Internet content is blocked unless allowed in
/// Settings → Files; the commands are DevTools (F12), reload and open in the system browser.
/// </summary>
public static class HtmlModule
{
    private static readonly DocumentKind[] Kinds = [DocumentKind.Html, DocumentKind.Svg];

    /// <summary>Shared by every preview (the first one to start the browser fixes its language and profile folder).</summary>
    internal static WebPreviewOptions Options { get; } = new();

    /// <summary>Previews alive right now (shown or cached in a tab group).</summary>
    internal static readonly List<HtmlPreviewView> Live = new();

    /// <summary>Links the previews handed to the system browser (test mode only records them).</summary>
    internal static readonly List<string> OpenedExternally = new();

    private static HtmlPreviewSettings Settings => SettingsService.I.Get<HtmlPreviewSettings>();

    public static void Register()
    {
        Options.UserDataFolder = Path.Combine(AppEnv.LocalDir, "WebView2"); // %LOCALAPPDATA%\BetterNotepad\WebView2 (test: under the data dir)
        Options.Language = Loc.I.Language;
        Options.AllowRemoteContent = Settings.AllowRemoteContent;
        Options.StubAllowedRemote = AppEnv.TestMode; // test instances never touch the network, even when the setting allows it
        // ...and their browser resolves no host at all (web sockets, preconnects): nothing can leave the hidden test desktop
        if (AppEnv.TestMode) Options.AdditionalBrowserArguments = "--host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE " + "bnpreview.local\" --no-proxy-server";
        Options.HandleDevToolsKey = false; // F12 is the rebindable html.devTools command
        Options.OpenExternal = uri => Shell(uri.AbsoluteUri, App.Current?.MainAppWindow);
        Options.ReadLocalText = ReadOpenBuffer;
        ApplyLabels();

        DocumentViewRegistry.RegisterPreview(DocumentKind.Html, doc => new HtmlPreviewView(doc, svg: false));
        DocumentViewRegistry.RegisterPreview(DocumentKind.Svg, doc => new HtmlPreviewView(doc, svg: true));

        SettingsService.I.Changed += OnSettingsChanged;
        Loc.LanguageChanged += () =>
        {
            Options.Language = Loc.I.Language; // the browser's own menus follow on its next start
            ApplyLabels();
        };
        DocumentManager.I.Opened += d =>
        {
            if (d is TextDocument t) t.BufferChanged += OnAnyBufferChanged;
        };
        DocumentManager.I.Closed += d =>
        {
            if (d is TextDocument t) t.BufferChanged -= OnAnyBufferChanged;
        };
        foreach (var t in DocumentManager.I.Documents.OfType<TextDocument>()) t.BufferChanged += OnAnyBufferChanged;
        RegisterCommands();
        MainWindow.FocusedContentKey = OnFocusedPageKey;
    }

    /// <summary>
    /// The page has the keyboard: the editor's find / find next / find previous shortcuts (as the user bound them) work in the
    /// page with the browser's own find bar instead of the hidden or unrelated editor find bar.
    /// </summary>
    private static bool OnFocusedPageKey(MainWindow w, KeyChord chord)
    {
        if ((w.ActiveView as TextDocumentView)?.Preview is not HtmlPreviewView { IsLive: true } pv || !pv.Preview.IsPageFocused) return false;
        if (chord == Keymap.GestureFor("editor.find"))
        {
            _ = pv.Preview.StartFindAsync();
            return true;
        }
        if (!pv.Preview.IsFinding) return false;
        if (chord == Keymap.GestureFor("editor.findNext"))
        {
            pv.Preview.FindNext();
            return true;
        }
        if (chord == Keymap.GestureFor("editor.findPrev"))
        {
            pv.Preview.FindNext(backwards: true);
            return true;
        }
        return false;
    }

    private static void ApplyLabels() => Options.Labels = new WebPreviewLabels
    {
        RuntimeMissing = Loc.T("html.runtimeMissing"),
        RuntimeMissingHint = Loc.T("html.runtimeMissingHint"),
        RuntimeDownload = Loc.T("html.runtimeDownload"),
        LoadFailed = Loc.T("html.loadFailed"),
        EmptyPage = Loc.T("html.emptyPage"),
    };

    private static void OnSettingsChanged(SettingsSection section, string? property)
    {
        if (section is not HtmlPreviewSettings s || Options.AllowRemoteContent == s.AllowRemoteContent) return;
        Options.AllowRemoteContent = s.AllowRemoteContent;
        Log.Info($"html: internet content in previews {(s.AllowRemoteContent ? "allowed" : "blocked")}");
        foreach (var v in Live.ToList()) v.OnPolicyChanged();
    }

    // ───────────── pages using other open tabs ─────────────

    /// <summary>The unsaved text of an open tab, so a page shows CSS / JS edits before they are saved (else null: from disk).</summary>
    private static string? ReadOpenBuffer(string path)
        => DocumentManager.I.FindByPath(path) is TextDocument { HasBuffer: true, IsDirty: true } t ? t.Buffer.Text : null;

    private static void OnAnyBufferChanged(object? sender, EventArgs e)
    {
        if (sender is not TextDocument { FilePath: { } path } || Live.Count == 0) return;
        foreach (var v in Live)
            if (v.IsLive && v.DependsOn(path)) v.RefreshNow();
    }

    // ───────────── commands ─────────────

    private static void RegisterCommands()
    {
        Cmd("html.devTools", "F12", c => ActivePreview(c)?.Preview.OpenDevTools(), c => ActivePreview(c) is { IsLive: true },
            "devtools f12 inspect element narzędzia deweloperskie zbadaj konsola console");
        Cmd("html.reload", null, c => ActivePreview(c)?.RefreshNow(), c => ActivePreview(c) is not null,
            "reload refresh odśwież przeładuj podgląd preview");
        Cmd("html.openInBrowser", null, c => OpenInBrowser(AppCommands.WindowOf(c), AppCommands.TextOf(c)), c => AppCommands.TextOf(c) is not null,
            "open in browser otwórz w przeglądarce edge chrome firefox");
    }

    private static void Cmd(string id, string? gesture, Action<CommandContext> run, Func<CommandContext, bool> can, string search)
        => CommandRegistry.Register(new AppCommand
        {
            Id = id,
            TitleKey = "cmd." + id,
            CategoryKey = "cmd.cat.view",
            DefaultGesture = gesture,
            Kinds = Kinds,
            SearchTerms = search,
            Execute = run,
            CanExecute = can,
        });

    /// <summary>The HTML / SVG preview of the active tab, when it is on screen (Split or Preview).</summary>
    internal static HtmlPreviewView? ActivePreview(CommandContext c)
        => (AppCommands.WindowOf(c)?.ActiveView as TextDocumentView)?.Preview is HtmlPreviewView { Element.IsVisible: true } v ? v : null;

    private static void OpenInBrowser(MainWindow? w, TextDocument? doc)
    {
        if (doc?.FilePath is not { } path)
        {
            w?.Toasts.Show(Loc.T("html.saveFirst"), ToastKind.Info);
            return;
        }
        Shell(path, w);
    }

    /// <summary>A link or file to the system's handler (browser, mail, Explorer); test instances only log it.</summary>
    internal static void Shell(string target, MainWindow? w)
    {
        OpenedExternally.Add(target);
        if (AppEnv.TestMode)
        {
            Log.Info("html: not opened in test mode: " + target);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("html: cannot open " + target, ex);
            w?.Toasts.Show(Loc.T("html.linkFailed", target), ToastKind.Error);
        }
    }

    /// <summary>The notice's "Settings" button: the Files page, scrolled to the switch, which gets a little nudge.</summary>
    internal static void OpenRemoteSetting(MainWindow? w)
    {
        w ??= App.Current?.MainAppWindow;
        if (w is null) return;
        w.ShowSettings(SettingsPageIds.Files);
        var target = w.Model.Pinned && App.Current?.MainAppWindow is { } main ? main : w;
        target.Dispatcher.BeginInvoke(() => SettingsPage.For(target)?.Reveal(SettingsPage.HtmlRemoteId), DispatcherPriority.Loaded);
    }
}
