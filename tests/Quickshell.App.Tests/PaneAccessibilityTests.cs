using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using Quickshell.App;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// What happens when something asks the pane about accessibility.
///
/// <para><b>The peer had tests and every one of them built it in-process and read it.</b> None sent
/// the message Windows sends, and that is where the client died: a hosted child window answers
/// <c>WM_GETOBJECT</c> down a path that expects a peer of a type internal to WPF, produces no
/// provider for the one this pane publishes, and hands the nothing to UIA — which throws inside the
/// message pump. A screen reader, a UI case, or Windows itself asking once was enough, so the client
/// was, for anyone using assistive technology, an application that exits on launch.</para>
///
/// <para>QS148, and it was found by a UI case rather than by any of this: nothing in this process
/// had ever sent the message that breaks it.</para>
/// </summary>
public sealed class PaneAccessibilityTests
{
    /// <summary>WM_GETOBJECT: what Windows sends a window when something wants its accessibility.</summary>
    private const uint WmGetObject = 0x003D;

    /// <summary>OBJID_CLIENT, the client area — which for this window is the whole of it.</summary>
    private static readonly nint ObjectIdClient = unchecked((nint)0xFFFFFFFC);

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nint wparam, nint lparam);

    /// <summary>
    /// The message is answered rather than fatal, and the terminal is still published.
    ///
    /// <para>Both halves, because the fix could so easily be a silencing. The child window declines —
    /// it is a texture with no controls and nothing to say — and what a reader is meant to find is
    /// found where a reader looks: the peer of an element in the window's tree.</para>
    /// </summary>
    [Fact]
    public void AskingThePaneAboutAccessibilityDoesNotKillTheClient()
    {
        Emulator emulator = new(80, 25);

        emulator.Feed(Encoding.UTF8.GetBytes("a reader would find this"));

        (nint answered, string? name, string? kind) = OnPane(emulator.Buffer, pane =>
        {
            // The message that used to end the process, sent to the window it is sent to.
            nint result = SendMessageW(pane.PaneHandle, WmGetObject, nint.Zero, ObjectIdClient);

            AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(pane);

            return (result, peer?.GetName(), peer?.GetClassName());
        });

        Assert.Equal(nint.Zero, answered);
        Assert.Equal("Terminal output", name);
        Assert.Equal("Terminal", kind);
    }

    /// <summary>
    /// And a pane with no session behind it answers the same way rather than differently.
    ///
    /// <para>A client shows a window before it has anything to put in it, so this is not a corner:
    /// it is the state every window is in for its first moments.</para>
    /// </summary>
    [Fact]
    public void APaneWithNoBufferAnswersTheSameWay()
    {
        nint answered = OnPane(null, pane =>
            SendMessageW(pane.PaneHandle, WmGetObject, nint.Zero, ObjectIdClient));

        Assert.Equal(nint.Zero, answered);
    }

    /// <summary>
    /// Builds the client's window with a pane in it, on an STA thread, and hands the pane over.
    /// </summary>
    private static T OnPane<T>(TerminalBuffer? reading, Func<TerminalPane, T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            Window? window = null;

            try
            {
                MainWindow client = new()
                {
                    Width = 480,
                    Height = 320,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };

                window = client;

                // Before it is shown, because WPF builds an element's automation peer once and
                // keeps it: a pane shown without a buffer publishes an empty terminal for good.
                TerminalPane pane = new() { Reading = reading };

                client.Show(pane);
                client.Show();
                client.UpdateLayout();

                Assert.True(pane.PaneHandle != nint.Zero, "the pane never built a handle");

                result = work(pane);
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                window?.Close();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the pane could not be asked", failed);
        }

        return result;
    }
}
