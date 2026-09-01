using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The two places WPF asks for a culture, which under invariant globalization killed the client.
///
/// <para><b>Both of these are reproductions before they are tests.</b> Each one exited the running
/// client and wrote a crash report: the first when the paste preview named a typeface, the second
/// when the find bar's text box drew its caret. Neither looks like a globalization question from
/// where it is written, which is exactly why a rule about not naming fonts would not have held —
/// the second has no font in it at all.</para>
///
/// <para><b>They assert no exception, and that is the whole claim.</b> There is nothing to measure
/// here: WPF either resolves the culture or throws, and a client that throws inside its own message
/// pump is a client that has gone.</para>
/// </summary>
public sealed class GlobalizationTests
{
    /// <summary>
    /// The setting is off, asserted where a reader will look for it rather than only in a file.
    ///
    /// <para>Invariant mode is detectable at run time: it is the mode in which no culture but the
    /// invariant one can be constructed. So this asks the runtime rather than the build.</para>
    /// </summary>
    [Fact]
    public void ThisProcessCanBuildACultureThatIsNotTheInvariantOne()
    {
        Assert.Equal("en", new CultureInfo("en").Name);

        // The user's own layout, by identifier, which is what WPF's text input stack asks for.
        Assert.NotNull(new CultureInfo(1046));
    }

    /// <summary>
    /// A text box drawing its caret, which is what the find bar does the moment it opens.
    ///
    /// <para>The crash was in <c>TextSelection.EnsureCaret</c> asking
    /// <c>InputLanguageSource.CurrentInputLanguage</c>, which builds a <c>CultureInfo</c> from the
    /// keyboard layout's identifier. On this machine that is 1046, and in invariant mode it
    /// throws.</para>
    /// </summary>
    [Fact]
    public void ATextBoxCanShowACaret()
    {
        OnStaThread(() =>
        {
            TextBox box = new() { MinWidth = 120 };
            Window window = new() { Content = box, Width = 300, Height = 120 };

            window.Show();

            box.Focus();
            box.Text = "something to put a caret in";
            box.CaretIndex = box.Text.Length;

            // Laid out for real, because the caret is built during layout and a window that was
            // never measured is a window where none of this has happened yet.
            window.UpdateLayout();
            window.Close();

            return true;
        });
    }

    /// <summary>
    /// Text measured in a named typeface, which is what a monospaced paste preview asks for.
    ///
    /// <para>The crash was in <c>MajorLanguages</c>' static constructor, reached from
    /// <c>Typeface.CheckFastPathNominalGlyphs</c> while measuring — so it needs a real measure and
    /// not merely a constructed element.</para>
    /// </summary>
    [Fact]
    public void TextCanBeMeasuredInANamedTypeface()
    {
        OnStaThread(() =>
        {
            TextBlock text = new()
            {
                Text = "echo something a person would check before running it",
                FontFamily = new FontFamily("Consolas"),
            };

            Window window = new() { Content = text, Width = 300, Height = 120 };

            window.Show();
            window.UpdateLayout();

            text.Measure(new Size(280, 100));

            Assert.True(text.DesiredSize.Width > 0, "the text was never measured");

            window.Close();

            return true;
        });
    }

    /// <summary>Runs something on an STA thread, which is the only kind a WPF window lives on.</summary>
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
            finally
            {
                // Showing a window builds a dispatcher on this thread, and a dispatcher that is
                // never shut down keeps a foreground thread alive after the test has finished — one
                // per case, until the runner refuses to exit and reports a suite that passed as a
                // failure. Marking the thread background is not enough: the dispatcher's own timer
                // thread is not this one.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
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
