using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS344: the real Windows Hello call, through UserConsentVerifier's COM interop interface, run where
/// it cannot put a prompt in front of anybody: the guest, whose local account has no Windows Hello.
/// There the verifier answers that it is not set up, which proves the activation factory, the
/// interface, the parameterised IID of the operation and the polled status, every part QS306's tests
/// answered through MainWindow.Verifies instead.
/// </summary>
public sealed class WindowsHelloTests
{
    /// <summary>What provision-guest-sshd.ps1 leaves in the guest, and nothing leaves on a desk somebody is at.</summary>
    private const string GuestMarker = @"C:\ProgramData\quickshell-cases\probe_ed25519";

    /// <summary>On a desk with no Windows Hello the real verifier answers Unavailable, through the interop, without a prompt.</summary>
    [Fact]
    public void TheRealVerifierAnswersUnavailableOnADeskWithNoHello()
    {
        // Never on a desk somebody is at: there it would put a real prompt in front of them.
        Assert.SkipUnless(File.Exists(GuestMarker),
            "this is not the guest, where Windows Hello is not set up: run tools/provision-guest-sshd.ps1 once, which marks the guest");

        HelloVerdict verdict = Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());

            Window owner = new()
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                Width = 200,
                Height = 100,
                ShowActivated = false,
            };

            owner.Show();

            Task<HelloVerdict> asked = WindowsHello.VerifyAsync(new WindowInteropHelper(owner).Handle,
                                                                "A test asking Windows Hello, which this desk does not have.");

            Stopwatch waited = Stopwatch.StartNew();

            while (!asked.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                DispatcherFrame frame = new();

                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
                Thread.Sleep(20);
            }

            owner.Close();

            Assert.True(asked.IsCompleted, "the verifier did not answer within thirty seconds, which on this desk means a prompt is up");

            return asked.Result;
        });

        Assert.Equal(HelloVerdict.Unavailable, verdict);
    }
}
