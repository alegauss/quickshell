using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The tree a tab holds, and its one hard part: moving the focus by direction.
///
/// <para><b>QS48's falsification is here and it is arithmetic.</b> <em>Falsified when directional
/// focus movement follows tree order instead of screen position.</em> A model that walked the tree
/// would answer nearly every one of these correctly, which is exactly why the shapes below are the
/// ones where the two disagree — a sibling that is a split rather than a pane, and a tall pane with
/// two stacked beside it.</para>
///
/// <para>Nothing here opens a window or a device. The tree holds numbers, and what a number turns
/// into is somebody else's business.</para>
/// </summary>
public sealed class PaneLayoutTests
{
    /// <summary>A tab starts as one pane filling everything.</summary>
    [Fact]
    public void ATabStartsAsOnePaneFillingIt()
    {
        PaneLayout layout = new();

        Assert.Equal(1, layout.Count);
        Assert.Equal(new Portion(0, 0, 1, 1), layout.Portions[layout.First]);

        // And the last pane cannot be closed, because a tab with no terminal is not a thing.
        Assert.Equal(-1, layout.Close(layout.First));
    }

    /// <summary>Splitting halves the space, and splitting again halves one of the halves.</summary>
    [Fact]
    public void SplittingHalvesTheSpaceItWasGiven()
    {
        PaneLayout layout = new();

        int left = layout.First;
        int right = layout.Split(left, Divide.Beside);

        // The id that was split became the split itself, so the terminal in it moved to a new one.
        left = layout.Panes[0];

        Assert.Equal(new Portion(0, 0, 0.5, 1), layout.Portions[left]);
        Assert.Equal(new Portion(0.5, 0, 0.5, 1), layout.Portions[right]);

        int lower = layout.Split(right, Divide.Below);

        right = layout.Portions.Keys.Single(pane => pane != left && pane != lower
                                                    && layout.Portions[pane].Y == 0
                                                    && layout.Portions[pane].X > 0);

        Assert.Equal(new Portion(0.5, 0, 0.5, 0.5), layout.Portions[right]);
        Assert.Equal(new Portion(0.5, 0.5, 0.5, 0.5), layout.Portions[lower]);

        // The left one is untouched, which is what proportions inside a subtree buys.
        Assert.Equal(new Portion(0, 0, 0.5, 1), layout.Portions[left]);
    }

    /// <summary>
    /// Moving right out of a tall pane lands on the pane level with it, not on whichever child the
    /// tree happens to hold first.
    ///
    /// <para><b>This is the falsification.</b> The arrangement is one tall pane on the left and two
    /// stacked on the right. The tall pane's sibling in the tree is the <em>split</em> holding both
    /// of the others, so tree order has no answer at all and would have to pick one — and the one it
    /// would pick is the first child, which is the upper. From the bottom of the tall pane, the pane
    /// on screen to the right is the lower one.</para>
    /// </summary>
    [Fact]
    public void MovingRightLandsOnThePaneOnScreenAndNotTheFirstChild()
    {
        PaneLayout layout = new();

        int tall = layout.First;
        int right = layout.Split(tall, Divide.Beside);

        tall = layout.Panes[0];

        int lower = layout.Split(right, Divide.Below);

        int upper = layout.Portions.Keys.Single(pane => pane != tall && pane != lower);

        // The tall pane spans the whole height, so its middle is level with the join between the
        // two on the right — and the tie is broken by which one the middle actually falls inside.
        Assert.Equal(new Portion(0, 0, 0.5, 1), layout.Portions[tall]);

        // Now make the tall pane short and low, so there is no tie at all: it is unambiguously
        // level with the lower one.
        layout.Share(tall, 0.5);

        int bottomLeft = layout.Split(tall, Divide.Below);

        tall = layout.Portions.Keys.Single(pane => pane != bottomLeft && pane != upper
                                                   && pane != lower);

        Assert.Equal(new Portion(0, 0.5, 0.5, 0.5), layout.Portions[bottomLeft]);

        // From the bottom-left pane, right is the lower one on the right — never the upper, which is
        // the first child of the split that is the sibling here.
        Assert.Equal(lower, layout.Beyond(bottomLeft, Toward.Right));

        // And from the top-left it is the upper one, by the same rule read the other way.
        Assert.Equal(upper, layout.Beyond(tall, Toward.Right));
    }

    /// <summary>There is nothing beyond the edge of the tab, and it says so rather than wrapping.</summary>
    [Fact]
    public void ThereIsNothingBeyondTheEdge()
    {
        PaneLayout layout = new();

        int left = layout.First;
        int right = layout.Split(left, Divide.Beside);

        left = layout.Panes[0];

        Assert.Equal(right, layout.Beyond(left, Toward.Right));
        Assert.Equal(left, layout.Beyond(right, Toward.Left));

        // Nothing above, below, or past either end. Wrapping here would move the focus to the far
        // side of the window, which is never what an arrow key means.
        Assert.Equal(-1, layout.Beyond(left, Toward.Left));
        Assert.Equal(-1, layout.Beyond(right, Toward.Right));
        Assert.Equal(-1, layout.Beyond(left, Toward.Up));
        Assert.Equal(-1, layout.Beyond(left, Toward.Down));
    }

    /// <summary>
    /// Closing a pane gives its space to the sibling, and a sibling that is a split takes it whole.
    /// </summary>
    [Fact]
    public void ClosingAPaneGivesItsSpaceToTheSibling()
    {
        PaneLayout layout = new();

        int left = layout.First;
        int right = layout.Split(left, Divide.Beside);

        left = layout.Panes[0];

        int lower = layout.Split(right, Divide.Below);

        int upper = layout.Portions.Keys.Single(pane => pane != left && pane != lower);

        Assert.Equal(3, layout.Count);

        // The left one goes, so the split holding the other two takes the whole width — and both of
        // them keep their share of it.
        layout.Close(left);

        Assert.Equal(2, layout.Count);
        Assert.Equal(new Portion(0, 0, 1, 0.5), layout.Portions[upper]);
        Assert.Equal(new Portion(0, 0.5, 1, 0.5), layout.Portions[lower]);

        // And one more leaves a tab that is one pane again, filling it.
        layout.Close(lower);

        Assert.Equal(1, layout.Count);
        Assert.Equal(new Portion(0, 0, 1, 1), layout.Portions[layout.First]);
    }

    /// <summary>
    /// A divider is dragged by setting a share, and neither side is ever allowed to disappear.
    ///
    /// <para>A pane dragged to nothing is a pane the user cannot get back to, and the only way out
    /// would be a chord they do not know they need.</para>
    /// </summary>
    [Fact]
    public void ADividerMovesAndNeitherSideCanBeDraggedAway()
    {
        PaneLayout layout = new();

        int left = layout.First;
        int right = layout.Split(left, Divide.Beside);

        left = layout.Panes[0];

        layout.Share(right, 0.75);

        Assert.Equal(0.75, layout.Portions[left].Width, 9);
        Assert.Equal(0.25, layout.Portions[right].Width, 9);

        // All the way over, twice, and both sides survive.
        layout.Share(right, 1.5);

        Assert.Equal(0.95, layout.Portions[left].Width, 9);

        layout.Share(right, -3);

        Assert.Equal(0.05, layout.Portions[left].Width, 9);

        // And equalising puts every split back where it started.
        layout.Equalise();

        Assert.Equal(0.5, layout.Portions[left].Width, 9);
    }
}
