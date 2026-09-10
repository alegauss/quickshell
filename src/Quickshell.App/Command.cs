using System.Windows.Input;

namespace Quickshell.App;

/// <summary>
/// A command that can say what it is called, which is what makes it findable.
///
/// <para><b>QS52's whole discipline is here.</b> The palette's list is generated from the actions
/// themselves rather than maintained beside them: a binding whose command carries a name is an entry,
/// and there is no second list to forget to add to. A hand-maintained list drifts, and a palette
/// missing a third of the actions is worse than none — the user stops trusting it and never comes
/// back.</para>
/// </summary>
public interface INamedCommand : ICommand
{
    /// <summary>What a user would type to look for this. An imperative, not a noun.</summary>
    string Name { get; }
}

/// <summary>
/// The gesture of an action the palette reaches and no key does.
///
/// <para><b>A binding with no chord is still a binding</b>, which is what keeps QS52's rule whole:
/// the palette reads its list off the window's bindings, so an action that deserves no chord of its
/// own is bound to this rather than listed somewhere else. It matches no input at all. Every chord
/// is one taken from the program on the far side, and an action somebody reaches for once in a
/// session is not worth one.</para>
/// </summary>
public sealed class PaletteOnly : InputGesture
{
    /// <summary>Nothing a keyboard or a mouse does is this gesture.</summary>
    public override bool Matches(object targetElement, InputEventArgs inputEventArgs) => false;
}

/// <summary>
/// One entry in the palette: what it is called, what it is bound to, and the thing itself.
/// </summary>
/// <param name="Name">What the action is called.</param>
/// <param name="Chord">The keys that also do it, or null where nothing does.</param>
/// <param name="Runs">The command, so running an entry and pressing the chord are the same path.</param>
public sealed record Command(string Name, string? Chord, ICommand Runs)
{
    /// <summary>Does it. The same call the key binding makes, so the two cannot diverge.</summary>
    public void Run() => Runs.Execute(null);
}

/// <summary>
/// Every action this client can perform, read off the bindings that already are that list.
/// </summary>
public static class Commands
{
    /// <summary>
    /// The actions, in the order they were bound.
    ///
    /// <para><b>Derived and never declared.</b> The window's input bindings are already the one list
    /// of everything this client does — QS162 read them to write the keys reference, and this reads
    /// them for the same reason.</para>
    ///
    /// <para>A command bound twice is one entry. <c>Ctrl+Shift+\</c> is bound to two
    /// <see cref="Key"/> values because the backslash sits in different places on different
    /// keyboards, and a palette listing it twice would be showing the user a fact about
    /// <see cref="Key"/> rather than about this client.</para>
    ///
    /// <para>A command that cannot run now is left out. "Go to tab 7" in a window with three tabs is
    /// not an action, and a list that offered it would be teaching the user that some of its entries
    /// do nothing.</para>
    /// </summary>
    /// <param name="bindings">The window's input bindings.</param>
    public static IReadOnlyList<Command> From(IEnumerable<InputBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        List<Command> found = [];
        HashSet<string> already = new(StringComparer.Ordinal);

        foreach (InputBinding binding in bindings)
        {
            if (binding.Command is not INamedCommand named || !named.CanExecute(null))
            {
                continue;
            }

            if (!already.Add(named.Name))
            {
                continue;
            }

            string? chord = binding is KeyBinding key ? Chord.Naming(key.Key, key.Modifiers) : null;

            found.Add(new Command(named.Name, chord, named));
        }

        return found;
    }

    /// <summary>
    /// What a typed query matches, best first.
    /// </summary>
    /// <param name="all">Everything the palette could offer.</param>
    /// <param name="query">What the user has typed. Empty is everything.</param>
    /// <param name="recent">
    /// The names most recently run, newest first.
    ///
    /// <para>What a user wants is usually what they wanted recently, and with nothing typed that is
    /// the only information there is to rank on.</para>
    /// </param>
    public static IReadOnlyList<Command> Matching(IReadOnlyList<Command> all, string query,
                                                  IReadOnlyList<string>? recent = null)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(query);

        List<(Command Entry, int Score, int Recent, int Order)> ranked = [];

        for (int index = 0; index < all.Count; index++)
        {
            int score = Score(all[index].Name, query);

            if (score < 0)
            {
                continue;
            }

            ranked.Add((all[index], score, Ago(recent, all[index].Name), index));
        }

        // Score first, then how recently it was run, then the order it was bound in. The last is
        // what keeps the list stable when nothing distinguishes two entries — a palette that
        // reshuffled between keystrokes would be one nobody could aim at.
        return [.. ranked.OrderByDescending(one => one.Score)
                         .ThenBy(one => one.Recent)
                         .ThenBy(one => one.Order)
                         .Select(one => one.Entry)];
    }

    /// <summary>How long ago this was run, as a place in the list, or never.</summary>
    private static int Ago(IReadOnlyList<string>? recent, string name)
    {
        if (recent is null)
        {
            return int.MaxValue;
        }

        for (int index = 0; index < recent.Count; index++)
        {
            if (string.Equals(recent[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// How well a name answers a query, or -1 where it does not answer it at all.
    ///
    /// <para><b>Subsequence and not substring</b>, so <c>spr</c> finds "Split pane right" — which is
    /// the whole reason to type into a palette rather than read it.</para>
    ///
    /// <para>The bonuses say what a good match is: letters that landed together, and letters that
    /// landed at the start of a word. Both are what a person means when they type initials.</para>
    /// </summary>
    /// <param name="name">The action's name.</param>
    /// <param name="query">What was typed.</param>
    public static int Score(string name, string query)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(query);

        string wanted = query.Trim();

        if (wanted.Length == 0)
        {
            return 0;
        }

        int score = 0;
        int at = 0;
        int last = -2;

        foreach (char letter in wanted)
        {
            if (letter == ' ')
            {
                continue;
            }

            int found = name.AsSpan(at)
                            .IndexOf([letter], StringComparison.OrdinalIgnoreCase);

            if (found < 0)
            {
                return -1;
            }

            found += at;

            score += 1;

            if (found == last + 1)
            {
                score += 10;
            }

            if (found == 0 || name[found - 1] == ' ' || name[found - 1] == '-')
            {
                score += 8;
            }

            // The first letter landing on the first letter. Without this, "sac" scores the same
            // against "Show all commands" as against "Import sessions from another client" — both
            // are three letters at three word starts — and the tie is broken by whichever was bound
            // first, which is not something a user can see or predict. Somebody typing an initial
            // means a name that starts with it.
            if (last < 0 && found == 0)
            {
                score += 15;
            }

            last = found;
            at = found + 1;
        }

        return score;
    }
}
