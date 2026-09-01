using System.Text;
using System.Windows.Input;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

// Both namespaces name a Key. Here the window's is meant: what these type is what a person pressed.
using Key = System.Windows.Input.Key;

namespace Quickshell.App.Tests;

/// <summary>
/// The route the window actually has: a real shell, the pipeline, the model, and a keystroke going
/// back the other way.
///
/// <para><b>Against <c>cmd.exe</c> and not a stub</b>, because the claim QS116 makes is that the
/// client shows what a session printed, and every stub in this repository prints what a test told it
/// to. What is unasserted anywhere else is that the three stages, the damage signal the pane sleeps
/// on and the typist all name the same session.</para>
///
/// <para>No window and no device here: those are <c>TerminalViewTests</c>, which put real ones on
/// screen. What these need is the half that has no pixels in it.</para>
/// </summary>
public sealed class LocalSessionTests
{
    /// <summary>How long any one of these waits before calling it a hang.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A shell's own output reaches the model, and the signal a pane sleeps on is the one that is set.
    ///
    /// <para>The signal is half the claim. A session that parsed correctly into the model and set a
    /// signal of its own would pass every assertion about text and still be a window that draws one
    /// frame and then stops, which is the failure the handed-in signal exists to prevent and is
    /// invisible from anywhere the text is checked.</para>
    /// </summary>
    [Fact]
    public async Task AShellsOutputReachesTheModelAndWakesThePanesSignal()
    {
        Emulator emulator = new(80, 25);
        DamageSignal damage = new();

        await using LocalSession session = await Open(emulator, damage);

        Assert.Same(damage, session.Pipeline.Damage);

        await Printed(emulator);

        Assert.True(damage.Sets > 0, "the pane's own signal was never set by the session");
    }

    /// <summary>
    /// A key pressed on the window runs in the shell, and its output comes back to the model.
    ///
    /// <para><b>Typed as a person types it</b>: characters through <see cref="Typist.Type"/> and
    /// Enter through <see cref="Typist.Press"/>, because those are two different paths out of the
    /// window and a test using one of them would leave the other unexercised.</para>
    ///
    /// <para>It waits for the prompt first. Bytes written before the console host has a reader on the
    /// other side can be dropped, which is the same reason a user waits for a prompt before typing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhatIsTypedOnTheWindowRunsInTheShell()
    {
        Emulator emulator = new(80, 25);
        DamageSignal damage = new();

        await using LocalSession session = await Open(emulator, damage);

        await Printed(emulator);

        Typist typist = new(emulator) { Sending = bytes => session.Pipeline.TypeAsync(bytes) };

        Assert.True(typist.Type("echo typed-on-the-window", ModifierKeys.None));
        Assert.True(typist.Press(Key.Return, ModifierKeys.None));

        await Shows(emulator, "typed-on-the-window");
    }

    /// <summary>
    /// The window's grid reaches the program, which is the third of QS32's three parties.
    ///
    /// <para>Debounced by the pipeline and so waited for rather than asserted at once. What is
    /// checked is that the far end was actually told: a resize reaching the model alone would leave
    /// the program drawing for a terminal nobody has.</para>
    /// </summary>
    [Fact]
    public async Task AResizeReachesTheProgramAndNotJustTheModel()
    {
        Emulator emulator = new(80, 25);
        DamageSignal damage = new();

        await using LocalSession session = await Open(emulator, damage);

        await Printed(emulator);

        session.Pipeline.Resize(132, 40);

        await Until(() => session.Pipeline.Resizes > 0, "the far end was never told a new size");

        Assert.Equal(132, emulator.Buffer.Columns);
        Assert.Equal(40, emulator.Buffer.Rows);
    }

    /// <summary>
    /// The shell this client runs with nothing else asked for is the one Windows names.
    ///
    /// <para>Asserted because it is a decision rather than an accident: there is deliberately no
    /// setting for it, and <c>COMSPEC</c> is Windows' own answer to the question a setting would ask
    /// a second time.</para>
    /// </summary>
    [Fact]
    public void TheDefaultShellIsWhateverWindowsSaysItIs()
    {
        Assert.Equal(Environment.GetEnvironmentVariable("COMSPEC"), LocalSession.Shell);
        Assert.EndsWith("cmd.exe", LocalSession.Shell, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A session on this machine's shell, at the grid every case here uses.</summary>
    private static Task<LocalSession> Open(Emulator emulator, DamageSignal damage) =>
        LocalSession.OpenAsync(emulator, damage, 80, 25,
                               cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Waits until the shell has put something on the screen.</summary>
    private static Task Printed(Emulator emulator) =>
        Until(() => Screen(emulator).Trim().Length > 0, "the shell printed nothing at all");

    /// <summary>Waits until the screen holds this text.</summary>
    private static Task Shows(Emulator emulator, string text) =>
        Until(() => Screen(emulator).Contains(text, StringComparison.Ordinal),
              $"'{text}' never appeared on the screen");

    /// <summary>The whole screen as one string, which is what a person reading it would see.</summary>
    private static string Screen(Emulator emulator)
    {
        StringBuilder text = new();

        for (int row = 0; row < emulator.Buffer.Rows; row++)
        {
            foreach (Cell cell in emulator.Buffer.Screen(row))
            {
                if (cell.Width != 0)
                {
                    text.Append(emulator.Buffer.TextOf(cell));
                }
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static async Task Until(Func<bool> settled, string otherwise)
    {
        DateTime giveUp = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < giveUp)
        {
            if (settled())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail(otherwise);
    }
}
