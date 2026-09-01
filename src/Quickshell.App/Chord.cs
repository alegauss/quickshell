using System.Windows.Input;

namespace Quickshell.App;

/// <summary>
/// What a key and its modifiers are called, in the one spelling a user is shown.
///
/// <para><b>QS162: this exists so the list can be checked rather than remembered.</b> Every chord
/// this client takes is a chord the program on the far side never sees, and the only thing that
/// stops that list drifting from the reference is a test that renders the real bindings and compares
/// them. Rendering them means agreeing on how a chord is written down.</para>
///
/// <para><b>The palette is the second reader.</b> QS52 needs the same strings beside the same
/// actions, and a palette that spelled them differently from the reference would be a third opinion
/// about what a user pressed.</para>
/// </summary>
public static class Chord
{
    /// <summary>
    /// How this chord is written: modifiers in Ctrl, Alt, Shift order, then the key.
    ///
    /// <para>The order is fixed rather than the order they were declared in, because a chord is a
    /// name and two spellings of one name are two names.</para>
    /// </summary>
    /// <param name="key">The key itself.</param>
    /// <param name="modifiers">What is held down with it.</param>
    public static string Naming(Key key, ModifierKeys modifiers)
    {
        List<string> parts = [];

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(Named(key));

        return string.Join('+', parts);
    }

    /// <summary>
    /// The key on its own, by the character on it where there is one.
    ///
    /// <para>WPF's names for the punctuation are about where the key sits on a keyboard rather than
    /// what is printed on it — <c>OemMinus</c> and <c>Oem5</c> mean nothing to somebody reading a
    /// reference to find out what to press. Note that <c>OemBackslash</c> and <c>Oem5</c> are the
    /// same character on different layouts, so they are deliberately the same name here: they are
    /// one chord that had to be bound twice.</para>
    ///
    /// <para><b>The page keys are named twice in the enum and print inconsistently.</b>
    /// <c>Key.PageDown</c> is the same value as <c>Key.Next</c> and comes out of
    /// <c>ToString</c> as <em>Next</em>, while <c>Key.PageUp</c> comes out as itself — so the two
    /// halves of one gesture would be spelt in two different worlds. Both are named here.</para>
    /// </summary>
    private static string Named(Key key) => key switch
    {
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.OemBackslash or Key.Oem5 => "\\",
        Key.OemMinus or Key.Subtract => "-",
        Key.OemPlus or Key.Add => "+",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString(),
    };
}
