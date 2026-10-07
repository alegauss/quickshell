using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Quickshell.App;

/// <summary>
/// A chord on a punctuation key, matched by the character the key types and not by where the key is
/// (QS171).
///
/// <para><b>The Oem keys are positions, and positions move.</b> Letters, digits and named keys are the
/// same <see cref="Key"/> everywhere; the Oem range is defined by where a key sits on a US keyboard,
/// and every layout that differs remaps it. So <c>Key.OemMinus</c> is not "the minus key" — on some
/// layouts it types something else, and the minus is elsewhere. A chord bound to it fails there, and
/// fails quietly: the client takes nothing and the character goes to the remote program, so the
/// split did not happen and something was typed.</para>
///
/// <para><b>So the chord names a character</b>, and a key press matches it where the active layout
/// says that key, unshifted, types that character. One binding per chord, on every layout — instead
/// of a binding per position somebody happened to think of. Where a layout puts the character behind
/// AltGr the chord does not exist there, which the keys reference says, and the palette still has
/// the command.</para>
/// </summary>
public sealed class CharacterGesture : InputGesture
{
    /// <summary>MAPVK_VK_TO_CHAR: the unshifted character a virtual key types.</summary>
    private const uint ToCharacter = 2;

    /// <summary>A chord on the key that types <paramref name="character"/>, with these held.</summary>
    public CharacterGesture(char character, ModifierKeys modifiers)
    {
        Character = character;
        Modifiers = modifiers;
    }

    /// <summary>The character the key types, unshifted.</summary>
    public char Character { get; }

    /// <summary>What is held with it.</summary>
    public ModifierKeys Modifiers { get; }

    /// <summary>How the chord is written, in <see cref="Chord"/>'s spelling.</summary>
    public string Name => Chord.Naming(Modifiers, Character.ToString());

    /// <inheritdoc/>
    public override bool Matches(object targetElement, InputEventArgs inputEventArgs)
    {
        if (inputEventArgs is not KeyEventArgs pressed || !pressed.IsDown)
        {
            return false;
        }

        Key key = pressed.Key == Key.System ? pressed.SystemKey : pressed.Key;

        return Keyboard.Modifiers == Modifiers && Typed(key, GetKeyboardLayout(0)) == Character;
    }

    /// <summary>
    /// The character a key types unshifted on a keyboard layout, or none where it types nothing — a
    /// dead key included, which types nothing on its own.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="layout">The layout's handle, as Windows names one.</param>
    public static char? Typed(Key key, nint layout)
    {
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);

        if (virtualKey == 0)
        {
            return null;
        }

        uint mapped = MapVirtualKeyExW((uint)virtualKey, ToCharacter, layout);

        // The top bit marks a dead key, and zero is a key that types nothing at all.
        return mapped == 0 || (mapped & 0x80000000) != 0 ? null : (char)(mapped & 0xFFFF);
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyExW(uint code, uint mapType, nint layout);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint thread);
}
