using System.Windows;
using System.Windows.Interop;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.UI.Tray;
using static BetterNotepad.Modules.Sys.SysNative;

namespace BetterNotepad.Modules.Sys;

/// <summary>
/// The notification-area icon (own Shell_NotifyIcon, no WinForms). Left click shows or hides the main window,
/// right click opens the themed <see cref="TrayMenu"/>. Survives an Explorer restart (TaskbarCreated).
/// Never created in test mode.
/// </summary>
internal sealed unsafe class SystemTray : TrayService
{
    private const uint IconId = 1;
    private const int CallbackMessage = SysNative.WM_APP + 1;

    private readonly uint _taskbarCreated = SysNative.RegisterWindowMessage("TaskbarCreated");
    private nint _icon;
    private bool _iconOwned;
    private bool _added;
    private TrayMenu? _menu;

    public static SystemTray? Active { get; private set; }

    /// <summary>Creates the icon (idempotent). Does nothing in test mode.</summary>
    public static void Start()
    {
        if (Active is not null || AppEnv.TestMode) return;
        var tray = new SystemTray();
        Active = tray;
        I = tray;
        MessageWindow.Source.AddHook(tray.Hook);
        HotkeyService.StatusChanged += tray.UpdateTip;
        tray.AddIcon();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    /// <summary>Removes the icon and lets the window close normally again.</summary>
    public static void Stop()
    {
        if (Active is not { } tray) return;
        Active = null;
        if (I == tray) I = null;
        HotkeyService.StatusChanged -= tray.UpdateTip;
        try { MessageWindow.Source.RemoveHook(tray.Hook); } catch { /* window already gone */ }
        tray.RemoveIcon();
        tray._menu?.CloseMenu();
    }

    private string Tip()
    {
        var chord = HotkeyService.StateOf(HotkeyService.Summon) == HotkeyState.Registered ? HotkeyService.ChordOf(HotkeyService.Summon) : null;
        return chord is { } c ? $"{AppInfo.ProductName} ({c})" : AppInfo.ProductName;
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = sizeof(NOTIFYICONDATA),
        hWnd = MessageWindow.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
    };

    private void AddIcon()
    {
        if (_icon == 0) _icon = SysNative.LoadSmallAppIcon(SysEnv.CurrentExe, out _iconOwned);
        var d = Data(SysNative.NIF_MESSAGE | SysNative.NIF_ICON | SysNative.NIF_TIP);
        SysNative.CopyTo(d.szTip, Tip(), 128);
        _added = SysNative.Shell_NotifyIcon(SysNative.NIM_ADD, ref d);
        if (!_added) Log.Warn("tray: icon not added yet (taskbar not ready); will retry on TaskbarCreated");
    }

    private void UpdateTip()
    {
        if (!_added) return;
        var d = Data(SysNative.NIF_TIP);
        SysNative.CopyTo(d.szTip, Tip(), 128);
        SysNative.Shell_NotifyIcon(SysNative.NIM_MODIFY, ref d);
    }

    private void RemoveIcon()
    {
        if (_added)
        {
            var d = Data(0);
            SysNative.Shell_NotifyIcon(SysNative.NIM_DELETE, ref d);
            _added = false;
        }
        if (_iconOwned && _icon != 0) SysNative.DestroyIcon(_icon);
        _icon = 0;
    }

    public override void ShowHint(string title, string text)
    {
        if (!_added) return;
        var d = Data(SysNative.NIF_INFO);
        SysNative.CopyTo(d.szInfoTitle, title, 64);
        SysNative.CopyTo(d.szInfo, text, 256);
        d.dwInfoFlags = SysNative.NIIF_INFO;
        SysNative.Shell_NotifyIcon(SysNative.NIM_MODIFY, ref d);
    }

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            handled = true;
            switch ((int)(lParam & 0xFFFF))
            {
                case SysNative.WM_LBUTTONUP:
                    Application.Current.Dispatcher.BeginInvoke(ToggleMainWindow);
                    break;
                case SysNative.WM_RBUTTONUP or SysNative.WM_CONTEXTMENU:
                    Application.Current.Dispatcher.BeginInvoke(OpenMenu);
                    break;
                case SysNative.NIN_BALLOONUSERCLICK:
                    Application.Current.Dispatcher.BeginInvoke(() => App.Current.MainAppWindow?.BringToFront());
                    break;
            }
        }
        else if (msg == _taskbarCreated)
        {
            // Explorer restarted: the icon is gone, add it again
            _added = false;
            AddIcon();
        }
        return 0;
    }

    public static void ToggleMainWindow()
    {
        var w = App.Current.MainAppWindow;
        if (w is null) return;
        if (w.IsVisible && w.WindowState != WindowState.Minimized) w.HideToTray();
        else w.BringToFront();
    }

    private void OpenMenu()
    {
        _menu?.CloseMenu();
        _menu = new TrayMenu();
        _menu.Closed += (_, _) => _menu = null;
        _menu.Show();
    }
}
