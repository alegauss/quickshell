namespace Quickshell.App;

/// <summary>
/// One tab, which holds a tree of terminals rather than one.
///
/// <para><b>QS48: a tab owns a layout and the layout owns the panes.</b> Watching a log on one host
/// while typing on another is the ordinary day for this audience, and a tab that held a single
/// session made it impossible rather than merely awkward.</para>
///
/// <para><b>The tree holds numbers and this holds what the numbers mean.</b> That division is what
/// lets the hard part — deciding which pane is to the right of this one — be checked as arithmetic
/// with no window anywhere near it. What is here is the other half: a leaf per number, and the one
/// that has the keyboard.</para>
///
/// <para><b>Splitting moves a terminal from one number to another</b>, because a pane that is split
/// becomes the split. <see cref="PaneLayout.Moved"/> says when, and the dictionary follows it — a
/// caller never has to know it happened.</para>
/// </summary>
public sealed class TerminalTab : IAsyncDisposable
{
    private readonly Dictionary<int, TerminalLeaf> _leaves = [];
    private readonly Settings _settings;

    private int _focused;
    private bool _disposed;

    private TerminalTab(Settings settings, string host, TerminalLeaf first)
    {
        _settings = settings;

        Host = host;

        Layout = new PaneLayout();
        Layout.Moved += Follow;

        _focused = Layout.First;
        _leaves[_focused] = first;
    }

    /// <summary>Where the panes are, in proportions of this tab.</summary>
    public PaneLayout Layout { get; }

    /// <summary>What a new pane in this tab connects to.</summary>
    public string Host { get; }

    /// <summary>Every terminal this tab holds, in the order they appear on screen.</summary>
    public IReadOnlyList<TerminalLeaf> Leaves => [.. Layout.Panes.Select(pane => _leaves[pane])];

    /// <summary>The pane with the keyboard, which is the one every surface answers for.</summary>
    public TerminalLeaf Focused => _leaves[_focused];

    /// <summary>Which pane that is, in the layout's own numbering.</summary>
    public int FocusedPane => _focused;

    /// <summary>
    /// Which pane fills the tab on its own, or -1 when they all share it.
    ///
    /// <para>The cheapest genuinely useful thing here: a cramped four-way split becomes a workable
    /// one for as long as somebody needs it, and the arrangement is exactly as it was afterwards
    /// because nothing about the tree was touched.</para>
    /// </summary>
    public int Zoomed { get; private set; } = -1;

    /// <summary>
    /// The name the user gave this tab, which outranks everything the hosts have to say.
    /// </summary>
    public string? Named { get; set; }

    /// <summary>What the strip shows: the user's name for it, or the focused pane's own title.</summary>
    public string Title => Named is { Length: > 0 } named ? named : Focused.Title;

    /// <summary>Whether anything in this tab is still connected.</summary>
    public bool IsLive => _leaves.Values.Any(leaf => leaf.IsLive);

    /// <summary>Whether output arrived in any of its panes while the tab was not on screen.</summary>
    public bool HasActivity => _leaves.Values.Any(leaf => leaf.HasActivity);

    /// <summary>Builds a tab with one pane in it, with a device and a loop but no session yet.</summary>
    public static TerminalTab Open(Settings settings, string host)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return new TerminalTab(settings, host, TerminalLeaf.Open(settings, host));
    }

    /// <summary>Starts a shell behind the pane that has the keyboard.</summary>
    public Task ConnectAsync(string? commandLine = null,
                             CancellationToken cancellationToken = default) =>
        Focused.ConnectAsync(commandLine, cancellationToken);

    /// <summary>
    /// Splits the focused pane and gives the new one the keyboard.
    ///
    /// <para>The keyboard moves because that is what the user asked for: somebody who splits a pane
    /// is about to type in the new one, and a client that left the focus behind makes them reach for
    /// the mouse to finish a gesture they made with the keyboard.</para>
    /// </summary>
    /// <returns>The new pane, with no session behind it yet.</returns>
    public TerminalLeaf? Split(Divide how)
    {
        int made = Layout.Split(_focused, how);

        if (made < 0)
        {
            return null;
        }

        TerminalLeaf leaf = TerminalLeaf.Open(_settings, Host);

        _leaves[made] = leaf;
        _focused = made;

        // A zoom survives nothing structural: the tab it was zoomed inside is a different tab now.
        Zoomed = -1;

        return leaf;
    }

    /// <summary>
    /// Closes the focused pane and hands its space to the sibling, which takes the keyboard.
    /// </summary>
    /// <returns>The terminal that went, for the caller to end, or null where this was the last.</returns>
    public TerminalLeaf? ClosePane()
    {
        if (!_leaves.TryGetValue(_focused, out TerminalLeaf? going))
        {
            return null;
        }

        int took = Layout.Close(_focused);

        if (took < 0)
        {
            return null;
        }

        _leaves.Remove(_focused);

        _focused = took;
        Zoomed = -1;

        return going;
    }

    /// <summary>Moves the keyboard to the pane in a direction, and stays put where there is none.</summary>
    /// <returns>Whether it moved.</returns>
    public bool Focus(Toward direction)
    {
        int found = Layout.Beyond(_focused, direction);

        if (found < 0)
        {
            return false;
        }

        _focused = found;

        return true;
    }

    /// <summary>Gives the keyboard to a pane by its number, which is what a click does.</summary>
    public void Focus(int pane)
    {
        if (_leaves.ContainsKey(pane))
        {
            _focused = pane;
        }
    }

    /// <summary>Which pane a terminal is in, or -1 where it is not in this tab at all.</summary>
    public int PaneOf(TerminalLeaf leaf)
    {
        foreach ((int pane, TerminalLeaf held) in _leaves)
        {
            if (ReferenceEquals(held, leaf))
            {
                return pane;
            }
        }

        return -1;
    }

    /// <summary>The terminal in a pane, or null where that number is not one.</summary>
    public TerminalLeaf? In(int pane) => _leaves.GetValueOrDefault(pane);

    /// <summary>Fills the tab with the focused pane, or gives the others their space back.</summary>
    public void Zoom() => Zoomed = Zoomed < 0 ? _focused : -1;

    /// <summary>This tab is the one on screen now, or is no longer.</summary>
    public void Showing(bool showing)
    {
        foreach (TerminalLeaf leaf in _leaves.Values)
        {
            leaf.Showing(showing);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Layout.Moved -= Follow;

        foreach (TerminalLeaf leaf in _leaves.Values)
        {
            await leaf.DisposeAsync().ConfigureAwait(false);
        }

        _leaves.Clear();
    }

    /// <summary>A pane became a split, so the terminal that was in it lives under a new number.</summary>
    private void Follow(int was, int now)
    {
        if (!_leaves.Remove(was, out TerminalLeaf? leaf))
        {
            return;
        }

        _leaves[now] = leaf;

        if (_focused == was)
        {
            _focused = now;
        }

        if (Zoomed == was)
        {
            Zoomed = now;
        }
    }
}
