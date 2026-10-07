using System.Windows;
using System.Windows.Controls;
using Quickshell.App;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS131: a question's buttons say what they do, in this client's own words.
///
/// <para>The falsification is read off the buttons themselves: none of them says Yes, No, OK or
/// Cancel, each names its act, and the way out — what Escape and closing the window do — is never
/// the destructive answer.</para>
/// </summary>
public sealed class ChoiceTests
{
    private static readonly string[] Windows = ["Yes", "No", "OK", "Cancel", "Sim", "Não"];

    /// <summary>The crash dialog's three buttons, as the crash path builds them.</summary>
    [Fact]
    public void TheCrashDialogsButtonsSayWhatTheyDo()
    {
        (IReadOnlyList<string> labels, string? chosen) = Asked(
            () => Choice.Ask(null, "quickshell has stopped", "It stopped.",
                             new ChoiceButton("Open the report", "file"),
                             new ChoiceButton("Open its folder", "folder"),
                             new ChoiceButton("Close", "close")),
            press: "Open its folder");

        Assert.Equal(["Open the report", "Open its folder", "Close"], labels);
        Assert.DoesNotContain(labels, label => Windows.Contains(label));
        Assert.Equal("folder", chosen);
    }

    /// <summary>
    /// The first button is what Enter does and the way out is what Escape does — the last one, or the
    /// one marked as the way out, so a question whose safe answer comes first keeps Escape safe.
    /// </summary>
    [Fact]
    public void EnterIsTheFirstAndEscapeIsTheWayOut()
    {
        (bool first, bool last) = OnSta(() =>
        {
            Choice usual = Build(new ChoiceButton("Open the report", "file"), new ChoiceButton("Close", "close"));
            Button[] buttons = Buttons(usual);

            return (buttons[0].IsDefault, buttons[1].IsCancel);
        });

        Assert.True(first);
        Assert.True(last);

        // The uninstall question: keeping the settings is both, so no key removes them.
        (bool keepDefault, bool keepCancel, bool removeCancel) = OnSta(() =>
        {
            Choice uninstall = Build(new ChoiceButton("Keep them", "keep") { IsWayOut = true },
                                     new ChoiceButton("Remove them too", "remove"));
            Button[] buttons = Buttons(uninstall);

            return (buttons[0].IsDefault, buttons[0].IsCancel, buttons[1].IsCancel);
        });

        Assert.True(keepDefault);
        Assert.True(keepCancel);
        Assert.False(removeCancel);
    }

    /// <summary>
    /// The host-key question, as the window asks it: three acts, and the one chosen is the verdict.
    /// Closing it without choosing is "do not connect".
    /// </summary>
    [Fact]
    public void TheHostKeyQuestionNamesItsThreeAnswers()
    {
        (IReadOnlyList<string> labels, SshHostKeyVerdict verdict) = OnSta(() =>
        {
            IReadOnlyList<string> seen = [];
            Choice.Showing = choice =>
            {
                seen = choice.Labels;
                choice.Press("Trust it this time");

                return choice.Chosen;
            };

            try
            {
                MainWindow window = new();
                HostKeyQuestion question = new(SshEndpoint.For("host.example", "me"),
                                               new SshHostKey("ssh-ed25519", "a key"u8.ToArray()), null,
                                               KnownHostVerdict.Unknown);

                Task<SshHostKeyVerdict> asked = window.AskHostKey(question, CancellationToken.None).AsTask();

                // The question is asked on the window's own thread; run it there.
                while (!asked.IsCompleted)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        () => { }, System.Windows.Threading.DispatcherPriority.Background);
                }

                return (seen, asked.GetAwaiter().GetResult());
            }
            finally
            {
                Choice.Showing = null;
            }
        });

        Assert.Equal(["Trust and remember it", "Trust it this time", "Don't connect"], labels);
        Assert.Equal(SshHostKeyVerdict.Accept, verdict);
    }

    // ---- plumbing ----

    private static Choice Build(params ChoiceButton[] buttons)
    {
        Choice? built = null;

        Choice.Showing = choice =>
        {
            built = choice;

            return null;
        };

        try
        {
            Choice.Ask(null, "a question", "Which?", buttons);
        }
        finally
        {
            Choice.Showing = null;
        }

        return built!;
    }

    private static Button[] Buttons(Choice choice) =>
        [.. LogicalTreeHelper.GetChildren((StackPanel)((StackPanel)choice.Content).Children[1]).OfType<Button>()];

    private static (IReadOnlyList<string> Labels, string? Chosen) Asked(Func<string?> asking, string press) =>
        OnSta(() =>
        {
            IReadOnlyList<string> labels = [];

            Choice.Showing = choice =>
            {
                labels = choice.Labels;
                choice.Press(press);

                return choice.Chosen;
            };

            try
            {
                string? answer = asking();

                return (labels, answer);
            }
            finally
            {
                Choice.Showing = null;
            }
        });

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread sta = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception caught)
            {
                failed = caught;
            }
        });

        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();

        if (failed is not null)
        {
            throw new InvalidOperationException("the STA work failed", failed);
        }

        return result;
    }
}
