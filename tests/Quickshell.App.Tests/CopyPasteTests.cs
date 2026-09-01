using System.Text;
using System.Windows.Input;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

// Both namespaces name a Key. Here the window's is meant: these are chords a person presses.
using Key = System.Windows.Input.Key;

namespace Quickshell.App.Tests;

/// <summary>
/// Copying and pasting through the window, which is the half QS30's model could not reach.
///
/// <para><b>Driven through the bindings the window actually carries</b>, because a method nothing is
/// bound to is a feature no user can reach — the same standard the diagnostics and import bindings
/// are held to.</para>
///
/// <para><b>The paste cases are the security half of this line.</b> Pasted text runs the moment it
/// contains a newline, so what is asserted is not that a paste works: it is that a paste with a
/// newline in it does not leave this client until somebody has seen it.</para>
/// </summary>
public sealed class CopyPasteTests
{
    /// <summary>
    /// Ctrl+Shift+C copies the selection, and Ctrl+C is deliberately not bound.
    ///
    /// <para>The second half is the one worth a test. Ctrl+C is how a person stops a runaway
    /// program, and a client that took it for copying would have removed the thing they reach for
    /// when something has gone wrong — quietly, and only noticed under load.</para>
    /// </summary>
    [Fact]
    public void CtrlShiftCCopiesAndCtrlCIsLeftToTheHost()
    {
        string copied = OnStaThread(() =>
        {
            MainWindow window = new() { Selected = () => "what the drag covered" };

            Assert.DoesNotContain(window.InputBindings.OfType<KeyBinding>(),
                                  bound => bound.Key == Key.C
                                           && bound.Modifiers == ModifierKeys.Control);

            Binding(window, Key.C).Command.Execute(null);

            return window.CopySelection();
        });

        Assert.Equal("what the drag covered", copied);
    }

    /// <summary>
    /// A copy with nothing selected leaves the clipboard alone.
    ///
    /// <para>There is no undo for a clipboard, and somebody who pressed the chord with nothing
    /// highlighted has not asked for what they were carrying to be thrown away.</para>
    /// </summary>
    [Fact]
    public void CopyingNothingPutsNothingOnTheClipboard()
    {
        string copied = OnStaThread(() =>
        {
            MainWindow window = new() { Selected = () => string.Empty };

            return window.CopySelection();
        });

        Assert.Equal(string.Empty, copied);
    }

    /// <summary>
    /// A paste carrying a newline is shown before it is sent, and refusing it sends nothing.
    ///
    /// <para>The refusal is asserted first, because that is the half a user is trusting.</para>
    /// </summary>
    [Fact]
    public void APasteWithANewlineIsShownAndARefusalSendsNothing()
    {
        (string shown, string sentAfterNo, string sentAfterYes) = OnStaThread(() =>
        {
            string seen = string.Empty;
            string went = string.Empty;

            MainWindow window = new()
            {
                Pasting = text =>
                {
                    went = text;

                    return ValueTask.CompletedTask;
                },
                Bracketed = () => false,
                AskingToPaste = text =>
                {
                    seen = text;

                    return false;
                },
            };

            Clipboard("echo one\r\necho two\n");

            window.PasteFromClipboard();

            string afterRefusing = went;

            window.AskingToPaste = _ => true;

            Binding(window, Key.V).Command.Execute(null);

            return (seen, afterRefusing, went);
        });

        // What the user was shown is what would be sent: carriage returns, one per line ending, and
        // not the pair a Windows clipboard supplies.
        Assert.Equal("echo one\recho two\r", shown);

        Assert.Equal(string.Empty, sentAfterNo);
        Assert.Equal("echo one\recho two\r", sentAfterYes);
    }

