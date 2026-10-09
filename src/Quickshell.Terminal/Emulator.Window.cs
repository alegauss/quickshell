namespace Quickshell.Terminal;

public sealed partial class Emulator
{
    /// <summary>How many titles the stack holds, which is xterm's depth; a push past it drops the oldest.</summary>
    public const int MaximumTitleStack = 10;

    private readonly List<(string? Icon, string? Window)> _titles = [];

    // A cell's size in pixels, packed as height above width so the window thread can set it and
    // the parse thread read it whole. Zero until a window that draws this emulator says.
    private long _cellPixels;

    /// <summary>
    /// The icon label, as the host set it with OSC 0 or OSC 1. Nothing draws it: it is held so the
    /// title stack can save and restore it as xterm does (QS236).
    /// </summary>
    internal string IconTitle { get; private set; } = string.Empty;

    /// <summary>How deep the title stack is.</summary>
    internal int TitleStackDepth => _titles.Count;

    /// <summary>
    /// Tells the emulator how large one cell is drawn, in pixels, so CSI 14 t and CSI 16 t can answer
    /// with the pane's real geometry. A headless emulator never calls it and answers zero.
    /// </summary>
    public void UseCellPixels(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        Volatile.Write(ref _cellPixels, ((long)height << 32) | (uint)width);
    }

    /// <summary>
    /// CSI t, xterm's window operations (QS236).
    ///
    /// <para><b>The reports are answered, every one, and describe the pane.</b> A host asking is
    /// blocked until an answer comes, so silence costs it a timeout. The pane is the whole screen
    /// a host can see, so the screen's size is the pane's, its position is the origin, and it is
    /// never iconified.</para>
    ///
    /// <para><b>Reports 20 and 21 are the attack QS19 was about.</b> They ask for the icon label and
    /// the window title, and a host that has just set the title can use them to have the terminal
    /// type its own text at the shell. They are answered, so nobody waits, and the answer is empty.</para>
    ///
    /// <para><b>The manipulations are refused</b>: iconify, move, resize, raise, lower, maximise,
    /// fullscreen and DECSLPP. A remote program does not get to move or size a local window, which
    /// is a non-goal with its reason rather than an omission.</para>
    /// </summary>
    private void WindowOperation(in CsiParameters parameters)
    {
        long packed = Volatile.Read(ref _cellPixels);
        int cellWidth = (int)(uint)packed;
        int cellHeight = (int)(packed >> 32);
        int rows = Buffer.Rows;
        int columns = Buffer.Columns;

        switch (parameters.Value(0, 0))
        {
            case 11:
                Send(Answer.WindowReport, 1);
                break;

            case 13:
                Send(Answer.WindowReport, 3, 0, 0);
                break;

            case 14:
                Send(Answer.WindowReport, 4, rows * cellHeight, columns * cellWidth);
                break;

            case 15:
                Send(Answer.WindowReport, 5, rows * cellHeight, columns * cellWidth);
                break;

            case 16:
                Send(Answer.WindowReport, 6, cellHeight, cellWidth);
                break;

            case 18:
                Send(Answer.WindowReport, 8, rows, columns);
                break;

            case 19:
                Send(Answer.WindowReport, 9, rows, columns);
                break;

            case 20:
                Send(Answer.EmptyLabel, 1);
                break;

            case 21:
                Send(Answer.EmptyLabel, 2);
                break;

            case 22:
                PushTitle(parameters.Value(1, 0));
                break;

            case 23:
                PopTitle(parameters.Value(1, 0));
                break;

            default:
                Unhandled++;
                break;
        }
    }

    /// <summary>
    /// CSI 22 ; Ps t: saves the icon label (1), the window title (2) or both (0).
    ///
    /// <para><b>A push of one fills the other half of the entry on top</b> when that half is empty,
    /// as xterm's does, so pushing the icon and then the window and popping both restores both.</para>
    /// </summary>
    private void PushTitle(int which)
    {
        bool icon = which is 0 or 1;
        bool window = which is 0 or 2;

        if (!icon && !window)
        {
            Unhandled++;
            return;
        }

        if (_titles.Count > 0 && which != 0)
        {
            (string? Icon, string? Window) top = _titles[^1];

            if (icon && top.Icon is null)
            {
                _titles[^1] = top with { Icon = IconTitle };
                return;
            }

            if (window && top.Window is null)
            {
                _titles[^1] = top with { Window = Title };
                return;
            }
        }

        if (_titles.Count == MaximumTitleStack)
        {
            _titles.RemoveAt(0);
        }

        _titles.Add((icon ? IconTitle : null, window ? Title : null));
    }

    /// <summary>
    /// CSI 23 ; Ps t: takes the top entry off the stack and restores the halves asked for that it
    /// holds. The entry goes whole, so popping the icon of an entry that saved both leaves nothing
    /// for a later pop of the window.
    /// </summary>
    private void PopTitle(int which)
    {
        if (which is not (0 or 1 or 2))
        {
            Unhandled++;
            return;
        }

        if (_titles.Count == 0)
        {
            return;
        }

        (string? icon, string? window) = _titles[^1];
        _titles.RemoveAt(_titles.Count - 1);

        if (which is 0 or 1 && icon is not null)
        {
            IconTitle = icon;
        }

        if (which is 0 or 2 && window is not null)
        {
            Title = window;
        }
    }

    /// <summary>What RIS does to the titles: both empty and nothing saved.</summary>
    private void ResetTitles()
    {
        Title = string.Empty;
        IconTitle = string.Empty;
        _titles.Clear();
    }
}
