using Quickshell.App;
using Quickshell.Render;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The candidate window's position, which is QS29's falsification: <em>falsified when the candidate
/// window appears anywhere other than at the cursor</em>.
///
/// <para><b>The arithmetic and the message dispatch, and not the interop.</b> Calling
/// <c>ImmSetCandidateWindow</c> takes an input method actually installed and composing, which no
/// build agent has; what a test can hold is everything this client decides before it makes that
/// call, and every way of getting it wrong is in there — a character count instead of a cell count,
/// a window origin instead of the pane's, a composition that outlives its commit.</para>
/// </summary>
public sealed class InputMethodTests
{
    /// <summary>
    /// A cell 8 wide and 17 tall, which is roughly a monospace face at 12 points and is deliberately
    /// not square: a transposed multiplication is invisible against a square cell.
    /// </summary>
    private static readonly CellMetrics Box = new(8, 17, 13);

    /// <summary>
    /// The candidate list lands on the cursor's own cell when nothing has been composed yet.
    /// </summary>
    [Fact]
    public void WithNothingComposedTheListIsOnTheCursor()
    {
        Composition composing = new();

        composing.Start();

        CandidateSpot spot = InputMethod.SpotFor(composing.Candidate(10, 4, 80), Box, 0, 0);

        Assert.Equal(80, spot.X);
        Assert.Equal(68, spot.Y);

        // One cell, which is the rectangle the list is told not to cover.
        Assert.Equal(8, spot.Width);
        Assert.Equal(17, spot.Height);
    }

    /// <summary>
    /// Japanese moves the list two columns per character, because that is how wide it is.
    ///
    /// <para><b>The failure this exists to catch is a list placed by counting characters.</b> Four
    /// characters is eight columns, and a client counting characters would put the list four columns
    /// back, in the middle of what the user is reading.</para>
    /// </summary>
    [Fact]
    public void WideCharactersMoveTheListByCellsAndNotByCharacters()
    {
        Composition composing = new();

        composing.Start();
        composing.Update("にほんご", 4);

        Assert.Equal(8, composing.CellsBeforeCaret);

        CandidateSpot spot = InputMethod.SpotFor(composing.Candidate(10, 4, 80), Box, 0, 0);

        // Ten columns of cursor plus eight of composition, and not ten plus four.
        Assert.Equal(18 * 8, spot.X);
        Assert.Equal(4 * 17, spot.Y);
    }

    /// <summary>
    /// The pane's own corner is added, because the tab strip is above it.
    ///
    /// <para>A position measured from the window's corner is right exactly while there is one tab,
    /// which is the state every screenshot is taken in and the reason this would ship broken.</para>
    /// </summary>
    [Fact]
    public void ThePanesOriginIsAddedAndNotTheWindows()
    {
        Composition composing = new();

        composing.Start();

        CandidateSpot spot = InputMethod.SpotFor(composing.Candidate(3, 2, 80), Box, 0, 34);

        Assert.Equal(3 * 8, spot.X);
        Assert.Equal((2 * 17) + 34, spot.Y);
    }

    /// <summary>
    /// A composition that runs off the right edge follows onto the next row, as the text does.
    /// </summary>
    [Fact]
    public void ACompositionThatWrapsTakesTheListWithIt()
    {
        Composition composing = new();

        composing.Start();
        composing.Update("abcde", 5);

        // Column 78 of an 80-wide screen, plus five: three cells past the edge.
        CandidateSpot spot = InputMethod.SpotFor(composing.Candidate(78, 4, 80), Box, 0, 0);

        Assert.Equal(3 * 8, spot.X);
        Assert.Equal(5 * 17, spot.Y);
    }

    /// <summary>
    /// The three messages this client answers, and what each does to the composition.
    ///
    /// <para><b>The result string ends the composition and is not read.</b> It is on its way as a
    /// <c>WM_CHAR</c> that WPF turns into text input, so a client reading it here as well would send
    /// every committed phrase twice — and the second copy would reach the host looking exactly like
    /// something the user typed.</para>
    /// </summary>
    [Fact]
    public void CommittingEndsTheCompositionWithoutTakingTheText()
    {
        InputMethod input = new();

        Assert.True(input.Handle(0, InputMethod.StartComposition, 0));
        Assert.True(input.Composition.IsActive);

        // GCS_RESULTSTR, which is what an input method sends when the user has chosen.
        Assert.True(input.Handle(0, InputMethod.Composing, 0x0800));

        Assert.False(input.Composition.IsActive);
        Assert.True(input.Composition.Text.IsEmpty);
    }

    /// <summary>Ending composition leaves nothing behind, which is the model's whole claim.</summary>
    [Fact]
    public void EndingCompositionLeavesNothingBehind()
    {
        InputMethod input = new();

        input.Handle(0, InputMethod.StartComposition, 0);

        Assert.True(input.Handle(0, InputMethod.EndComposition, 0));
        Assert.False(input.Composition.IsActive);

        // And a message that is nothing to do with composing is not this client's.
        Assert.False(input.Handle(0, 0x0100, 0));
    }

    /// <summary>
    /// Starting a composition asks where it goes, and takes null for an answer.
    ///
    /// <para>Null is the state the client is in before the pane has a device, and it has to cost
    /// nothing: an input method invoked during start-up must not be a crash on a path a user reaches
    /// by typing.</para>
    /// </summary>
    [Fact]
    public void PlacingIsAskedAndNullIsAnAnswer()
    {
        int asked = 0;

        InputMethod input = new()
        {
            Placing = _ =>
            {
                asked++;

                return null;
            },
        };

        input.Handle(0, InputMethod.StartComposition, 0);

        Assert.Equal(1, asked);
        Assert.Equal(0, input.Placements);

        // And with no delegate at all, which is a window nobody has wired a terminal to.
        InputMethod bare = new();

        bare.Handle(0, InputMethod.StartComposition, 0);

        Assert.Equal(0, bare.Placements);
    }

    /// <summary>
    /// The calls this makes into <c>imm32</c> are made, against a real window, and come back.
    ///
    /// <para><b>What this catches is a struct laid out wrong</b>, which is the classic way an
    /// interop like this fails: it compiles, it passes every test above, and the first person to
    /// type Japanese gets a candidate list in the wrong place or a corrupted stack. The only way to
    /// find that is to let Windows read the structure.</para>
    ///
    /// <para><b>It does not assert that a candidate window appeared</b>, and it must not: that takes
    /// an input method installed and composing, and this machine has one language with a US
    /// keyboard. What is asserted is that a real window's context was obtained and both calls
    /// returned — which is the part a build agent can honestly hold.</para>
    /// </summary>
    [Fact]
    public void TheCallsIntoTheInputMethodAreMadeAgainstARealWindow()
    {
        long placed = OnStaThread(() =>
        {
            MainWindow window = new();

            window.Show();

            // A cell the arithmetic above has already been checked against, so what is under test
            // here is only the crossing into Windows.
            window.Input.Placing = composing =>
                InputMethod.SpotFor(composing.Candidate(10, 4, 80), Box, 0, 34);

            nint handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

            Assert.NotEqual(nint.Zero, handle);

            window.Input.Handle(handle, InputMethod.StartComposition, 0);

            long times = window.Input.Placements;

            window.Close();

            return times;
        });

        Assert.Equal(1, placed);
    }

    /// <summary>
    /// Runs something on an STA thread, which is the only kind a WPF window can be built on.
    /// </summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failed = error;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failed is not null)
        {
            throw new InvalidOperationException("the work on the STA thread failed", failed);
        }

        return result;
    }
}
