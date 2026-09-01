using System.Windows.Input;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// The route from a key on a window to the bytes a host reads.
///
/// <para><b>Two events and not one, because a keyboard has two kinds of key.</b> A letter, a digit
/// and anything a dead key composes arrive as text that Windows has already resolved through the
/// layout — this client has no business deciding what a Portuguese keyboard produces. Everything
/// with no character of its own arrives as a key, and what it sends depends on modes the host has
/// set, which is why <see cref="Emulator.Encode(Terminal.Key, KeyModifiers, System.Span{byte})"/>
/// answers rather than a table here.</para>
///
/// <para><b>The window's own chords are declined before anything is encoded.</b> Not after: a chord
/// that reached here, was encoded and then discarded would be one wrong edit away from being sent,
/// and what it would send is a control sequence into somebody's shell.</para>
///
/// <para><b>Each keystroke gets its own bytes.</b> A reused buffer would be handed to an
/// asynchronous write and then overwritten by the next key while that write was still reading it.
/// Sixteen bytes at the speed a person types is not a cost worth a race.</para>
/// </summary>
public sealed class Typist
{
    private readonly Emulator _emulator;

    /// <summary>Types into a model, whose modes decide what the keys mean.</summary>
    public Typist(Emulator emulator)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        _emulator = emulator;
    }

    /// <summary>
    /// Where the bytes go: a session's <c>TypeAsync</c>, or null while there is no session.
    ///
    /// <para>Null is the shipped client's state today, and it is not a stub — there is nowhere for a
    /// keystroke to go until QS126 gives this window a connection. What null must not do is throw,
    /// because a user typing into a window that has not connected is not an error.</para>
    /// </summary>
    public Func<ReadOnlyMemory<byte>, ValueTask>? Sending { get; set; }

    /// <summary>How many keystrokes have been encoded to something and sent.</summary>
    public long Sent { get; private set; }

    /// <summary>How many were declined because the window had reserved the chord.</summary>
    public long Declined { get; private set; }

    /// <summary>
    /// A key with no character of its own — an arrow, a function key, Enter, Escape.
    /// </summary>
    /// <param name="key">The key, with Alt chords already resolved past <c>Key.System</c>.</param>
    /// <param name="modifiers">What was held.</param>
    /// <returns>True where this was the terminal's to take, which is what marks the event handled.</returns>
    public bool Press(System.Windows.Input.Key key, ModifierKeys modifiers)
    {
        if (Typing.Reserved(key, modifiers))
        {
            Declined++;

            return false;
        }

        Terminal.Key named = Typing.From(key);

        if (named == Terminal.Key.None)
        {
            return false;
        }

        byte[] bytes = new byte[Keys.MaximumLength];
        int written = _emulator.Encode(named, Typing.From(modifiers), bytes);

        return Send(bytes, written);
    }

    /// <summary>
    /// A character the window resolved.
    /// </summary>
    /// <param name="text">
    /// What Windows produced. For a control chord this is the control character itself — WPF puts
    /// it in <c>ControlText</c> rather than in <c>Text</c>, and a caller that read only the latter
    /// would find that control-C sent nothing.
    /// </param>
    /// <param name="modifiers">What was held, which for text only the alt setting cares about.</param>
    /// <returns>True where something was sent.</returns>
    public bool Type(string text, ModifierKeys modifiers)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        byte[] bytes = new byte[Keys.MaximumLength + text.Length];
        int written = _emulator.Encode(text, Typing.From(modifiers), bytes);

        return Send(bytes, written);
    }

    private bool Send(byte[] bytes, int written)
    {
        if (written == 0)
        {
            return false;
        }

        Func<ReadOnlyMemory<byte>, ValueTask>? sending = Sending;

        if (sending is null)
        {
            // Taken, and dropped. The key was the terminal's; there is no host to give it to. It is
            // reported as handled either way, because a window with no session must not let a
            // keystroke fall through to whatever else might be listening.
            return true;
        }

        Sent++;

        // Not awaited: this is a UI thread, and a keystroke that blocked it until a socket accepted
        // the write would be a window that stops repainting while the network is slow. The write
        // itself is ordered by the channel behind it.
        _ = Deliver(sending, bytes.AsMemory(0, written));

        return true;
    }

    private static async Task Deliver(Func<ReadOnlyMemory<byte>, ValueTask> sending,
                                      ReadOnlyMemory<byte> bytes)
    {
        try
        {
            await sending(bytes).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A keystroke into a connection that has gone is not a crash. The session's own end is
            // what reports that, and it is already on its way to the user by then.
        }
    }
}
