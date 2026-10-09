namespace Quickshell.Terminal;

/// <summary>
/// What DECSC saves: the position, the pen, the character sets and origin mode. A value, so saving
/// it is a copy and the live state cannot be saved by reference (QS239).
/// </summary>
internal readonly record struct SavedCursor(
    int Row,
    int Column,
    Pen Pen,
    CharacterSet G0,
    CharacterSet G1,
    int ActiveSet,
    bool OriginMode)
{
    /// <summary>What a screen with nothing saved restores: the top left, the default pen, ASCII, origin mode off.</summary>
    public static SavedCursor Home => new(0, 0, Pen.Default, CharacterSet.Ascii, CharacterSet.Ascii, 0, false);
}

public sealed partial class Emulator
{
    /// <summary>How many private modes XTSAVE keeps, so a host cannot grow the table without bound.</summary>
    public const int MaximumSavedModes = 64;

    // XTSAVE's table, made the first time a host saves anything.
    private Dictionary<int, bool>? _savedModes;

    /// <summary>
    /// XTSAVE, <c>CSI ? Pm s</c>: saves each named private mode this client can report as set or
    /// reset. A mode it does not have, or one it refuses, has no state to save and is skipped.
    /// </summary>
    private void SaveModes(in CsiParameters parameters)
    {
        for (int group = 0; group < parameters.Count; group++)
        {
            int mode = parameters.Value(group, -1);
            ModeState state = DecModeState(mode);

            if (state is not (ModeState.Set or ModeState.Reset))
            {
                Unhandled++;
                continue;
            }

            _savedModes ??= [];

            if (_savedModes.Count < MaximumSavedModes || _savedModes.ContainsKey(mode))
            {
                _savedModes[mode] = state == ModeState.Set;
            }
        }
    }

    /// <summary>XTRESTORE, <c>CSI ? Pm r</c>: sets each named mode back as XTSAVE found it, and leaves one never saved alone.</summary>
    private void RestoreModes(in CsiParameters parameters)
    {
        for (int group = 0; group < parameters.Count; group++)
        {
            int mode = parameters.Value(group, -1);

            if (_savedModes is not null && _savedModes.TryGetValue(mode, out bool set))
            {
                SetPrivateMode(mode, set);
            }
        }
    }
}
