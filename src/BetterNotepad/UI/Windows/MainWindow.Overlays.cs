using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;

namespace BetterNotepad.UI.Windows;

/// <summary>Palette, settings and tab switcher entry points of a window.</summary>
public partial class MainWindow
{
    private TabSwitcher? _tabSwitcher;

    public void ShowPalette(PaletteMode mode, string? initialText = null) => CommandPalette.Show(this, mode, initialText);

    public void ShowSettings(string? page = null)
    {
        // a small pinned window has no room for the settings page: it opens in the main window
        if (Model.Pinned && App.Current.MainAppWindow is { } main && main != this)
        {
            main.BringToFront();
            main.ShowSettings(page);
            return;
        }
        SettingsPage.Show(this, page);
    }

    public void OpenTabSwitcher(bool reverse) => _tabSwitcher = TabSwitcher.Open(this, reverse);

    /// <summary>
    /// Context for commands run from the palette or a menu: like <see cref="Context"/>, but the active
    /// tab's editor counts as the target although it is not focused.
    /// </summary>
    public CommandContext PaletteContext() => new()
    {
        Window = this,
        Document = ActiveDocument,
        Group = Model.ActiveGroup,
        Editor = ActiveView?.Editor,
    };

    /// <summary>Runs a command by id from a menu / the palette (errors become a toast).</summary>
    public void RunCommand(string id)
    {
        var cmd = CommandRegistry.Get(id);
        if (cmd is null) return;
        var ctx = PaletteContext();
        if (!(cmd.CanExecute?.Invoke(ctx) ?? true)) return;
        try
        {
            cmd.Execute(ctx);
        }
        catch (Exception ex)
        {
            Log.Error($"command {cmd.Id} failed", ex);
            Toasts.Show(Loc.T("error.command", cmd.Title, ex.Message), ToastKind.Error);
        }
    }

    /// <summary>Starts a task nobody awaits; a failure is logged and shown instead of vanishing.</summary>
    internal static void Fire(Task task)
    {
        task.ContinueWith(t =>
        {
            var ex = t.Exception?.GetBaseException();
            Log.Error("background task failed", ex);
            App.Current?.Dispatcher.BeginInvoke(() =>
                App.Current.MainAppWindow?.Toasts.Show(Loc.T("error.unexpected", ex?.Message ?? ""), ToastKind.Error));
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
