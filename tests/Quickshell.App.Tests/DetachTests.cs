using System.Text;
using System.Windows.Input;
using Quickshell.App;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS160: a tab taken out into a window of its own keeps its connection. The pane is rebuilt for
/// the new window, and the session behind it is the same object, never closed and never opened again.
/// </summary>
public sealed class DetachTests
{
    private static readonly TerminalShare Shared = new();

    /// <summary>
    /// The design's own falsifier: <em>falsified when detaching a tab reconnects its session</em>.
    /// </summary>
    [Fact]
    public void ADetachedTabKeepsItsConnection()
    {
        (bool went, int left, bool sameTab, bool newPane, int opened, int disposed, string typed, bool adopted) =
            Sta.Run(() =>
            {
                MainWindow window = new();
                MainWindow? other = null;

                window.Detaches = tab =>
                {
                    other = new MainWindow();
                    other.Adopt(tab);
                };

                window.Add(TerminalTab.Open(Settings.Default, Shared, "alpha"));
                window.Add(TerminalTab.Open(Settings.Default, Shared, "beta"));

                TerminalTab beta = window.Held[1];
                TerminalLeaf leaf = beta.Focused;
                Counted session = new();
                int opens = 0;

                leaf.ConnectAsync((_, _, _, _, _) =>
                {
                    opens++;

                    return Task.FromResult<IShellSession>(session);
                }).GetAwaiter().GetResult();

                TerminalPane before = leaf.Pane;

                bool moved = window.Detach(1);

                leaf.Typist.Type("after", ModifierKeys.None);

                return (moved, window.Held.Count, ReferenceEquals(other?.Held.Single(), beta),
                        !ReferenceEquals(before, leaf.Pane), opens, session.Disposed, session.Typed.ToString(),
                        other?.Sessions.Closing().Contains("beta") == true);
            });

        Assert.True(went, "the tab did not leave");
        Assert.Equal(1, left);
        Assert.True(sameTab, "the new window holds a different tab");
        Assert.True(newPane, "the pane was not rebuilt for the new window");
        Assert.Equal(1, opened);
        Assert.Equal(0, disposed);
        Assert.Equal("after", typed);
        Assert.True(adopted, "the new window's close question does not know the session it holds");
    }

    [Fact]
    public void TheOnlyTabDoesNotLeaveItsWindow()
    {
        (bool went, bool offered) = Sta.Run(() =>
        {
            MainWindow window = new() { Detaches = _ => { } };

            window.Add(TerminalTab.Open(Settings.Default, Shared, "alone"));

            return (window.Detach(0), window.Actions.Any(entry => entry.Name == "Move tab to a new window"));
        });

        Assert.False(went);
        Assert.False(offered);
    }

    [Fact]
    public void WithTwoTabsThePaletteOffersIt()
    {
        bool offered = Sta.Run(() =>
        {
            MainWindow window = new() { Detaches = _ => { } };

            window.Add(TerminalTab.Open(Settings.Default, Shared, "alpha"));
            window.Add(TerminalTab.Open(Settings.Default, Shared, "beta"));

            return window.Actions.Any(entry => entry.Name == "Move tab to a new window");
        });

        Assert.True(offered);
    }

    private sealed class Counted : IShellSession
    {
        public StringBuilder Typed { get; } = new();

        public int Disposed { get; private set; }

        public Task<PtyExit> Ended { get; } = new TaskCompletionSource<PtyExit>().Task;

        public ValueTask TypeAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Typed.Append(Encoding.UTF8.GetString(bytes.Span));

            return ValueTask.CompletedTask;
        }

        public void Resize(int columns, int rows)
        {
        }

        public void KeepScrollback(int lines)
        {
        }

        public ValueTask DisposeAsync()
        {
            Disposed++;

            return ValueTask.CompletedTask;
        }
    }
}
