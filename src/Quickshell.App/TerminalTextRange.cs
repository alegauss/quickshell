using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// A span of the terminal's text, as UI Automation asks for one.
///
/// <para>The interface is wide and most of it is furniture — attributes this text does not have,
/// child elements it does not contain. What matters is the handful a screen reader actually drives:
/// <see cref="GetText"/>, <see cref="ExpandToEnclosingUnit"/>, <see cref="Move"/> and
/// <see cref="MoveEndpointByUnit"/>, and every one of those is a call into
/// <see cref="TerminalDocument"/> rather than logic of its own.</para>
///
/// <para><b>What is not supported answers as not supported</b> rather than as a lie that is cheaper
/// to write. A reader that is told a range was moved when it was not will loop.</para>
/// </summary>
public sealed class TerminalTextRange : ITextRangeProvider
{
    private readonly TerminalDocument _document;
    private readonly IRawElementProviderSimple? _owner;

    /// <summary>A range over part of a document, which is what every method here hands back.</summary>
    /// <param name="document">The buffer, read as text.</param>
    /// <param name="start">Where it begins; clamped to the document.</param>
    /// <param name="end">Where it ends; the two are sorted, so either order is accepted.</param>
    /// <param name="owner">The element a reader found this through, where there is one.</param>
    public TerminalTextRange(TerminalDocument document, int start, int end,
                             IRawElementProviderSimple? owner = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        _document = document;
        _owner = owner;

        Start = Math.Clamp(Math.Min(start, end), 0, document.Length);
        End = Math.Clamp(Math.Max(start, end), 0, document.Length);
    }

    /// <summary>Where this range begins, as an offset into the document.</summary>
    public int Start { get; private set; }

    /// <summary>Where it ends.</summary>
    public int End { get; private set; }

    /// <inheritdoc/>
    public ITextRangeProvider Clone() => new TerminalTextRange(_document, Start, End, _owner);

    /// <inheritdoc/>
    public bool Compare(ITextRangeProvider range) =>
        range is TerminalTextRange other && other.Start == Start && other.End == End;

    /// <inheritdoc/>
    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
                                TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not TerminalTextRange other)
        {
            return 0;
        }

        int mine = endpoint == TextPatternRangeEndpoint.Start ? Start : End;
        int theirs = targetEndpoint == TextPatternRangeEndpoint.Start ? other.Start : other.End;

        return mine.CompareTo(theirs);
    }

    /// <summary>
    /// Grows the range to a whole unit, which is what a reader does before reading something aloud.
    /// </summary>
    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        if (Step(unit) is not { } step)
        {
            Start = 0;
            End = _document.Length;

            return;
        }

        if (step == TextStep.Character)
        {
            End = Math.Min(_document.Length, Start + 1);

            return;
        }

        if (step == TextStep.Line)
        {
            Start = _document.StartOfLine(_document.LineOf(Start));
            End = Math.Min(_document.Length, _document.Move(Start, TextStep.Line, 1));

            // The last line has no line after it to stop at, so it runs to the end.
            End = End <= Start ? _document.Length : End;

            return;
        }

        Start = _document.Move(_document.Move(Start, TextStep.Word, 1), TextStep.Word, -1);
        End = _document.Move(Start, TextStep.Word, 1);
    }

    /// <inheritdoc/>
    public ITextRangeProvider? FindAttribute(int attribute, object value, bool backward) => null;

    /// <summary>Finds text, which is what a reader's own search is built on.</summary>
    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string all = _document.Text(Start, End - Start);
        int at = backward
            ? all.LastIndexOf(text, StringComparison.OrdinalIgnoreCase)
            : all.IndexOf(text, ignoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

        return at < 0 ? null : new TerminalTextRange(_document, Start + at, Start + at + text.Length, _owner);
    }

    /// <inheritdoc/>
    public object? GetAttributeValue(int attribute) => null;

    /// <summary>
    /// Where this text is on screen.
    ///
    /// <para>Empty, and said rather than guessed: the pane is a texture and this class has no map
    /// from an offset to a rectangle. A reader uses it to draw a highlight, and no highlight is a
    /// better answer than one over the wrong words.</para>
    /// </summary>
    public double[] GetBoundingRectangles() => [];

    /// <inheritdoc/>
    public IRawElementProviderSimple? GetEnclosingElement() => _owner;

    /// <summary>The characters, up to whatever the reader asked for.</summary>
    public string GetText(int maxLength)
    {
        int length = End - Start;

        return _document.Text(Start, maxLength < 0 ? length : Math.Min(length, maxLength));
    }

    /// <summary>Moves the whole range by units, which is a reader stepping through the output.</summary>
    public int Move(TextUnit unit, int count)
    {
        if (Step(unit) is not { } step || count == 0)
        {
            return 0;
        }

        int was = Start;
        int moved = _document.Move(Start, step, count);

        Start = moved;
        End = moved;

        ExpandToEnclosingUnit(unit);

        return was == moved ? 0 : count;
    }

    /// <inheritdoc/>
    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        if (Step(unit) is not { } step || count == 0)
        {
            return 0;
        }

        int was = endpoint == TextPatternRangeEndpoint.Start ? Start : End;
        int moved = _document.Move(was, step, count);

        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            Start = Math.Min(moved, End);
        }
        else
        {
            End = Math.Max(moved, Start);
        }

        return was == moved ? 0 : count;
    }

    /// <inheritdoc/>
    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
                                    TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not TerminalTextRange other)
        {
            return;
        }

        int to = targetEndpoint == TextPatternRangeEndpoint.Start ? other.Start : other.End;

        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            Start = Math.Min(to, End);
        }
        else
        {
            End = Math.Max(to, Start);
        }
    }

    /// <inheritdoc/>
    public void Select()
    {
        // Selection is the terminal's own, and QS30 owns it. A reader's selection landing in a
        // different model from the user's would be two selections that disagree.
    }

    /// <inheritdoc/>
    public void AddToSelection()
    {
    }

    /// <inheritdoc/>
    public void RemoveFromSelection()
    {
    }

    /// <inheritdoc/>
    public void ScrollIntoView(bool alignToTop)
    {
    }

    /// <summary>Nothing is inside a range of terminal text: it is text and not a tree.</summary>
    public IRawElementProviderSimple[] GetChildren() => [];

    /// <summary>
    /// What a UI Automation unit means to this document, or null for one it does not model.
    ///
    /// <para>A format, a page and a document are all the whole thing here — a terminal has no pages
    /// and one run of formatting is not a unit a reader navigates by.</para>
    /// </summary>
    private static TextStep? Step(TextUnit unit) => unit switch
    {
        TextUnit.Character => TextStep.Character,
        TextUnit.Word => TextStep.Word,
        TextUnit.Line or TextUnit.Paragraph => TextStep.Line,
        _ => null,
    };
}
