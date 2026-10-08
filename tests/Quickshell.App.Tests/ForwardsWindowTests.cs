using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS70's view, on a desk with no SSH session: it opens from the palette, and says plainly that
/// nothing is running rather than showing an empty list that could mean the view is broken.
/// </summary>
public sealed class ForwardsWindowTests
{
    [Fact]
    public void WithNothingRunningTheViewSaysSo()
    {
        string line = Sta.Run(() => new ForwardsWindow(() => []).Lines.Single());

        Assert.StartsWith("No forwards are running", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowForwardsIsInThePaletteAndOpensTheView()
    {
        (bool offered, string shown) = Sta.Run(() =>
        {
            MainWindow window = new();
            ForwardsWindow? opened = null;

            window.ShowsForwards = view => opened = view;

            bool listed = window.Actions.Any(one => one.Name == "Show forwards");

            window.Actions.Single(one => one.Name == "Show forwards").Run();

            return (listed, opened!.Lines.Single());
        });

        Assert.True(offered);
        Assert.StartsWith("No forwards are running", shown, StringComparison.Ordinal);
    }
}
