// System.IO is not in a WPF project's implicit usings, and turning WPF on for this assembly
// is what made this file name it.
using System.IO;

namespace Quickshell.App;

/// <summary>What a user is asked when they close a window with sessions open.</summary>
/// <param name="Open">
/// What is open, named. A count is not enough: "3 sessions" and "prod-db, staging, a local shell"
/// are answered differently by the same person, and the second is the one that stops a mistake.
/// </param>
public readonly record struct ClosingQuestion(IReadOnlyList<string> Open)
{
    /// <summary>The sentence a dialog puts at the top.</summary>
    public string Asking =>
        Open.Count == 1
            ? $"Close {Open[0]}?"
            : $"Close {Open.Count} sessions?";
}

/// <summary>
/// Whether to ask before closing, and how to stop being asked.
///
/// <para><b>Asks once, and honours never again.</b> A confirmation a user has switched off and which
/// comes back is worse than one that was never offered: it teaches them that the checkbox is a lie,
/// and after that they stop reading every dialog this client shows — including the one about a host
/// key that changed.</para>
///
/// <para>Nothing is asked when nothing is open, because a window with no session in it is a window,
/// and asking about it is the kind of small friction that adds up to a client people describe as
/// nagging.</para>
/// </summary>
public sealed class CloseGuard
{
    private readonly string? _file;

    private bool _stopAsking;

    private CloseGuard(string? file, bool stopAsking)
    {
        _file = file;
        _stopAsking = stopAsking;
    }

    /// <summary>A guard that asks, remembering nothing beyond this run.</summary>
    public static CloseGuard Asking() => new(null, stopAsking: false);

    /// <summary>A guard that remembers the answer across runs.</summary>
    public static CloseGuard ReadFrom(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        return new CloseGuard(file, File.Exists(file));
    }

    /// <summary>Whether the user has said not to ask again.</summary>
    public bool Silenced => _stopAsking;

    /// <summary>
    /// What to ask about closing with these sessions open, or null to close without asking.
    /// </summary>
    public ClosingQuestion? Ask(IReadOnlyList<string> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        return open.Count == 0 || _stopAsking ? null : new ClosingQuestion(open);
    }

    /// <summary>
    /// Never again, and it means never: written down, so the next launch does not ask either.
    /// </summary>
    public void NeverAgain()
    {
        _stopAsking = true;

        if (_file is null)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_file);

        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_file, "The user asked not to be warned about closing open sessions.\n");
    }
}
