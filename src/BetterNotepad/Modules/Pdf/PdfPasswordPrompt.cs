using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterNotepad.Core.Localization;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using Glyphs = BetterNotepad.UI.Controls.Glyphs;

namespace BetterNotepad.Modules.Pdf;

/// <summary>
/// Passwords of protected PDFs entered in this session, by full path. Memory only: never written to the session,
/// the settings or the disk, gone when the app quits (a restored tab asks again).
/// </summary>
internal static class PdfPasswords
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task> Asking = new(StringComparer.OrdinalIgnoreCase);

    public static string? Get(string path) => Known.GetValueOrDefault(Key(path));

    public static void Remember(string path, string password) => Known[Key(path)] = password;

    public static void Forget(string path) => Known.Remove(Key(path));

    /// <summary>A prompt for this file is open (another view of the same file waits for it instead of asking twice).</summary>
    public static Task? PromptInProgress(string path) => Asking.GetValueOrDefault(Key(path));

    public static void BeginPrompt(string path, Task done) => Asking[Key(path)] = done;

    public static void EndPrompt(string path) => Asking.Remove(Key(path));

    private static string Key(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}

/// <summary>
/// Body of the password dialog: a password field styled like <c>Field</c> with a show / hide toggle, a placeholder,
/// an inline error (wrong password → shake), a Caps Lock hint and a note that the password stays in memory only.
/// </summary>
internal sealed class PdfPasswordPrompt
{
    private readonly PasswordBox _box = new();
    private readonly TextBox _plain = new() { Visibility = Visibility.Collapsed };
    private readonly Button _reveal = new() { Width = 28, Height = 28 };
    private readonly Icon _revealIcon = new() { Glyph = Glyphs.View, FontSize = 14 };
    private readonly TextBlock _placeholder = new() { FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Grid _field = new();
    private readonly StackPanel _error = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _errorText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _caps = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0), Visibility = Visibility.Collapsed };
    private bool _syncing;

    public PdfPasswordPrompt()
    {
        // the Field templates apply Padding twice (host margin + text view): 6 → ~12 px; room on the right for the toggle
        var padding = new Thickness(6, 4, 20, 4);
        _box.SetResourceReference(FrameworkElement.StyleProperty, "PasswordField");
        _box.Padding = padding;
        _box.FontSize = 13;
        _plain.SetResourceReference(FrameworkElement.StyleProperty, "Field");
        _plain.Padding = padding;
        _plain.FontSize = 13;
        _box.PasswordChanged += (_, _) => Changed(fromPlain: false);
        _plain.TextChanged += (_, _) => Changed(fromPlain: true);
        foreach (var input in new Control[] { _box, _plain })
        {
            input.PreviewKeyDown += (_, _) => UpdateCaps();
            input.PreviewKeyUp += (_, _) => UpdateCaps();
            input.GotKeyboardFocus += (_, _) => UpdateCaps();
        }
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");

        _reveal.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        _reveal.Content = _revealIcon;
        _reveal.HorizontalAlignment = HorizontalAlignment.Right;
        _reveal.VerticalAlignment = VerticalAlignment.Center;
        _reveal.Margin = new Thickness(0, 0, 4, 0);
        _reveal.Click += (_, _) => SetRevealed(!Revealed);

        _field.Children.Add(_box);
        _field.Children.Add(_plain);
        _field.Children.Add(_placeholder);
        _field.Children.Add(_reveal);

        var errorIcon = new Icon { Glyph = Glyphs.Error, FontSize = 12, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        errorIcon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        _errorText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        _error.Children.Add(errorIcon);
        _error.Children.Add(_errorText);

        var capsIcon = new Icon { Glyph = Glyphs.Warning, FontSize = 12, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        capsIcon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
        var capsText = new TextBlock { FontSize = 12, Text = Loc.T("pdf.pw.caps"), VerticalAlignment = VerticalAlignment.Center };
        capsText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _caps.Children.Add(capsIcon);
        _caps.Children.Add(capsText);

        var note = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 12, 0, 0) };
        var noteIcon = new Icon { Glyph = Glyphs.Shield, FontSize = 12, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        noteIcon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        var noteText = new TextBlock { FontSize = 12, Text = Loc.T("pdf.pw.note"), TextWrapping = TextWrapping.Wrap, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center };
        noteText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        note.Children.Add(noteIcon);
        note.Children.Add(noteText);

        Element = new StackPanel { Children = { _field, _error, _caps, note } };
        ApplyTexts();
    }

    /// <summary>The content placed into the dialog.</summary>
    public FrameworkElement Element { get; }

    /// <summary>What gets the keyboard focus when the dialog opens.</summary>
    public IInputElement FocusTarget => Revealed ? _plain : _box;

    public string Password => Revealed ? _plain.Text : _box.Password;

    public bool Revealed => _plain.Visibility == Visibility.Visible;

    public bool ErrorShown => _error.Visibility == Visibility.Visible;

    /// <summary>For tests and snapshots: types a password.</summary>
    internal void SetPassword(string password)
    {
        if (Revealed) _plain.Text = password;
        else _box.Password = password;
    }

    public void SetRevealed(bool revealed)
    {
        if (revealed == Revealed) return;
        _syncing = true;
        if (revealed) _plain.Text = _box.Password;
        else _box.Password = _plain.Text;
        _syncing = false;
        _plain.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        _box.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        _revealIcon.Glyph = revealed ? "" : Glyphs.View; // Hide / View
        ApplyTexts();
        Keyboard.Focus(FocusTarget);
        if (revealed) _plain.CaretIndex = _plain.Text.Length;
    }

    /// <summary>Inline error under the field: red outline, message, shake; the password is selected for retyping.</summary>
    public void ShowError(string message)
    {
        _errorText.Text = message;
        _error.Visibility = Visibility.Visible;
        _box.Tag = _plain.Tag = "error";
        Motion.Shake(_field);
        Keyboard.Focus(FocusTarget);
        if (Revealed) _plain.SelectAll();
        else _box.SelectAll();
    }

    private void Changed(bool fromPlain)
    {
        _placeholder.Visibility = Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_syncing || fromPlain != Revealed) return;
        // typing again clears the error
        _error.Visibility = Visibility.Collapsed;
        _box.Tag = _plain.Tag = null;
    }

    private void UpdateCaps() =>
        _caps.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyTexts()
    {
        _placeholder.Text = Loc.T("pdf.pw.placeholder");
        _reveal.ToolTip = Loc.T(Revealed ? "pdf.pw.hide" : "pdf.pw.show");
    }
}
