using Quickshell.Terminal;

namespace Quickshell.Render;

/// <summary>
/// Whether there is a frame to draw, and the memory of the last one that says so.
///
/// <para><b>This class is where the idle-cost figure is actually won.</b> Everything else in this
/// assembly makes a frame cheap; this is what makes a frame not happen. The block criterion "an idle
/// window issues no draw calls" is a claim about <see cref="Frames"/> not moving, and there is no
/// other place in the renderer that could keep it.</para>
///
/// <para><b>It holds no clock and touches no device.</b> Elapsed time arrives as the cursor's phase,
/// already resolved by <see cref="CursorBlink"/>, so a test can put a window through a whole stream
/// and then a hundred idle ticks without waiting for any of them.</para>
///
/// <para>The gate is deliberately conservative in one direction only: asked about a screen it has
/// never seen, it says draw. There is no state in which it says "nothing to do" about a screen that
/// changed, and every state it is unsure about resolves to one wasted frame rather than to a stale
/// window.</para>
/// </summary>
public sealed class RedrawGate
{
    private Damage _drawn;
    private bool _caret;
    private bool _seen;

    /// <summary>How many frames this gate has authorised, which is the number the criterion reads.</summary>
    public long Frames { get; private set; }

    /// <summary>How many times it has answered that there was nothing to do.</summary>
    public long Skipped { get; private set; }

    /// <summary>
    /// Whether a frame must be drawn, remembering the answer as though it had been.
    ///
    /// <para>Call it once per wake-up and draw when it says so. Calling it twice without drawing
    /// would have the second call answer no, which is correct about the state and wrong about the
    /// window — the gate records what it authorised, not what happened afterwards.</para>
    /// </summary>
    /// <param name="damage">The terminal's own answer about what changed, taken in one go.</param>
    /// <param name="cursorShowing">Whether the blink phase currently has the cursor lit, from
    /// <see cref="CellRenderer.CursorShowing"/>.</param>
    /// <returns>True where a frame is owed.</returns>
    public bool Claim(Damage damage, bool cursorShowing)
    {
        // A cursor the host has hidden does not blink, so its phase must not reach the comparison.
        // Otherwise DECTCEM off would leave the window waking twice a second to draw the same
        // picture, which is the failure this class exists to prevent wearing a different hat.
        bool caret = damage.CursorVisible && cursorShowing;

        if (_seen && damage == _drawn && caret == _caret)
        {
            Skipped++;
            return false;
        }

        _drawn = damage;
        _caret = caret;
        _seen = true;
        Frames++;

        return true;
    }

    /// <summary>
    /// Forgets what was drawn, so the next <see cref="Claim"/> says yes.
    ///
    /// <para>For the changes the terminal knows nothing about: a device lost and recreated, a font or
    /// theme reloaded, a window resized by the user. Each of those leaves the terminal's damage
    /// identical and the picture wrong, and a gate with no way to be told would hold an idle window
    /// on a stale frame until the host next sent a byte.</para>
    /// </summary>
    public void Invalidate() => _seen = false;
}
