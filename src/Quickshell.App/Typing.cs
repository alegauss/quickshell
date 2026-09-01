using System.Windows.Input;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// What the window's keyboard means to a terminal.
///
/// <para><b>Two vocabularies, and the translation is all this is.</b> WPF names every key on the
/// keyboard; <see cref="Quickshell.Terminal.Key"/> names only the ones whose bytes are not simply
/// their character, because a letter goes out as a letter and needs no table. Everything WPF knows
/// that this does not answer for is <see cref="Quickshell.Terminal.Key.None"/>, which encodes to
/// nothing — a modifier pressed on its own is not a keystroke.</para>
///
/// <para><b>The reserved set is here and it is two chords long.</b> <c>Keys</c> says it: every chord
/// the window keeps is one stolen from the program on the far side, so the set stays small and stays
/// written down. An unmodified key belongs to the host, always — which is why both of these carry
/// two modifiers.</para>
/// </summary>
public static class Typing
{
    /// <summary>
    /// The terminal's name for a WPF key, or <c>None</c> where it has none.
    ///
    /// <para>Letters, digits and symbols are absent on purpose: they arrive as text, through the
    /// keyboard layout, and a table here would be this client deciding what a Portuguese keyboard's
    /// dead keys produce.</para>
    /// </summary>
    public static Terminal.Key From(System.Windows.Input.Key key) => key switch
    {
        System.Windows.Input.Key.Up => Terminal.Key.Up,
        System.Windows.Input.Key.Down => Terminal.Key.Down,
        System.Windows.Input.Key.Left => Terminal.Key.Left,
        System.Windows.Input.Key.Right => Terminal.Key.Right,
        System.Windows.Input.Key.Home => Terminal.Key.Home,
        System.Windows.Input.Key.End => Terminal.Key.End,
        System.Windows.Input.Key.Insert => Terminal.Key.Insert,
        System.Windows.Input.Key.Delete => Terminal.Key.Delete,
        System.Windows.Input.Key.PageUp => Terminal.Key.PageUp,
        System.Windows.Input.Key.PageDown => Terminal.Key.PageDown,

        System.Windows.Input.Key.Back => Terminal.Key.Backspace,
        System.Windows.Input.Key.Tab => Terminal.Key.Tab,
        System.Windows.Input.Key.Return => Terminal.Key.Enter,
        System.Windows.Input.Key.Escape => Terminal.Key.Escape,

        System.Windows.Input.Key.F1 => Terminal.Key.F1,
        System.Windows.Input.Key.F2 => Terminal.Key.F2,
        System.Windows.Input.Key.F3 => Terminal.Key.F3,
        System.Windows.Input.Key.F4 => Terminal.Key.F4,
        System.Windows.Input.Key.F5 => Terminal.Key.F5,
        System.Windows.Input.Key.F6 => Terminal.Key.F6,
        System.Windows.Input.Key.F7 => Terminal.Key.F7,
        System.Windows.Input.Key.F8 => Terminal.Key.F8,
        System.Windows.Input.Key.F9 => Terminal.Key.F9,
        System.Windows.Input.Key.F10 => Terminal.Key.F10,
        System.Windows.Input.Key.F11 => Terminal.Key.F11,
        System.Windows.Input.Key.F12 => Terminal.Key.F12,
        System.Windows.Input.Key.F13 => Terminal.Key.F13,
        System.Windows.Input.Key.F14 => Terminal.Key.F14,
        System.Windows.Input.Key.F15 => Terminal.Key.F15,
        System.Windows.Input.Key.F16 => Terminal.Key.F16,
        System.Windows.Input.Key.F17 => Terminal.Key.F17,
        System.Windows.Input.Key.F18 => Terminal.Key.F18,
        System.Windows.Input.Key.F19 => Terminal.Key.F19,
        System.Windows.Input.Key.F20 => Terminal.Key.F20,

        // The pad, which application keypad mode gives sequences of its own. WPF reports these
        // apart from the digits above the letters, and that distinction is the whole point of them.
        System.Windows.Input.Key.Divide => Terminal.Key.KeypadDivide,
        System.Windows.Input.Key.Multiply => Terminal.Key.KeypadMultiply,
        System.Windows.Input.Key.Subtract => Terminal.Key.KeypadSubtract,
        System.Windows.Input.Key.Add => Terminal.Key.KeypadAdd,
        System.Windows.Input.Key.Decimal => Terminal.Key.KeypadDecimal,
        System.Windows.Input.Key.NumPad0 => Terminal.Key.Keypad0,
        System.Windows.Input.Key.NumPad1 => Terminal.Key.Keypad1,
        System.Windows.Input.Key.NumPad2 => Terminal.Key.Keypad2,
        System.Windows.Input.Key.NumPad3 => Terminal.Key.Keypad3,
        System.Windows.Input.Key.NumPad4 => Terminal.Key.Keypad4,
        System.Windows.Input.Key.NumPad5 => Terminal.Key.Keypad5,
        System.Windows.Input.Key.NumPad6 => Terminal.Key.Keypad6,
        System.Windows.Input.Key.NumPad7 => Terminal.Key.Keypad7,
        System.Windows.Input.Key.NumPad8 => Terminal.Key.Keypad8,
        System.Windows.Input.Key.NumPad9 => Terminal.Key.Keypad9,

        _ => Terminal.Key.None,
    };

    /// <summary>What was held, in the terminal's three bits. Windows is not one of them.</summary>
    public static KeyModifiers From(ModifierKeys modifiers)
    {
        KeyModifiers held = KeyModifiers.None;

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            held |= KeyModifiers.Shift;
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            held |= KeyModifiers.Alt;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            held |= KeyModifiers.Control;
        }

        // The Windows key is deliberately absent: nothing in any terminal's key table has a bit for
        // it, and a chord carrying it belongs to the desktop rather than to a session.
        return held;
    }

    /// <summary>
    /// Whether this chord is the window's own and must never reach the host.
    ///
    /// <para>The whole set, written out rather than derived, because it is the list a user is owed
    /// when they ask what this client takes from the program they are running. Both entries carry
    /// two modifiers so that neither collides with anything a terminal program binds.</para>
    /// </summary>
    public static bool Reserved(System.Windows.Input.Key key, ModifierKeys modifiers)
    {
        const ModifierKeys Both = ModifierKeys.Control | ModifierKeys.Shift;

        return (modifiers & Both) == Both
            && key is System.Windows.Input.Key.F1 or System.Windows.Input.Key.I;
    }
}
