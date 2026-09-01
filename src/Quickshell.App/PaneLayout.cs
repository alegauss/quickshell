namespace Quickshell.App;

/// <summary>Which way a split divides the space it was given.</summary>
public enum Divide
{
    /// <summary>Side by side, so the divider is vertical and the two share the width.</summary>
    Beside,

    /// <summary>One above the other, sharing the height.</summary>
    Below,
}

/// <summary>A direction to move the focus in, which is a direction on screen.</summary>
public enum Toward
{
    /// <summary>Left.</summary>
    Left,

    /// <summary>Right.</summary>
    Right,

    /// <summary>Up.</summary>
    Up,

    /// <summary>Down.</summary>
    Down,
}

/// <summary>
/// Where a pane sits inside its tab, as proportions of it rather than pixels.
/// </summary>
/// <param name="X">The left edge, 0 at the tab's own left.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">How much of the tab's width it takes, 1 being all of it.</param>
/// <param name="Height">How much of its height.</param>
public readonly record struct Portion(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge.</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge.</summary>
    public double Bottom => Y + Height;
}

/// <summary>
/// The tree of splits a tab holds, and where each pane ends up in it.
///
/// <para><b>A tree and not a list, because any pane can be split again.</b> A model that held rows
/// of columns runs out at whatever depth its author happened to stop at; this one does not run out,
/// and the cost of that is one recursive walk in each direction.</para>
///
/// <para><b>Proportions and not pixels, so resizing the window keeps the arrangement's shape.</b>
/// Every split carries how much of its space the first child takes, and the pixels are worked out
/// from the window each time it is laid out — which means a client that has never been resized and
/// one dragged across two monitors hold the same tree.</para>
///
/// <para><b>Panes are numbers here and terminals elsewhere.</b> The tree knows nothing about a
/// session, a device or a window, which is what lets its one hard part — moving the focus by
/// direction — be checked as the arithmetic it is rather than by driving a desktop.</para>
/// </summary>
public sealed class PaneLayout
{
    /// <summary>
    /// How close two edges must be to count as touching.
    ///
    /// <para>Proportions are computed by repeated halving, so an edge that should be a half is a
    /// half give or take the last bit of a double. A comparison with no tolerance would decide that
    /// the pane immediately to the right is not to the right.</para>
    /// </summary>
    private const double Touching = 1e-9;

    private readonly Dictionary<int, Node> _nodes = [];

    private readonly int _root;

    private int _next;

    /// <summary>
    /// A layout holding one pane, which is what a tab starts as.
    ///
    /// <para>The root's id never changes, and that is why splitting turns a pane into a split in
    /// place rather than putting a new parent above it: a root that could be replaced would need
    /// every walk to start by asking what the root is now.</para>
    /// </summary>
    public PaneLayout()
    {
        _root = Add(new Node(Leaf: true));
    }

    /// <summary>Every pane, left to right and top to bottom as they appear on screen.</summary>
    public IReadOnlyList<int> Panes => [.. Where().OrderBy(one => one.Value.Y)
                                                 .ThenBy(one => one.Value.X)
                                                 .Select(one => one.Key)];

    /// <summary>How many panes this tab holds.</summary>
    public int Count => _nodes.Values.Count(node => node.Leaf);

    /// <summary>The pane a tab with one pane has, which is the one every tab opens with.</summary>
    public int First => Panes[0];

    /// <summary>
    /// Splits a pane, giving half its space to a new one.
    /// </summary>
    /// <param name="pane">The pane to divide.</param>
    /// <param name="how">Which way.</param>
    /// <returns>The new pane, or -1 where the pane named is not one.</returns>
    public int Split(int pane, Divide how)
    {
        if (!_nodes.TryGetValue(pane, out Node? node) || !node.Leaf)
        {
            return -1;
        }

        // The pane being split becomes the split, and a copy of it becomes the first child. The id
        // the caller holds therefore stays with the terminal that was already in it, which is the
        // whole reason it is done this way round rather than by making a new parent.
        int kept = Add(new Node(Leaf: true));
        int made = Add(new Node(Leaf: true));

        _nodes[pane] = new Node(Leaf: false)
        {
            How = how,
            First = kept,
            Second = made,
            Share = 0.5,
        };

        Moved?.Invoke(pane, kept);

        return made;
    }

    /// <summary>
    /// A pane was split and the terminal that was in it now lives in a new one.
    ///
    /// <para>Raised because the id a caller was holding has become a split rather than a pane, and
    /// whatever it had mapped to that id has to follow. Doing it the other way — a new parent above
    /// the old id — would leave the ids stable and the tree's root unable to be a leaf, which costs
    /// a special case in every walk instead of one event here.</para>
    /// </summary>
    public event Action<int, int>? Moved;

    /// <summary>
    /// Closes a pane and gives its space to the sibling it was sharing with.
    /// </summary>
    /// <returns>The pane that took the space, or -1 where this was the last one.</returns>
    public int Close(int pane)
    {
        if (Count <= 1 || !_nodes.TryGetValue(pane, out Node? going) || !going.Leaf)
        {
            return -1;
        }

        int parent = Parent(pane);

        if (parent < 0)
        {
            return -1;
        }

        Node split = _nodes[parent];
        int sibling = split.First == pane ? split.Second : split.First;

        // The parent becomes the sibling: whatever the sibling was — a pane or another split — takes
        // the whole of the space the two were sharing.
        _nodes[parent] = _nodes[sibling];

        _nodes.Remove(pane);
        _nodes.Remove(sibling);

        Moved?.Invoke(sibling, parent);

        return parent;
    }

