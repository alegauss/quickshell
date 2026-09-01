using System.Runtime.InteropServices;
using Quickshell.Render;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>Where an input method's windows go, in pixels inside the window that owns the context.</summary>
/// <param name="X">The left edge of the cell the composition has reached.</param>
/// <param name="Y">Its top edge.</param>
/// <param name="Width">One cell wide.</param>
/// <param name="Height">One cell tall, which is what a candidate list is told to avoid covering.</param>
public readonly record struct CandidateSpot(int X, int Y, int Width, int Height);

/// <summary>
/// The input method's own messages, and the call that puts its windows on the cursor.
///
/// <para><b>An input method cannot see a GPU surface.</b> It has no way to inspect what is on the
/// pane, so left alone it draws its candidate list wherever the window's origin happens to be —
/// which for this client is the top-left corner, however far down the screen somebody is typing.
/// Telling it is not a courtesy; converting a grid position into pixels is the one thing only this
/// client can do.</para>
///
/// <para><b>The committed text is not taken here, and that is deliberate.</b> A result string
/// arrives as <c>WM_CHAR</c> immediately afterwards, so WPF raises <c>TextInput</c> and
/// <see cref="Typist"/> sends it down the path every other typed character takes. Reading the
/// result string here as well would send every committed phrase twice, and the second copy would
/// reach the host looking exactly like something the user typed.</para>
///
/// <para><b>Nothing here marks a message handled</b>, for the same reason: WPF's own IME handling
/// is what produces that <c>WM_CHAR</c>, and a hook that consumed the message would be a client
/// where composing works and committing does not.</para>
/// </summary>
public sealed class InputMethod
{
    /// <summary>An input method has begun composing.</summary>
    public const int StartComposition = 0x010D;

    /// <summary>The composition changed, or was committed.</summary>
    public const int Composing = 0x010F;

    /// <summary>Composition is over, whether or not anything was committed.</summary>
    public const int EndComposition = 0x010E;

    /// <summary>GCS_COMPSTR: the message carries the string in progress.</summary>
    private const int InProgress = 0x0008;

    /// <summary>GCS_CURSORPOS: it carries where the caret is inside that string.</summary>
    private const int CaretIn = 0x0080;

    /// <summary>GCS_RESULTSTR: it carries what the user settled on.</summary>
    private const int Result = 0x0800;

    /// <summary>CFS_CANDIDATEPOS, and CFS_EXCLUDE for the rectangle not to cover.</summary>
    private const int AtPoint = 0x0002;
    private const int AtCandidatePoint = 0x0040;
    private const int Excluding = 0x0080;

    /// <summary>The composition being typed, which is display state and never the buffer's.</summary>
    public Composition Composition { get; } = new();

    /// <summary>
    /// Where the input method's windows belong, asked each time the composition moves.
    ///
    /// <para>Null, or answering null, while there is no terminal to place them against — before the
    /// pane has a device there is no cell size, and a position invented without one would be a
    /// number this client made up.</para>
    /// </summary>
    public Func<Composition, CandidateSpot?>? Placing { get; set; }

    /// <summary>How many compositions this has placed, which is what a test counts.</summary>
    public long Placements { get; private set; }

    /// <summary>
    /// The cell a candidate list belongs at, given where the cursor is and how big a cell is.
    ///
    /// <para><b>The arithmetic this whole class exists for, kept apart from the interop so it can be
    /// checked.</b> The grid position comes from <see cref="Composition.Candidate"/>, which counts
    /// cells and not characters — eight Japanese characters are sixteen columns — and this turns it
    /// into pixels inside the window, which means adding where the pane starts: the tab strip is
    /// above it, so the pane's origin is not the window's.</para>
    /// </summary>
    /// <param name="at">The cell, from the composition.</param>
    /// <param name="box">How big one cell is.</param>
    /// <param name="paneLeft">The pane's left edge in the window's client pixels.</param>
    /// <param name="paneTop">Its top edge.</param>
    public static CandidateSpot SpotFor(CandidatePlacement at, CellMetrics box, int paneLeft,
                                        int paneTop) =>
        new(paneLeft + (at.Column * box.Width), paneTop + (at.Row * box.Height),
            box.Width, box.Height);

