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
    /// <summary>
    /// How much is kept for a session still opening (QS249): a few lines of fast typing or a short
    /// paste, and a bound, because a pane whose open hangs must not grow without one.
    /// </summary>
    public const int MaximumHeld = 4096;

    private readonly Emulator _emulator;

    // The UI thread types and a session's open completes on a pool thread; this orders the two, so
    // a key typed while the held ones are being handed over cannot overtake them.
    private readonly Lock _gate = new();
    private Func<ReadOnlyMemory<byte>, ValueTask>? _sending;
    private List<byte>? _held;

    /// <summary>Types into a model, whose modes decide what the keys mean.</summary>
    public Typist(Emulator emulator)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        _emulator = emulator;
    }

    /// <summary>
    /// Where the bytes go: a session's <c>TypeAsync</c>, or null while there is no session.
    ///
    /// <para>Null must not throw, because a user typing into a window that has not connected is not
    /// an error. While <see cref="Hold"/> says a session is on its way, what is typed is kept, and
    /// setting this hands it over first, in order, before anything typed after (QS249).</para>
    /// </summary>
    public Func<ReadOnlyMemory<byte>, ValueTask>? Sending
    {
        get
        {
            lock (_gate)
            {
                return _sending;
            }
        }

        set
        {
            lock (_gate)
            {
                _sending = value;

                if (value is not null && _held is { } held)
                {
                    _held = null;

                    if (held.Count > 0)
                    {
                        Sent++;
                        _ = Deliver(value, held.ToArray());
                    }
                }
            }
        }
    }

    /// <summary>How many keystrokes' bytes were dropped because a session took too long to open (QS249).</summary>
    public long Overflowed { get; private set; }

    /// <summary>
    /// A session is opening into this pane: keep what is typed until <see cref="Sending"/> is set,
    /// rather than dropping it because the host is not there yet (QS249).
    /// </summary>
    public void Hold()
    {
        lock (_gate)
        {
            if (_sending is null)
            {
                _held ??= [];
            }
        }
    }

    /// <summary>The session did not open: what was kept for it goes nowhere, and nothing more is kept.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _held = null;
        }
    }

    /// <summary>
    /// Something was typed, which is how a view scrolled back learns the reading is finished.
    ///
    /// <para>Raised for a key the terminal took, whether or not there was a session to give it to:
    /// a person typing into a window that has not connected has still stopped reading.</para>
    /// </summary>
    public Action? Typed { get; set; }

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

        // In bytes and not in characters, which is QS187: UTF-8 spends up to three bytes on one
        // character of this string, and a buffer counted in characters refused a paste of seventeen
        // accented letters by throwing out of the keystroke handler. The encoder's own bound, plus
        // the room an escape prefix and a key's sequence need.
        byte[] bytes = new byte[Keys.MaximumLength + System.Text.Encoding.UTF8.GetMaxByteCount(text.Length)];
        int written = _emulator.Encode(text, Typing.From(modifiers), bytes);

        return Send(bytes, written);
    }

    private bool Send(byte[] bytes, int written)
    {
        if (written == 0)
        {
            return false;
        }

        Typed?.Invoke();

        lock (_gate)
        {
            if (_sending is { } sending)
            {
                Sent++;

                // Not awaited: this is a UI thread, and a keystroke that blocked it until a socket
                // accepted the write would be a window that stops repainting while the network is
                // slow. The write itself is ordered by the channel behind it, and this lock orders
                // it after anything held for the session.
                _ = Deliver(sending, bytes.AsMemory(0, written));
            }
            else if (_held is { } held)
            {
                // A session on its way (QS249): kept for it, within the bound.
                if (held.Count + written <= MaximumHeld)
                {
                    held.AddRange(bytes.AsSpan(0, written));
                }
                else
                {
                    Overflowed++;
                }
            }

            // Otherwise taken, and dropped: there is no host coming to give it to. It is reported
            // as handled either way, because a window with no session must not let a keystroke fall
            // through to whatever else might be listening.
        }

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
