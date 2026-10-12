using System.Windows.Input;
using BetterNotepad.Core.Commands;

namespace BetterNotepad.Tests;

public sealed class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+P", ModifierKeys.Control | ModifierKeys.Shift, Key.P)]
    [InlineData("Alt+Up", ModifierKeys.Alt, Key.Up)]
    [InlineData("Ctrl+/", ModifierKeys.Control, Key.OemQuestion)]
    [InlineData("Ctrl+]", ModifierKeys.Control, Key.OemCloseBrackets)]
    [InlineData("Ctrl+[", ModifierKeys.Control, Key.OemOpenBrackets)]
    [InlineData("Win+Shift+N", ModifierKeys.Windows | ModifierKeys.Shift, Key.N)]
    [InlineData("F3", ModifierKeys.None, Key.F3)]
    [InlineData("Shift+F3", ModifierKeys.Shift, Key.F3)]
    [InlineData("Ctrl+Alt+Shift+Delete", ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.Delete)]
    [InlineData("Ctrl+1", ModifierKeys.Control, Key.D1)]
    [InlineData("Ctrl+0", ModifierKeys.Control, Key.D0)]
    [InlineData("Ctrl+Enter", ModifierKeys.Control, Key.Enter)]
    [InlineData("Esc", ModifierKeys.None, Key.Escape)]
    [InlineData("Ctrl+PgUp", ModifierKeys.Control, Key.PageUp)]
    [InlineData("Ctrl+Tab", ModifierKeys.Control, Key.Tab)]
    [InlineData("Ctrl+Space", ModifierKeys.Control, Key.Space)]
    [InlineData("Ctrl+,", ModifierKeys.Control, Key.OemComma)]
    [InlineData("Ctrl+.", ModifierKeys.Control, Key.OemPeriod)]
    [InlineData("Ctrl+-", ModifierKeys.Control, Key.OemMinus)]
    [InlineData("Ctrl+=", ModifierKeys.Control, Key.OemPlus)]
    [InlineData("Ctrl+;", ModifierKeys.Control, Key.OemSemicolon)]
    [InlineData("Ctrl+'", ModifierKeys.Control, Key.OemQuotes)]
    [InlineData("Ctrl+`", ModifierKeys.Control, Key.Oem3)]
    public void Parse_RecognizesChord(string text, ModifierKeys mods, Key key)
    {
        var chord = KeyChord.Parse(text);
        Assert.NotNull(chord);
        Assert.Equal(mods, chord.Value.Modifiers);
        Assert.Equal(key, chord.Value.Key);
    }

    [Theory]
    [InlineData("Ctrl+Shift+P")]
    [InlineData("Alt+Up")]
    [InlineData("Ctrl+/")]
    [InlineData("Ctrl+]")]
    [InlineData("Win+Shift+N")]
    [InlineData("F3")]
    [InlineData("Shift+F3")]
    [InlineData("Ctrl+Alt+Up")]
    [InlineData("Ctrl+Shift+Alt+Del")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Esc")]
    [InlineData("Ctrl+PgDn")]
    [InlineData("Ctrl+Backspace")]
    [InlineData("Ctrl+Ins")]
    [InlineData("Ctrl+1")]
    [InlineData("Ctrl+\\")]
    [InlineData("Ctrl+-")]
    [InlineData("Ctrl+=")]
    [InlineData("Ctrl+`")]
    [InlineData("Alt+Z")]
    public void ToString_Parse_RoundTrip_IsIdentity(string text)
    {
        var chord = KeyChord.Parse(text);
        Assert.NotNull(chord);
        Assert.Equal(text, chord.Value.ToString());
        Assert.Equal(chord, KeyChord.Parse(chord.Value.ToString()));
    }

    [Theory]
    [InlineData("shift+ctrl+p", "Ctrl+Shift+P")]
    [InlineData("CTRL+SHIFT+P", "Ctrl+Shift+P")]
    [InlineData("Alt+Ctrl+Shift+Win+X", "Win+Ctrl+Shift+Alt+X")]
    [InlineData("Control+S", "Ctrl+S")]
    [InlineData("Meta+E", "Win+E")]
    [InlineData("Windows+D", "Win+D")]
    [InlineData(" Ctrl + Shift + P ", "Ctrl+Shift+P")]
    [InlineData("ctrl+up", "Ctrl+Up")]
    [InlineData("Delete", "Del")]
    [InlineData("Escape", "Esc")]
    [InlineData("PageUp", "PgUp")]
    [InlineData("Ctrl+Plus", "Ctrl+=")]
    public void Parse_IsCaseInsensitive_AndNormalizesToCanonicalText(string input, string canonical)
        => Assert.Equal(canonical, KeyChord.Parse(input)?.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("+")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Banana")]
    [InlineData("Ctrl+Shift+P+Q")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+999")]
    [InlineData("12")]
    [InlineData("Ctrl+A,B")]
    public void Parse_InvalidText_ReturnsNull(string? text)
        => Assert.Null(KeyChord.Parse(text));

    [Fact]
    public void RoundTrip_EveryDefinedKey_WithEveryModifierCombination()
    {
        var failures = new List<string>();
        ModifierKeys[] mods =
        [
            ModifierKeys.None,
            ModifierKeys.Control,
            ModifierKeys.Control | ModifierKeys.Shift,
            ModifierKeys.Alt,
            ModifierKeys.Windows | ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt,
        ];
        foreach (var key in Enum.GetValues<Key>())
        {
            if (key == Key.OemBackslash) continue; // deliberately folded into Oem5 ("both \ keys count as one")
            foreach (var m in mods)
            {
                var chord = new KeyChord(m, key);
                var text = chord.ToString();
                var back = KeyChord.Parse(text);
                if (back != chord) failures.Add($"{m}+{key}: ToString '{text}' parsed as {(back is null ? "null" : back.Value.Modifiers + "+" + back.Value.Key)}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(40)));
    }

    [Fact]
    public void Backslash_ParsesToOem5_AndBothBackslashKeysPrintTheSame()
    {
        Assert.Equal(Key.Oem5, KeyChord.Parse("Ctrl+\\")!.Value.Key);
        Assert.Equal("\\", KeyChord.KeyName(Key.Oem5));
        Assert.Equal("\\", KeyChord.KeyName(Key.OemBackslash));
    }

    [Fact]
    public void NumpadDigits_RoundTrip()
    {
        for (var i = 0; i <= 9; i++)
        {
            var chord = new KeyChord(ModifierKeys.Control, Key.NumPad0 + i);
            Assert.Equal($"Ctrl+Num{i}", chord.ToString());
            Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
            Assert.Equal(chord, KeyChord.Parse($"Ctrl+NumPad{i}"));
        }
    }

    [Fact]
    public void ToString_UsesStableModifierOrder_WinCtrlShiftAlt()
        => Assert.Equal("Win+Ctrl+Shift+Alt+K",
            new KeyChord(ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Windows, Key.K).ToString());

    [Fact]
    public void Parts_SplitsTheDisplayText()
    {
        Assert.Equal(["Ctrl", "Shift", "P"], KeyChord.Parse("Ctrl+Shift+P")!.Value.Parts);
        Assert.Equal(["F3"], KeyChord.Parse("F3")!.Value.Parts);
    }

    [Theory]
    [InlineData(Key.NumPad0, "Num0")]
    [InlineData(Key.NumPad9, "Num9")]
    [InlineData(Key.D5, "5")]
    [InlineData(Key.OemQuestion, "/")]
    [InlineData(Key.Return, "Enter")]
    [InlineData(Key.Back, "Backspace")]
    [InlineData(Key.F12, "F12")]
    [InlineData(Key.Add, "NumPlus")]
    [InlineData(Key.Subtract, "NumMinus")]
    public void KeyName_GivesShortDisplayNames(Key key, string name)
        => Assert.Equal(name, KeyChord.KeyName(key));

    [Theory]
    [InlineData("Ctrl+NumPlus", Key.Add)]
    [InlineData("Ctrl+NumMinus", Key.Subtract)]
    public void NumpadPlusMinus_RoundTrip(string text, Key key)
    {
        var chord = KeyChord.Parse(text);
        Assert.Equal(new KeyChord(ModifierKeys.Control, key), chord);
        Assert.Equal(text, chord!.Value.ToString());
    }

    [Fact]
    public void Equality_IsStructural()
    {
        Assert.Equal(KeyChord.Parse("Ctrl+Shift+P"), KeyChord.Parse("shift+ctrl+p"));
        Assert.NotEqual(KeyChord.Parse("Ctrl+P"), KeyChord.Parse("Ctrl+Shift+P"));
        Assert.Equal(new KeyChord(ModifierKeys.Control, Key.S).GetHashCode(), KeyChord.Parse("Ctrl+S")!.Value.GetHashCode());
    }

    // ---------------------------------------------------------------- AltGr (Polish keyboard) guard

    [Theory]
    [InlineData("Ctrl+Alt+S")]
    [InlineData("Ctrl+Shift+Alt+A")]
    [InlineData("Ctrl+Alt+A")]
    [InlineData("Ctrl+Alt+Z")]
    [InlineData("Ctrl+Alt+L")]
    [InlineData("Win+Ctrl+Alt+E")]
    public void IsAltGrConflict_True(string text)
        => Assert.True(KeyChord.Parse(text)!.Value.IsAltGrConflict, text);

    [Theory]
    [InlineData("Ctrl+Alt+Up")]
    [InlineData("Ctrl+Shift+P")]
    [InlineData("Alt+Z")]
    [InlineData("Ctrl+Z")]
    [InlineData("Shift+Alt+A")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Ctrl+Alt+F5")]
    [InlineData("Ctrl+Alt+Enter")]
    [InlineData("Win+Shift+N")]
    [InlineData("F3")]
    public void IsAltGrConflict_False(string text)
        => Assert.False(KeyChord.Parse(text)!.Value.IsAltGrConflict, text);

    [Fact]
    public void IsAltGrConflict_EveryCtrlAltLetter_IsConflict_NoOtherKeyIs()
    {
        foreach (var key in Enum.GetValues<Key>())
        {
            var isLetter = key is >= Key.A and <= Key.Z;
            Assert.Equal(isLetter, new KeyChord(ModifierKeys.Control | ModifierKeys.Alt, key).IsAltGrConflict);
            Assert.Equal(isLetter, new KeyChord(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, key).IsAltGrConflict);
            Assert.False(new KeyChord(ModifierKeys.Control, key).IsAltGrConflict);
            Assert.False(new KeyChord(ModifierKeys.Alt, key).IsAltGrConflict);
            Assert.False(new KeyChord(ModifierKeys.Control | ModifierKeys.Shift, key).IsAltGrConflict);
        }
    }

    [Theory]
    [InlineData(Key.LeftCtrl, true)]
    [InlineData(Key.RightCtrl, true)]
    [InlineData(Key.LeftShift, true)]
    [InlineData(Key.RightShift, true)]
    [InlineData(Key.LeftAlt, true)]
    [InlineData(Key.RightAlt, true)]
    [InlineData(Key.LWin, true)]
    [InlineData(Key.RWin, true)]
    [InlineData(Key.A, false)]
    [InlineData(Key.F1, false)]
    public void IsModifierOnly(Key key, bool expected)
        => Assert.Equal(expected, new KeyChord(ModifierKeys.None, key).IsModifierOnly);
}
