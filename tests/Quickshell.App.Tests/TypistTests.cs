using System.Text;
using System.Windows.Input;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

// Both namespaces name a Key, which is the whole reason Typing exists. Here the window's is meant
// every time: what these assert is what a WPF key event turns into.
using Key = System.Windows.Input.Key;

namespace Quickshell.App.Tests;

/// <summary>
/// A key pressed on the window, and the bytes a host reads.
///
/// <para>QS116's last piece. The two ends of this route were each built and checked — <c>Keys</c>
/// against xterm's tables, <c>SessionPipeline.TypeAsync</c> against a channel — and nothing carried
/// a WPF key event to either of them.</para>
///
/// <para><b>The route is checked end to end, through a real pipeline over a real channel.</b> A test
/// that asserted the encoding would be asserting <c>Keys</c> again, and <c>Keys</c> has its own; what
/// is unasserted is everything between a window's event and a write.</para>
/// </summary>
public sealed class TypistTests
{
    /// <summary>
    /// An arrow pressed on the window arrives at the far end as the arrow's own sequence.
    /// </summary>
    [Fact]
    public async Task AKeyPressedOnTheWindowReachesTheHost()
    {
        PtyStub host = new();
        Emulator emulator = new(80, 25);

        await using SessionPipeline pipeline = SessionPipeline.Start(host, emulator);

        Typist typist = new(emulator) { Sending = bytes => pipeline.TypeAsync(bytes) };

        Assert.True(typist.Press(Key.Up, ModifierKeys.None), "the terminal declined an arrow key");

        byte[] sent = await Written(host);

        // CSI A, which is what an unmodified Up sends outside application cursor mode. Spelled with
        // an escape and never the byte itself, which SourceHygieneTests refuses for QS98's reasons.
        Assert.Equal("\u001b[A", Encoding.UTF8.GetString(sent));
    }

    /// <summary>
    /// A modifier reaches the host with the key, in xterm's parameter form.
    ///
    /// <para>Here because the modifier crosses two vocabularies on the way — WPF's
    /// <see cref="ModifierKeys"/> and the terminal's three bits — and a translation that dropped one
    /// would still send a perfectly valid unmodified arrow.</para>
    /// </summary>
    [Fact]
    public async Task AModifierCrossesWithTheKey()
    {
        PtyStub host = new();
        Emulator emulator = new(80, 25);

        await using SessionPipeline pipeline = SessionPipeline.Start(host, emulator);

        Typist typist = new(emulator) { Sending = bytes => pipeline.TypeAsync(bytes) };

        typist.Press(Key.Right, ModifierKeys.Control);

        byte[] sent = await Written(host);

        // 1 + 4 for control, which is the 5 in xterm's parameter.
        Assert.Equal("\u001b[1;5C", Encoding.UTF8.GetString(sent));
    }

    /// <summary>
    /// A typed character goes as itself, through the layout Windows already applied.
    /// </summary>
    [Fact]
    public async Task ATypedCharacterGoesAsItself()
    {
        PtyStub host = new();
        Emulator emulator = new(80, 25);

        await using SessionPipeline pipeline = SessionPipeline.Start(host, emulator);

        Typist typist = new(emulator) { Sending = bytes => pipeline.TypeAsync(bytes) };

        // A character no ASCII path would carry, because the encoding is the claim.
        Assert.True(typist.Type("ç", ModifierKeys.None));

        byte[] sent = await Written(host);

        Assert.Equal("ç", Encoding.UTF8.GetString(sent));
    }

    /// <summary>
    /// The window's own chords never reach the host, and are counted where they stop.
    ///
    /// <para>Both of them, by name. This is the list a user is owed when they ask what this client
    /// takes from the program they are running, and a test that checked one of the two would let the
    /// other be added silently.</para>
    /// </summary>
    [Theory]
    [InlineData(Key.F1)]
    [InlineData(Key.I)]
    public async Task TheWindowsOwnChordsAreNotTheHosts(Key key)
    {
        PtyStub host = new();
        Emulator emulator = new(80, 25);

        await using SessionPipeline pipeline = SessionPipeline.Start(host, emulator);

        Typist typist = new(emulator) { Sending = bytes => pipeline.TypeAsync(bytes) };

        Assert.False(typist.Press(key, ModifierKeys.Control | ModifierKeys.Shift),
                     "the terminal took a chord the window had reserved");

        Assert.Equal(1, typist.Declined);
        Assert.Equal(0, typist.Sent);

        // And the same key without both modifiers is the host's, which is what keeps the reserved
        // set two chords rather than two keys.
        Assert.True(typist.Press(key == Key.F1 ? Key.F1 : Key.Up, ModifierKeys.None));

        Assert.NotEmpty(await Written(host));
    }

    /// <summary>
    /// A key with no session behind it is taken and dropped rather than thrown over.
    ///
    /// <para>The shipped client's state today, and it must not be an exception on a UI thread: a
    /// person typing into a window that has not connected is not doing anything wrong.</para>
    /// </summary>
    [Fact]
    public void AKeyWithNowhereToGoIsStillTheTerminals()
    {
        Typist typist = new(new Emulator(80, 25));

        Assert.True(typist.Press(Key.Up, ModifierKeys.None));
        Assert.True(typist.Type("a", ModifierKeys.None));

        Assert.Equal(0, typist.Sent);
    }

    /// <summary>
    /// A modifier on its own is not a keystroke, and neither is a key this map does not know.
    /// </summary>
    [Fact]
    public void AKeyThatMeansNothingSendsNothing()
    {
        Typist typist = new(new Emulator(80, 25));

        Assert.False(typist.Press(Key.LeftShift, ModifierKeys.Shift));
        Assert.False(typist.Press(Key.LWin, ModifierKeys.Windows));
        Assert.False(typist.Type(string.Empty, ModifierKeys.None));
    }

    /// <summary>The first write the far end saw, waited for rather than assumed.</summary>
    private static async Task<byte[]> Written(PtyStub host)
    {
        for (int poll = 0; poll < 500; poll++)
        {
            lock (host.Written)
            {
                if (host.Written.Count > 0)
                {
                    return host.Written[0];
                }
            }

            await Task.Delay(10);
        }

        Assert.Fail("nothing reached the far end in five seconds");

        return [];
    }
}
