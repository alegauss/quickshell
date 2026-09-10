using System.Text;
using System.Windows;
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
///
/// <para><b>On a clipboard of their own, and one test on the real one.</b> QS180: every case here
/// used to write the desk's clipboard, which erased what the person at the machine had copied and
/// went red whenever another process opened the clipboard between a write and a read. What these
/// cases are about is what the window does with text, so they hand it a <see cref="HeldClipboard"/>.
/// The last case is the one about the clipboard itself, and it puts back what it found.</para>
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
        (string copied, string held) = OnStaThread(() =>
        {
            HeldClipboard clipboard = new();
            MainWindow window = new() { Selected = () => "what the drag covered", Clipboard = clipboard };

            Assert.DoesNotContain(window.InputBindings.OfType<KeyBinding>(),
                                  bound => bound.Key == Key.C
                                           && bound.Modifiers == ModifierKeys.Control);

            Binding(window, Key.C).Command.Execute(null);

            return (window.CopySelection(), clipboard.Text);
        });

        Assert.Equal("what the drag covered", copied);
        Assert.Equal("what the drag covered", held);
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
        (string copied, string held, int writes) = OnStaThread(() =>
        {
            HeldClipboard clipboard = new() { Text = "what they were carrying" };
            MainWindow window = new() { Selected = () => string.Empty, Clipboard = clipboard };

            return (window.CopySelection(), clipboard.Text, clipboard.Writes);
        });

        Assert.Equal(string.Empty, copied);
        Assert.Equal("what they were carrying", held);
        Assert.Equal(0, writes);
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
                Clipboard = new HeldClipboard { Text = "echo one\r\necho two\n" },
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
                Clipboard = new HeldClipboard { Text = "echo one\r\n" },
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
                // An escape, a bell and a tab. The tab is text and survives; the other two are not.
                Clipboard = new HeldClipboard { Text = "safe" + (char)0x1B + "[2J" + (char)0x07 + "\tend" },
                Pasting = text =>
                {
                    went = text;

                    return ValueTask.CompletedTask;
                },
                Bracketed = () => false,
                AskingToPaste = _ => true,
            };

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
            MainWindow window = new() { Clipboard = new HeldClipboard { Text = "anything" } };

            return window.PasteFromClipboard();
        });

        Assert.Equal(string.Empty, sent);
    }

    /// <summary>
    /// The window's clipboard is the system's unless something says otherwise, and this test leaves
    /// the system's holding what it held.
    ///
    /// <para><b>The one case on the real clipboard</b>, because it is the one about the clipboard:
    /// every other case proves what the window does with text, and this proves the text it copies is
    /// on the desk's clipboard and the text it pastes came from there.</para>
    ///
    /// <para><b>It puts back what it found, or it touches nothing.</b> Plain text is saved and
    /// restored, and an empty clipboard is emptied again. Anything else — a picture, a file list, text
    /// carrying its formatting — is something this test cannot put back exactly, so it measures
    /// nothing and says so rather than destroy it. So does a clipboard another process holds open for
    /// the whole of the wait, which on a working desk is a state and not a failure.</para>
    /// </summary>
    [Fact]
    public void TheWindowUsesTheSystemClipboardAndLeavesItAsItWasFound()
    {
        (string? skipped, string copied, string onTheDesk, string pasted, bool restored) = OnStaThread(() =>
        {
            if (!Found(out string? before, out string? why))
            {
                return (why, string.Empty, string.Empty, string.Empty, true);
            }

            const string Probe = "quickshell clipboard probe";
            string went = string.Empty;

            MainWindow window = new()
            {
                Selected = () => Probe,
                Pasting = text =>
                {
                    went = text;

                    return ValueTask.CompletedTask;
                },
                Bracketed = () => false,
                AskingToPaste = _ => true,
            };

            string copy = string.Empty;
            string desk = string.Empty;
            string paste = string.Empty;
            bool back = false;

            try
            {
                Assert.Same(SystemClipboard.Instance, window.Clipboard);

                copy = Patiently(window.CopySelection);
                desk = Patiently(() => Now() ?? string.Empty);
                paste = Patiently(window.PasteFromClipboard);
            }
            finally
            {
                // Whatever happened above, and before anything is asserted: this is the person at
                // the machine's clipboard, and the test is a guest in it.
                back = Put(before);
            }

            return ((string?)null, copy, desk, paste, back);
        });

        Assert.SkipWhen(skipped is not null, skipped ?? string.Empty);

        Assert.Equal("quickshell clipboard probe", copied);
        Assert.Equal("quickshell clipboard probe", onTheDesk);
        Assert.Equal("quickshell clipboard probe", pasted);

        // What the person at the machine had, put back and read back. Read back by the write that
        // put it there and not by a later look: something on this desk rewrites the clipboard just
        // after it changes, and what it does afterwards is not this client's doing — QS180.
        Assert.True(restored, "the clipboard could not be put back to what it held before this case");
    }

    /// <summary>
    /// Reads what the desk's clipboard holds, if it is something this test can put back exactly.
    /// </summary>
    /// <param name="before">The plain text it holds, or null for nothing at all.</param>
    /// <param name="why">Why this case will measure nothing, where it will not.</param>
    private static bool Found(out string? before, out string? why)
    {
        before = null;
        why = null;

        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                IDataObject? held = System.Windows.Clipboard.GetDataObject();
                string[] formats = held?.GetFormats(autoConvert: false) ?? [];

                if (formats.Length == 0)
                {
                    return true;
                }

                if (!formats.All(PlainText.Contains))
                {
                    why = "the clipboard holds something other than plain text ("
                          + string.Join(", ", formats.Where(format => !PlainText.Contains(format)))
                          + "), which this case cannot put back exactly, so it measured nothing";

                    return false;
                }

                // Empty text is no text: an empty clipboard is what putting it back produces.
                before = System.Windows.Clipboard.GetText() is { Length: > 0 } text ? text : null;

                return true;
            }
            catch (Exception)
            {
                // Somebody else has it open. Waiting is the whole remedy.
            }

            Thread.Sleep(50);
        }

        why = "another process held the clipboard open for two seconds, so this case measured nothing";

        return false;
    }

    /// <summary>The formats plain text arrives in, which are the only ones this case will restore.</summary>
    private static readonly HashSet<string> PlainText = new(StringComparer.Ordinal)
    {
        DataFormats.Text, DataFormats.UnicodeText, DataFormats.OemText, DataFormats.Locale,
        DataFormats.StringFormat,
    };

    /// <summary>Puts back what the real-clipboard case found: the text, or nothing at all.</summary>
    /// <returns>Whether it read back as what was put, within two seconds of asking.</returns>
    private static bool Put(string? before)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (before is null)
                {
                    System.Windows.Clipboard.Clear();
                }
                else
                {
                    System.Windows.Clipboard.SetText(before);
                }

                if (Now() == before)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // Held open. Try again rather than leave the probe where the user's text was.
            }

            Thread.Sleep(50);
        }

        return false;
    }

    /// <summary>What the desk's clipboard holds as text now, or null for nothing.</summary>
    private static string? Now()
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                return System.Windows.Clipboard.ContainsText()
                    ? System.Windows.Clipboard.GetText()
                    : null;
            }
            catch (Exception)
            {
                Thread.Sleep(50);
            }
        }

        return null;
    }

    /// <summary>
    /// Asks until something comes back, for a little while: a read or a write that lands while
    /// another process holds the clipboard comes back empty, and that is the desk and not the window.
    /// </summary>
    private static string Patiently(Func<string> asking)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            string answer = asking();

            if (answer.Length > 0)
            {
                return answer;
            }

            Thread.Sleep(50);
        }

        return string.Empty;
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