    /// <summary>Gives every split back an even share, which is what a chord to equalise does.</summary>
    public void Equalise()
    {
        foreach (int at in _nodes.Keys.ToArray())
        {
            if (!_nodes[at].Leaf)
            {
                _nodes[at] = _nodes[at] with { Share = 0.5 };
            }
        }
    }

    /// <summary>
    /// Sets how much of a split's space its first child takes, which is what dragging a divider does.
    /// </summary>
    /// <param name="pane">Either child of the split to move.</param>
    /// <param name="share">The first child's share, clamped away from nothing at all.</param>
    public void Share(int pane, double share)
    {
        int parent = Parent(pane);

        if (parent >= 0)
        {
            // A pane dragged to nothing is a pane the user cannot get back to, so neither side is
            // ever allowed to disappear. Five per cent is small enough to be a deliberate choice and
            // large enough to still be clickable.
            _nodes[parent] = _nodes[parent] with { Share = Math.Clamp(share, 0.05, 0.95) };
        }
    }

    /// <summary>Where every pane sits, as proportions of the tab.</summary>
    public IReadOnlyDictionary<int, Portion> Portions => Where();

    /// <summary>
    /// The pane in a direction, by where it is on screen and not by where it is in the tree.
    ///
    /// <para><b>This is the line's falsification and the reason the tree carries geometry at all.</b>
    /// Moving right means the pane whose rectangle lies to the right — which is very often not the
    /// sibling. Split a window in half, then split the right half again: from the left pane, "right"
    /// has two candidates and neither is its sibling, because its sibling is the split holding both
    /// of them. A model that walked the tree would answer with whichever child happened to be first.
    /// </para>
    ///
    /// <para>Among the panes that really are in that direction, the nearest edge wins; where two are
    /// equally near, the one whose span the source's middle falls inside wins, which is what makes
    /// moving right out of a tall pane land beside the cursor rather than at the top.</para>
    /// </summary>
    /// <returns>The pane to move to, or -1 where there is none that way.</returns>
    public int Beyond(int from, Toward direction)
    {
        Dictionary<int, Portion> where = Where();

        if (!where.TryGetValue(from, out Portion source))
        {
            return -1;
        }

        int best = -1;
        (double Edge, double Off) score = (double.MaxValue, double.MaxValue);

        foreach ((int pane, Portion at) in where)
        {
            if (pane == from || Behind(source, at, direction) is not { } edge)
            {
                continue;
            }

            double off = Off(source, at, direction);

            if (edge < score.Edge - Touching
                || (edge < score.Edge + Touching && off < score.Off - Touching))
            {
                best = pane;
                score = (edge, off);
            }
        }

        return best;
    }

    /// <summary>
    /// How far past this pane's edge the other one starts, or null where it is not that way at all.
    /// </summary>
    private static double? Behind(Portion source, Portion other, Toward direction)
    {
        double gap = direction switch
        {
            Toward.Left => source.X - other.Right,
            Toward.Right => other.X - source.Right,
            Toward.Up => source.Y - other.Bottom,
            _ => other.Y - source.Bottom,
        };

        // Zero is the ordinary case — two panes sharing a divider touch exactly — so the tolerance
        // is what makes a divider computed by halving count as one edge rather than two.
        return gap >= -Touching ? Math.Max(0d, gap) : null;
    }

    /// <summary>
    /// How far this pane's middle is from the other's span across the direction of travel.
    ///
    /// <para>Zero where the middle falls inside it, which is what decides between two panes stacked
    /// beside a tall one: the user's cursor is somewhere in the tall pane, and the pane level with it
    /// is the one they mean.</para>
    /// </summary>
    private static double Off(Portion source, Portion other, Toward direction)
    {
        (double middle, double from, double to) = direction is Toward.Left or Toward.Right
            ? (source.Y + (source.Height / 2), other.Y, other.Bottom)
            : (source.X + (source.Width / 2), other.X, other.Right);

        return middle < from ? from - middle : Math.Max(0d, middle - to);
    }

    /// <summary>Where every pane is, worked out from the root down.</summary>
    private Dictionary<int, Portion> Where()
    {
        Dictionary<int, Portion> found = [];

        Walk(_root, new Portion(0, 0, 1, 1), found);

        return found;
    }

    private void Walk(int at, Portion space, Dictionary<int, Portion> into)
    {
        Node node = _nodes[at];

        if (node.Leaf)
        {
            into[at] = space;

            return;
        }

        if (node.How == Divide.Beside)
        {
            double width = space.Width * node.Share;

            Walk(node.First, space with { Width = width }, into);
            Walk(node.Second,
                 space with { X = space.X + width, Width = space.Width - width }, into);

            return;
        }

        double height = space.Height * node.Share;

        Walk(node.First, space with { Height = height }, into);
        Walk(node.Second, space with { Y = space.Y + height, Height = space.Height - height }, into);
    }

    /// <summary>The split holding this node, or -1 where nothing does — which is the root.</summary>
    private int Parent(int child)
    {
        foreach ((int at, Node node) in _nodes)
        {
            if (!node.Leaf && (node.First == child || node.Second == child))
            {
                return at;
            }
        }

        return -1;
    }

    private int Add(Node node)
    {
        _nodes[_next] = node;

        return _next++;
    }

    /// <summary>One node: a pane, or a split holding two.</summary>
    private sealed record Node(bool Leaf)
    {
        /// <summary>Which way it divides, for a split.</summary>
        public Divide How { get; init; }

        /// <summary>The node above or to the left.</summary>
        public int First { get; init; } = -1;

        /// <summary>The other one.</summary>
        public int Second { get; init; } = -1;

        /// <summary>How much of the space the first takes.</summary>
        public double Share { get; init; } = 0.5;
    }
}
