using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS162's falsification, as a test rather than as a thing anybody has to remember:
/// <em>falsified when a chord is reserved and the list does not name it</em>.
///
/// <para><b>Against the real window's bindings.</b> The list is read off the <see cref="MainWindow"/>
/// WPF would build, so the reference is checked against what the client actually takes and not
/// against a second list somebody would have to keep in step with the first — which is the failure
/// this is here to prevent, not to reproduce.</para>
///
/// <para><b>Both directions.</b> A chord taken with nothing written about it fails, which is the
/// claim. A chord the page describes that nothing binds fails too, which is what stops the reference
/// quietly becoming a description of an older client.</para>
/// </summary>
public sealed partial class KeysReferenceTests
{
    /// <summary>Where the reference lives, and it is a file a user reads.</summary>
    private const string Reference = "docs/KEYS.md";

    /// <summary>A row of one of the tables, which is the only place a chord is named.</summary>
    [GeneratedRegex(@"^\|.*$", RegexOptions.Multiline)]
    private static partial Regex Row { get; }

    /// <summary>Something in backticks, inside such a row.</summary>
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Quoted { get; }

    /// <summary>Every chord this client takes is on the page, and everything on it is taken.</summary>
    [Fact]
    public void EveryChordIsDocumentedAndEveryDocumentedChordIsTaken()
    {
        string[] taken = OnStaThread(() =>
            new MainWindow().InputBindings
                            .OfType<KeyBinding>()
                            .Select(one => Chord.Naming(one.Key, one.Modifiers))
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .ToArray());

        Assert.NotEmpty(taken);

        string[] written = [.. Written(File.ReadAllText(Page()))];

        Assert.NotEmpty(written);

        // A chord this client takes from the remote program with nothing written about it. This is
        // the falsification.
        Assert.Empty(taken.Except(written, StringComparer.Ordinal));

        // And a chord the page promises that nothing binds, which is the same page telling a user
        // about a client they are not running.
        Assert.Empty(written.Except(taken, StringComparer.Ordinal));
    }

    /// <summary>
    /// The chords this client promises never to take are not taken.
    ///
    /// <para><b>Ctrl+C is the one that matters.</b> The page states it as a promise rather than as a
    /// description, and a promise nothing checks is one a later binding breaks silently — at the
    /// moment a user is trying to stop something, which is the worst moment this client has.</para>
    /// </summary>
    [Theory]
    [InlineData(Key.C, ModifierKeys.Control)]
    [InlineData(Key.V, ModifierKeys.Control)]
    [InlineData(Key.F1, ModifierKeys.None)]
    [InlineData(Key.PageUp, ModifierKeys.None)]
    [InlineData(Key.PageDown, ModifierKeys.None)]
    [InlineData(Key.Tab, ModifierKeys.None)]
    public void TheChordsLeftToTheRemoteProgramAreLeftToIt(Key key, ModifierKeys modifiers)
    {
        bool[] bound = OnStaThread(() =>
            new MainWindow().InputBindings
                            .OfType<KeyBinding>()
                            .Select(one => one.Key == key && one.Modifiers == modifiers)
                            .ToArray());

        Assert.DoesNotContain(true, bound);
    }

    /// <summary>Every chord named in a table row of the reference.</summary>
    private static IEnumerable<string> Written(string page) =>
        Row.Matches(page)
           .SelectMany(row => Quoted.Matches(row.Value).Select(one => one.Groups[1].Value))
           .Where(IsChord)
           .Distinct(StringComparer.Ordinal)
           .Order(StringComparer.Ordinal);

    /// <summary>
    /// Whether a backticked thing in a row is a chord at all.
    ///
    /// <para>A row's second column may quote a setting's name or a command, and neither of those is
    /// a claim about what this client takes.</para>
    /// </summary>
    private static bool IsChord(string quoted)
    {
        string[] parts = quoted.Split('+', StringSplitOptions.None);

        // A trailing "+" as the key itself would split into an empty last part; nothing binds it
        // today, and reading it as a chord would be reading "Ctrl+" as one too.
        return parts.Length > 1
               && parts[^1].Length > 0
               && parts[..^1].All(part => part is "Ctrl" or "Alt" or "Shift");
    }

    /// <summary>The reference, found by the file that names the solution.</summary>
    private static string Page()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, Reference.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>A window is a WPF object, so it is built where WPF can build one.</summary>
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
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the window could not be built", failed);
        }

        return result;
    }
}
