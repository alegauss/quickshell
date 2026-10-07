using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS183: a clipboard somebody else is holding for a moment is waited for, and one held for longer
/// is said rather than silently answered with nothing.
/// </summary>
public sealed class BusyClipboardTests
{
    /// <summary>
    /// The falsification: a paste pressed while another process holds the clipboard for less than a
    /// tenth of a second still sends what the clipboard holds.
    /// </summary>
    [Fact]
    public void APasteWhileTheClipboardIsBrieflyHeldStillSends()
    {
        (string sent, string? notice) = Sta.Run(() =>
        {
            string went = string.Empty;

            MainWindow window = new()
            {
                Clipboard = new BusyClipboard(busyFor: 3) { Text = "make" },
                Pasting = text =>
                {
                    went = text;

                    return ValueTask.CompletedTask;
                },
                Bracketed = () => true,
            };

            window.PasteFromClipboard();

            return (went, window.Notice);
        });

        // Bracketed, so it arrives between the markers that say it is a paste.
        Assert.Contains("make", sent, StringComparison.Ordinal);
        Assert.Null(notice);
    }

    /// <summary>A clipboard held past the bound sends nothing, and the title says why.</summary>
    [Fact]
    public void APasteTheClipboardNeverAnswersIsSaid()
    {
        (string sent, string? notice, string title) = Sta.Run(() =>
        {
            string went = string.Empty;

            MainWindow window = new()
            {
                Clipboard = new BusyClipboard(busyFor: int.MaxValue) { Text = "make" },
                Pasting = text =>
                {
                    went = text;

                    return ValueTask.CompletedTask;
                },
                Bracketed = () => true,
            };

            window.PasteFromClipboard();

            return (went, window.Notice, window.Title);
        });

        Assert.Equal(string.Empty, sent);
        Assert.StartsWith("Paste did not happen", notice, StringComparison.Ordinal);
        Assert.StartsWith("Paste did not happen", title, StringComparison.Ordinal);
    }

    /// <summary>
    /// A copy is the same the other way: briefly held, it lands; held for good, it says so instead of
    /// leaving the previous text to be pasted somewhere it was not meant to go.
    /// </summary>
    [Fact]
    public void ACopyWaitsForABriefHoldAndSaysWhenItCannot()
    {
        (string brief, string held, string? notice) = Sta.Run(() =>
        {
            BusyClipboard moment = new(busyFor: 3);
            BusyClipboard forever = new(busyFor: int.MaxValue);

            MainWindow window = new() { Selected = () => "the selection", Clipboard = moment };

            string first = window.CopySelection();

            window.Clipboard = forever;

            string second = window.CopySelection();

            return (moment.Text == "the selection" ? first : "not written", second, window.Notice);
        });

        Assert.Equal("the selection", brief);
        Assert.Equal(string.Empty, held);
        Assert.StartsWith("Copy did not happen", notice, StringComparison.Ordinal);
    }

    /// <summary>A clipboard that is held open by somebody else for its first few asks.</summary>
    private sealed class BusyClipboard(int busyFor) : IClipboard
    {
        private int _asked;

        public string Text { get; set; } = string.Empty;

        public string Read() => TryRead(out string text) ? text : string.Empty;

        public bool TryRead(out string text)
        {
            text = string.Empty;

            if (_asked++ < busyFor)
            {
                return false;
            }

            text = Text;

            return true;
        }

        public bool Write(string text)
        {
            if (_asked++ < busyFor)
            {
                return false;
            }

            Text = text;

            return true;
        }
    }
}
