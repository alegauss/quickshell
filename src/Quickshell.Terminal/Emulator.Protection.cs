namespace Quickshell.Terminal;

/// <summary>Which kind of character protection the host last asked for, which is xterm's model of it.</summary>
internal enum Protection : byte
{
    /// <summary>Never asked for: no erase skips anything.</summary>
    Off,

    /// <summary>DECSCA: the selective erases skip a protected cell, and the ordinary ones do not.</summary>
    Dec,

    /// <summary>SPA and EPA: every erase skips a protected cell except DECSERA.</summary>
    Iso,
}

public sealed partial class Emulator
{
    private Protection _protection;

    /// <summary>
    /// DECSCA, <c>CSI Ps " q</c>: 1 protects what is printed next, 0 and 2 stop protecting it.
    ///
    /// <para><b>One bit, two meanings, as xterm has it.</b> A cell is protected or it is not; what
    /// differs between DECSCA and SPA is which erases respect the bit, and that is the mode the
    /// last of them set rather than something each cell remembers (QS235).</para>
    /// </summary>
    private void SelectCharacterProtection(int mode)
    {
        _protection = Protection.Dec;

        switch (mode)
        {
            case 1:
                _pen = _pen.Set(CellFlags.Protected);
                break;

            case 0:
            case 2:
                _pen = _pen.Clear(CellFlags.Protected);
                break;

            default:
                Unhandled++;
                break;
        }
    }

    /// <summary>
    /// What DECSTR and RIS do to protection: nothing printed next is protected, and no erase skips
    /// a cell that already is. Without it a protected area outlives the program that drew it,
    /// which is how one esctest case's leftovers survived the next one's clear. The saved cursor's
    /// pen goes with the rest of the saved cursor, which DECSTR resets whole.
    /// </summary>
    private void ResetProtection()
    {
        _protection = Protection.Off;
        _pen = _pen.Clear(CellFlags.Protected);
    }

    /// <summary>SPA, <c>ESC V</c>, and EPA, <c>ESC W</c>: the start and end of an ISO protected area.</summary>
    private void ProtectedArea(bool start)
    {
        if (start)
        {
            _protection = Protection.Iso;
            _pen = _pen.Set(CellFlags.Protected);
        }
        else
        {
            _pen = _pen.Clear(CellFlags.Protected);
        }
    }

    /// <summary>
    /// Whether an erase leaves protected cells alone: a selective one (DECSED, DECSEL) under either
    /// kind of protection, and an ordinary one (ED, EL, ECH) under ISO protection only. DECSEL and
    /// DECSED respecting ISO protection too is xterm's, kept for compatibility, and esctest records
    /// it as xterm's known bug rather than as a failure.
    /// </summary>
    private bool KeepsProtected(bool selective) =>
        selective ? _protection != Protection.Off : _protection == Protection.Iso;

    /// <summary>DECSCA as DECRQSS reports it: 1 only while DEC protection is what is being printed.</summary>
    private int ProtectionReport =>
        _protection == Protection.Dec && (_pen.Flags & CellFlags.Protected) != 0 ? 1 : 0;

    /// <summary>
    /// DECSERA, <c>CSI Pt ; Pl ; Pb ; Pr $ {</c>: erases the rectangle's unprotected cells. It
    /// ignores the margins and follows origin mode, like every rectangle operation, and it respects
    /// DEC protection only - an ISO protected cell is erased (QS235).
    /// </summary>
    private void SelectiveEraseRectangle(in CsiParameters parameters)
    {
        if (!Rectangle(parameters, 0, out int top, out int left, out int bottom, out int right))
        {
            return;
        }

        bool keep = _protection == Protection.Dec;

        for (int row = top; row <= bottom; row++)
        {
            Buffer.Clear(row, left, right - left + 1, _pen.Background, keep);
        }
    }
}