    /// <summary>
    /// Handles one of the input method's messages, and never says it handled it.
    /// </summary>
    /// <param name="window">The window the context belongs to, which is the one with focus.</param>
    /// <param name="message">What arrived.</param>
    /// <param name="low">Its lParam, which for a composition is which strings it carries.</param>
    /// <returns>True where this was a message about composing, for a caller that counts them.</returns>
    public bool Handle(nint window, int message, nint low)
    {
        switch (message)
        {
            case StartComposition:
                Composition.Start();
                Place(window);

                return true;

            case Composing:
                Compose(window, (int)low);

                return true;

            case EndComposition:
                // Nothing to erase, because nothing was ever written into the buffer. That is the
                // whole reason Composition holds this apart from the model.
                Composition.Cancel();

                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Reads whatever the message carries and moves the windows to match.
    ///
    /// <para>The result string ends the composition here and is not read: it is on its way as
    /// <c>WM_CHAR</c>, and this client's job is to stop drawing what is no longer being typed.</para>
    /// </summary>
    private void Compose(nint window, int carrying)
    {
        if ((carrying & Result) != 0)
        {
            Composition.Cancel();

            return;
        }

        if ((carrying & InProgress) == 0)
        {
            return;
        }

        nint context = ImmGetContext(window);

        if (context == 0)
        {
            return;
        }

        try
        {
            // Bytes, not characters: this is the wide API and the length it answers with is the
            // buffer size it wants. A caller that treated it as a character count would ask for half
            // the string and get half a name.
            int bytes = ImmGetCompositionStringW(context, InProgress, null, 0);

            if (bytes < 0)
            {
                return;
            }

            char[] text = new char[(bytes / sizeof(char)) + 1];

            bytes = ImmGetCompositionStringW(context, InProgress, text, (uint)(bytes + sizeof(char)));

            int caret = ImmGetCompositionStringW(context, CaretIn, null, 0);

            Composition.Update(text.AsSpan(0, Math.Max(0, bytes / sizeof(char))),
                               Math.Max(0, caret));
        }
        finally
        {
            ImmReleaseContext(window, context);
        }

        Place(window);
    }

    /// <summary>
    /// Tells the input method where the cursor is, in the two forms it asks in.
    ///
    /// <para>The composition window takes a point, which is where an input method that draws the
    /// text in progress puts it. The candidate window takes a point <em>and</em> a rectangle to
    /// avoid — the cell itself — so the list appears beside what is being typed rather than on top
    /// of it.</para>
    /// </summary>
    private void Place(nint window)
    {
        if (Placing?.Invoke(Composition) is not { } spot)
        {
            return;
        }

        nint context = ImmGetContext(window);

        if (context == 0)
        {
            return;
        }

        try
        {
            CompositionForm composing = new()
            {
                Style = AtPoint,
                Position = new Point { X = spot.X, Y = spot.Y },
            };

            ImmSetCompositionWindow(context, ref composing);

            CandidateForm candidates = new()
            {
                Index = 0,
                Style = AtCandidatePoint | Excluding,
                Position = new Point { X = spot.X, Y = spot.Y },
                Area = new Rectangle
                {
                    Left = spot.X,
                    Top = spot.Y,
                    Right = spot.X + spot.Width,
                    Bottom = spot.Y + spot.Height,
                },
            };

            ImmSetCandidateWindow(context, ref candidates);

            Placements++;
        }
        finally
        {
            ImmReleaseContext(window, context);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionForm
    {
        public uint Style;
        public Point Position;
        public Rectangle Area;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateForm
    {
        public uint Index;
        public uint Style;
        public Point Position;
        public Rectangle Area;
    }

    [DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint window);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(nint window, nint context);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCompositionStringW(nint context, int wanted, char[]? into,
                                                       uint bytes);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetCompositionWindow(nint context, ref CompositionForm form);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetCandidateWindow(nint context, ref CandidateForm form);
}
