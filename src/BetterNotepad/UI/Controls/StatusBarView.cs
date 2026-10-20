using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Editor.Highlighting;

namespace BetterNotepad.UI.Controls;

/// <summary>Line:column, selection, lock, language, encoding, line endings, indentation, zoom.</summary>
public sealed class StatusBarView : Grid
{
    private readonly Button _position = Item();
    private readonly Button _lock = Item(Glyphs.Lock);
    private readonly Button _tail = Item(Glyphs.Refresh);
    private readonly Button _large = Item(Glyphs.Info);
    private readonly Button _zoom = Item();
    private readonly Button _indent = Item();
    private readonly Button _encoding = Item();
    private readonly Button _eol = Item();
    private readonly Button _language = Item();
    private IDocumentView? _view;
    private Document? _doc;

    public StatusBarView()
    {
        Height = 26;
        SetResourceReference(BackgroundProperty, "Brush.Chrome");
        var top = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
        top.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        Children.Add(top);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        left.Children.Add(_position);
        left.Children.Add(_lock);
        left.Children.Add(_tail);
        left.Children.Add(_large);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        right.Children.Add(_zoom);
        right.Children.Add(_indent);
        right.Children.Add(_encoding);
        right.Children.Add(_eol);
        right.Children.Add(_language);
        Children.Add(left);
        Children.Add(right);

        _lock.SetResourceReference(Control.ForegroundProperty, "Brush.Warning");
        _position.Click += (_, _) => { if (_view is not IStatusInfoView) Commands("editor.goToLine"); };
        _lock.Click += (_, _) => Commands("file.toggleLock");
        _tail.Click += (_, _) => Commands("file.toggleTail");
        _zoom.Click += (_, _) =>
        {
            if (_view is IStatusInfoView info) info.StatusZoomClicked();
            else Editor.CodeEditor.ResetZoom();
        };
        _language.Click += (_, _) => Commands("editor.changeLanguage");
        _encoding.Click += (_, _) => OpenMenu(_encoding, Windows.MainWindow.Of(this)?.BuildEncodingMenu());
        _eol.Click += (_, _) => OpenMenu(_eol, Windows.MainWindow.Of(this)?.BuildEolMenu());
        _indent.Click += (_, _) => OpenMenu(_indent, Windows.MainWindow.Of(this)?.BuildIndentMenu());

        Loaded += (_, _) =>
        {
            Loc.LanguageChanged += Refresh;
            SettingsService.I.Changed += OnSettings;
        };
        Unloaded += (_, _) =>
        {
            Loc.LanguageChanged -= Refresh;
            SettingsService.I.Changed -= OnSettings;
        };
        Refresh();
    }

    private void Commands(string id) => Windows.MainWindow.Of(this)?.Execute(id);

    private static void OpenMenu(FrameworkElement target, ContextMenu? menu)
    {
        if (menu is null) return;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private static Button Item(string? glyph = null)
    {
        var b = new Button { Visibility = Visibility.Collapsed };
        b.SetResourceReference(StyleProperty, "FlatButton");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null) row.Children.Add(new Icon { Glyph = glyph, FontSize = 11, Margin = new Thickness(0, 0, 5, 0) });
        row.Children.Add(new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        b.Content = row;
        return b;
    }

    private static void SetText(Button b, string? text, string? tip = null)
    {
        var tb = ((StackPanel)b.Content).Children.OfType<TextBlock>().Last();
        tb.Text = text ?? "";
        b.ToolTip = tip;
        b.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Bind(IDocumentView? view)
    {
        if (_view is TextDocumentView oldText) oldText.CaretMoved -= Refresh;
        if (_view is IStatusInfoView oldInfo) oldInfo.StatusChanged -= Refresh;
        if (_doc is not null) _doc.PropertyChanged -= OnDocChanged;
        _view = view;
        _doc = view?.Document;
        if (_view is TextDocumentView text) text.CaretMoved += Refresh;
        if (_view is IStatusInfoView info) info.StatusChanged += Refresh;
        if (_doc is not null) _doc.PropertyChanged += OnDocChanged;
        Refresh();
    }

    private void OnDocChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void OnSettings(SettingsSection s, string? p)
    {
        if (s is EditorSettings) Refresh();
    }

    public void Refresh()
    {
        var editor = _view?.Editor;
        var text = _doc as TextDocument;
        if (editor?.Document is not null)
        {
            var caret = editor.TextArea.Caret;
            var sel = editor.SelectionLength;
            var pos = Loc.T("status.position", caret.Line, caret.Column);
            if (sel > 0) pos += "  " + Loc.T("status.selected", sel);
            SetText(_position, pos, Loc.T("status.goToLineTip"));
        }
        else if (_view is IStatusInfoView info)
        {
            // a picture (or another non-text view): its own summary, not line:column
            SetText(_position, info.StatusText);
        }
        else
        {
            SetText(_position, null);
        }

        SetText(_lock, _doc?.IsLocked == true ? Loc.T("status.readOnly") : null, Loc.T("status.unlockTip"));
        SetText(_tail, text?.TailMode == true ? Loc.T("status.tail") : null, Loc.T("status.tailTip"));
        SetText(_large, text?.IsLarge == true ? Loc.T("status.large") : null, Loc.T("status.largeTip"));

        if (text is not null && text.HasBuffer)
        {
            var s = SettingsService.I.Get<EditorSettings>();
            SetText(_indent, s.InsertSpaces ? Loc.T("status.spaces", s.TabSize) : Loc.T("status.tabs", s.TabSize));
            SetText(_encoding, text.EncodingLabel, Loc.T("status.encodingTip"));
            SetText(_eol, text.Eol.Label(), Loc.T("status.eolTip"));
            SetText(_language, Languages.Get(text.LanguageId).Name, Loc.T("status.languageTip"));
            SetText(_zoom, Math.Abs(s.Zoom - 1) > 0.001 ? $"{s.Zoom * 100:0}%" : null, Loc.T("status.zoomTip"));
        }
        else
        {
            SetText(_indent, null);
            SetText(_encoding, null);
            SetText(_eol, null);
            SetText(_language, null);
            SetText(_zoom, (_view as IStatusInfoView)?.StatusZoom, (_view as IStatusInfoView)?.StatusZoomTip);
        }
    }
}
