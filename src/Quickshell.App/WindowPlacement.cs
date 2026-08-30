// System.IO is not in a WPF project's implicit usings, and turning WPF on for this assembly
// is what made this file name it.
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Quickshell.App;

/// <summary>Where the window was, on a particular arrangement of screens.</summary>
/// <param name="X">Left edge, in virtual-desktop coordinates.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">How wide.</param>
/// <param name="Height">How tall.</param>
/// <param name="Maximised">Whether it was maximised, which is remembered apart from its size.</param>
public readonly record struct Placement(int X, int Y, int Width, int Height, bool Maximised);

/// <summary>
/// One screen, in the two facts that decide whether a window fits on it.
/// </summary>
/// <param name="X">Left edge in virtual-desktop coordinates.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">How wide.</param>
/// <param name="Height">How tall.</param>
public readonly record struct Screen(int X, int Y, int Width, int Height)
{
    /// <summary>Whether a placement is wholly inside this screen.</summary>
    public bool Holds(Placement placement) =>
        placement.X >= X && placement.Y >= Y
        && placement.X + placement.Width <= X + Width
        && placement.Y + placement.Height <= Y + Height;
}

/// <summary>
/// Where the window goes, remembered per arrangement of screens.
///
/// <para><b>Per arrangement, and that is the whole point.</b> A window remembered as one position
/// comes back off-screen the moment a laptop is undocked: it was at x=2400 on the second monitor
/// and there is no second monitor now. So the arrangement is part of the key — plug the dock back
/// in and the window returns to where it was on that arrangement, having never been scattered.</para>
///
/// <para><b>And it is checked on the way out as well as remembered.</b> An arrangement can be the
/// same signature and a screen still be gone — a projector unplugged between one launch and the
/// next — so a placement that no screen holds is refused and the window opens where it can be seen.
/// A window a user cannot find is worse than a window in the wrong place.</para>
/// </summary>
public sealed class WindowPlacements
{
    private readonly Dictionary<string, Placement> _byArrangement = new(StringComparer.Ordinal);
    private readonly string? _file;

    private WindowPlacements(string? file, Dictionary<string, Placement> remembered)
    {
        _file = file;
        _byArrangement = remembered;
    }

    /// <summary>How many arrangements are remembered.</summary>
    public int Count => _byArrangement.Count;

    /// <summary>An empty set, for a first run or a test.</summary>
    public static WindowPlacements Empty() => new(null, []);

    /// <summary>Reads what was remembered, or an empty set where nothing was.</summary>
    public static WindowPlacements ReadFrom(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        if (!File.Exists(file))
        {
            return new WindowPlacements(file, []);
        }

        try
        {
            Dictionary<string, Placement>? read =
                JsonSerializer.Deserialize<Dictionary<string, Placement>>(File.ReadAllBytes(file));

            return new WindowPlacements(file, read ?? []);
        }
        catch (JsonException)
        {
            // A file this cannot read is a file somebody edited or a version that moved on. Losing a
            // window position is not worth refusing to start over.
            return new WindowPlacements(file, []);
        }
    }

    /// <summary>
    /// The name one arrangement of screens goes by.
    ///
    /// <para>Built from every screen's own rectangle, in a fixed order, so the same physical setup
    /// produces the same name whichever order Windows enumerates it in. Two docks with identical
    /// geometry are one arrangement here, which is the right answer: the window fits either.</para>
    /// </summary>
    public static string Arrangement(IEnumerable<Screen> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);

        IOrderedEnumerable<Screen> ordered = screens.OrderBy(screen => screen.X)
                                                    .ThenBy(screen => screen.Y)
                                                    .ThenBy(screen => screen.Width)
                                                    .ThenBy(screen => screen.Height);

        StringBuilder name = new();

        foreach (Screen screen in ordered)
        {
            name.Append(CultureInfo.InvariantCulture,
                        $"{screen.X},{screen.Y},{screen.Width},{screen.Height};");
        }

        return name.ToString();
    }

    /// <summary>Remembers where the window is on this arrangement.</summary>
    public void Remember(IEnumerable<Screen> screens, Placement placement)
    {
        _byArrangement[Arrangement(screens)] = placement;

        if (_file is null)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_file);

        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(_byArrangement));
    }

    /// <summary>
    /// Where to open on this arrangement, or null to let the window place itself.
    ///
    /// <para>Null rather than a guess where nothing is remembered or where what is remembered no
    /// longer fits: the window manager's own idea of where a new window goes is better than any
    /// number this could invent, and it is on a screen by construction.</para>
    /// </summary>
    public Placement? For(IEnumerable<Screen> screens)
    {
        Screen[] visible = [.. screens];

        if (!_byArrangement.TryGetValue(Arrangement(visible), out Placement remembered))
        {
            return null;
        }

        // Maximised is remembered on its own: a maximised window has no meaningful rectangle to
        // check, and it lands on a screen whatever happened to the others.
        return remembered.Maximised || visible.Any(screen => screen.Holds(remembered))
            ? remembered
            : null;
    }
}