    /// <summary>
    /// With bracketed paste on, the program is told and the user is not asked.
    ///
    /// <para>That is the point of DECSET 2004: the program itself declines to run pasted text, so a
    /// dialog as well would be a question in front of every ordinary paste — and a client that asks
    /// too often is one whose questions stop being read.</para>
    /// </summary>
    [Fact]
    public void BracketedPasteTellsTheProgramInsteadOfAskingTheUser()
    {
        (int asked, string sent) = OnStaThread(() =>
        {
            int times = 0;
            string went = string.Empty;

            MainWindow window = new()
            {
                Pasting = text =>
                {
                    went = text;

                    return ValueTask.CompletedTask;
                },
                Bracketed = () => true,
                AskingToPaste = _ =>
                {
                    times++;

                    return true;
                },
            };

            Clipboard("echo one\r\n");

            window.PasteFromClipboard();

            return (times, went);
        });

        Assert.Equal(0, asked);
        Assert.Equal(Paste.Start + "echo one\r" + Paste.Finish, sent);
    }

    /// <summary>
    /// An escape sequence in the clipboard never reaches the host, and needs no asking about.
    ///
    /// <para>Nothing legitimate pastes an escape sequence. One that got through could set a mode,
    /// change a title or answer a query on the user's behalf — and with no newline in it, nothing
    /// would have asked.</para>
    /// </summary>
    [Fact]
    public void ControlCharactersAreStrippedBeforeAnythingIsSent()
    {
        string sent = OnStaThread(() =>
        {
            string went = string.Empty;

            MainWindow window = new()
            {
                Pasting = text =>
                {
                    went = text;

                    return ValueTask.CompletedTask;
                },
                Bracketed = () => false,
                AskingToPaste = _ => true,
            };

            // An escape, a bell and a tab. The tab is text and survives; the other two are not.
            Clipboard("safe" + (char)0x1B + "[2J" + (char)0x07 + "\tend");

            window.PasteFromClipboard();

            return went;
        });

        Assert.Equal("safe[2J\tend", sent);
        Assert.DoesNotContain((char)0x1B, sent);
        Assert.DoesNotContain((char)0x07, sent);
    }

    /// <summary>
    /// The count in the dialog is the number of lines the host would run.
    ///
    /// <para>A trailing break does not add a line. The count is the whole reason a user glances at
    /// that dialog before reading it, and two commands announced as three says there is something in
    /// the clipboard they cannot see — which is exactly the alarm this dialog exists to raise
    /// honestly.</para>
    /// </summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData("echo one", 1)]
    [InlineData("echo one\r", 1)]
    [InlineData("echo one\recho two", 2)]
    [InlineData("echo one\recho two\r", 2)]
    [InlineData("echo one\r\r", 2)]
    public void TheLineCountIsWhatTheHostWouldRun(string text, int lines) =>
        Assert.Equal(lines, MainWindow.Lines(text));

    /// <summary>A paste with nowhere to go sends nothing rather than throwing on a UI thread.</summary>
    [Fact]
    public void APasteWithNoSessionSendsNothing()
    {
        string sent = OnStaThread(() =>
        {
            MainWindow window = new();

            Clipboard("anything");

            return window.PasteFromClipboard();
        });

        Assert.Equal(string.Empty, sent);
    }

    /// <summary>The binding this window carries for a key, with both modifiers asserted.</summary>
    private static KeyBinding Binding(MainWindow window, Key key)
    {
        KeyBinding bound = window.InputBindings.OfType<KeyBinding>()
                                 .Single(binding => binding.Key == key
                                                    && binding.Modifiers.HasFlag(ModifierKeys.Shift));

        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, bound.Modifiers);

        return bound;
    }

    /// <summary>
    /// Puts text on the clipboard and reads it back before going on.
    ///
    /// <para><b>Read back, and that is not belt and braces.</b> The clipboard is one object shared
    /// by every process on the desktop: a set can be accepted and then lost to whatever else was
    /// reaching for it, and a test that assumed otherwise fails later, somewhere else, saying that
    /// a paste sent nothing.</para>
    /// </summary>
    private static void Clipboard(string text)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);

                if (System.Windows.Clipboard.ContainsText()
                    && System.Windows.Clipboard.GetText() == text)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Somebody else has it open. Waiting is the whole remedy.
            }

            Thread.Sleep(50);
        }

        Assert.Fail("the clipboard would not hold what this test put on it");
    }

    /// <summary>Runs something on an STA thread, which the clipboard and a window both need.</summary>
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
