namespace Quickshell.Render;

/// <summary>
/// A skyline bottom-left allocator over one atlas page.
///
/// <para>The skyline is the profile of what has already been placed: a list of horizontal segments,
/// each at the height allocation has reached there. A rectangle goes at the leftmost place whose
/// profile it can sit on with the lowest resulting top, which is what keeps a page of glyphs packed
/// in rows without anything having to know that glyphs come in rows.</para>
///
/// <para>Nothing is ever freed here. Reclaiming a hole inside a page costs more bookkeeping than the
/// memory is worth, so the atlas evicts a whole page and calls <see cref="Reset"/>.</para>
/// </summary>
public sealed class SkylinePacker
{
    private readonly List<Segment> _skyline = [];

    /// <summary>One run of the profile: everything from <c>X</c> for <c>Width</c> is filled to <c>Y</c>.</summary>
    private readonly record struct Segment(int X, int Y, int Width);

    /// <summary>Opens an empty packer over a page of the given size.</summary>
    public SkylinePacker(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
        Reset();
    }

    /// <summary>The page's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The page's height in pixels.</summary>
    public int Height { get; }

    /// <summary>Pixels handed out so far, which is what a page is worth keeping measured against.</summary>
    public long Used { get; private set; }

    /// <summary>How many rectangles are currently placed.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Places a rectangle, or answers false and changes nothing. False is not an error: it is the
    /// page saying the atlas needs another one.
    /// </summary>
    public bool TryAllocate(int width, int height, out int x, out int y)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        x = 0;
        y = 0;

        int bestTop = int.MaxValue;
        int bestWidth = int.MaxValue;
        int bestIndex = -1;

        for (int index = 0; index < _skyline.Count; index++)
        {
            if (!Fits(index, width, height, out int candidate))
            {
                continue;
            }

            // Lowest top wins; a tie goes to the narrower segment, which leaves the wide runs of
            // free profile intact for the next rectangle that needs one.
            if (candidate + height < bestTop || (candidate + height == bestTop && _skyline[index].Width < bestWidth))
            {
                bestTop = candidate + height;
                bestWidth = _skyline[index].Width;
                bestIndex = index;
                x = _skyline[index].X;
                y = candidate;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        Raise(bestIndex, x, y + height, width);
        Used += (long)width * height;
        Count++;
        return true;
    }

    /// <summary>Empties the page. The pixels are still there; nothing points at them any more.</summary>
    public void Reset()
    {
        _skyline.Clear();
        _skyline.Add(new Segment(0, 0, Width));
        Used = 0;
        Count = 0;
    }

    /// <summary>
    /// The lowest top a rectangle of this size can have if its left edge is at segment
    /// <paramref name="index"/>, or false if it runs off the page in either direction.
    /// </summary>
    private bool Fits(int index, int width, int height, out int top)
    {
        top = 0;

        if (_skyline[index].X + width > Width)
        {
            return false;
        }

        int remaining = width;

        for (int i = index; remaining > 0; i++)
        {
            if (i >= _skyline.Count)
            {
                return false;
            }

            top = Math.Max(top, _skyline[i].Y);

            if (top + height > Height)
            {
                return false;
            }

            remaining -= _skyline[i].Width;
        }

        return true;
    }

    /// <summary>Raises the profile over the rectangle just placed, then flattens what became level.</summary>
    private void Raise(int index, int x, int top, int width)
    {
        _skyline.Insert(index, new Segment(x, top, width));

        for (int i = index + 1; i < _skyline.Count; i++)
        {
            Segment previous = _skyline[i - 1];
            Segment current = _skyline[i];
            int overlap = previous.X + previous.Width - current.X;

            if (overlap <= 0)
            {
                break;
            }

            if (current.Width <= overlap)
            {
                _skyline.RemoveAt(i);
                i--;
                continue;
            }

            _skyline[i] = current with { X = current.X + overlap, Width = current.Width - overlap };
            break;
        }

        Merge();
    }

    /// <summary>
    /// Joins neighbouring segments at the same height. Without it the profile grows a segment per
    /// allocation and every later search walks all of them.
    /// </summary>
    private void Merge()
    {
        for (int i = 0; i < _skyline.Count - 1; i++)
        {
            if (_skyline[i].Y != _skyline[i + 1].Y)
            {
                continue;
            }

            _skyline[i] = _skyline[i] with { Width = _skyline[i].Width + _skyline[i + 1].Width };
            _skyline.RemoveAt(i + 1);
            i--;
        }
    }
}
