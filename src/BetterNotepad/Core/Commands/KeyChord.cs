using System.Windows.Input;

namespace BetterNotepad.Core.Commands;

/// <summary>A key + modifiers ("Ctrl+Shift+P"). Win is allowed for global hotkeys only.</summary>
public readonly record struct KeyChord(ModifierKeys Modifiers, Key Key)
{
    public static KeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "win" or "windows" or "meta": mods |= ModifierKeys.Windows; break;
                default:
                    if (key is not null) return null; // only one non-modifier key per chord
                    key = ParseKey(raw);
                    if (key is null) return null;
                    break;
            }
        }
        return key is null ? null : new KeyChord(mods, key.Value);
    }

    private static Key? ParseKey(string s)
    {
        switch (s)
        {
            case "/": return Key.OemQuestion;
            case "\\": return Key.Oem5;
            case "[": return Key.OemOpenBrackets;
            case "]": return Key.OemCloseBrackets;
            case "-": return Key.OemMinus;
            case "=" or "Plus": return Key.OemPlus;
            case ",": return Key.OemComma;
            case ".": return Key.OemPeriod;
            case ";": return Key.OemSemicolon;
            case "'": return Key.OemQuotes;
            case "`": return Key.Oem3;
            case "Up": return Key.Up;
            case "Down": return Key.Down;
            case "Left": return Key.Left;
            case "Right": return Key.Right;
            case "Enter": return Key.Enter;
            case "Esc" or "Escape": return Key.Escape;
            case "Space": return Key.Space;
            case "Del" or "Delete": return Key.Delete;
            case "Ins" or "Insert": return Key.Insert;
            case "PgUp" or "PageUp": return Key.PageUp;
            case "PgDn" or "PageDown": return Key.PageDown;
            case "Backspace": return Key.Back;
            case "NumPlus": return Key.Add;
            case "NumMinus": return Key.Subtract;
        }
        if (s.Length == 1 && char.IsDigit(s[0])) return Key.D0 + (s[0] - '0');
        // KeyName writes numpad digits as "Num0".."Num9"
        if (s.Length == 4 && s.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && s[3] is >= '0' and <= '9') return Key.NumPad0 + (s[3] - '0');
        // names only: Enum.TryParse would also accept numbers ("999") and comma lists ("A,B")
        if (!char.IsLetter(s[0]) || !s.All(char.IsLetterOrDigit)) return null;
        return Enum.TryParse<Key>(s, ignoreCase: true, out var k) && Enum.IsDefined(k) ? k : null;
    }

    public static string KeyName(Key key) => key switch
    {
        Key.OemQuestion => "/",
        Key.OemBackslash or Key.Oem5 => "\\",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.Oem3 => "`",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Delete => "Del",
        Key.Insert => "Ins",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.Back => "Backspace",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        Key.Add => "NumPlus",
        Key.Subtract => "NumMinus",
        _ => key.ToString(),
    };

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public string[] Parts => ToString().Split('+');

    /// <summary>
    /// Ctrl+Alt+letter (and with Shift) is AltGr on Polish keyboards (ą ć ę ł ń ó ś ź ż): binding it
    /// would swallow those characters. Never allowed — neither global nor inside the app.
    /// </summary>
    public bool IsAltGrConflict =>
        Modifiers.HasFlag(ModifierKeys.Control) && Modifiers.HasFlag(ModifierKeys.Alt) && Key is >= Key.A and <= Key.Z;

    /// <summary>Normalizes keyboard events (Alt arrives as Key.System, AltGr as Ctrl+Alt).</summary>
    public static KeyChord FromEvent(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            Key.OemBackslash => Key.Oem5, // both "\" keys count as one
            _ => e.Key,
        };
        return new KeyChord(Keyboard.Modifiers, key);
    }

    public bool IsModifierOnly => Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
