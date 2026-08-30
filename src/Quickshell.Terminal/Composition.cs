namespace Quickshell.Terminal;

/// <summary>Where a candidate list belongs, in grid coordinates the window converts to pixels.</summary>
/// <param name="Column">The column, which may be past the cursor's own.</param>
/// <param name="Row">The row, which is the next one down where the composition wrapped.</param>
public readonly record struct CandidatePlacement(int Column, int Row);

/// <summary>
/// Text being composed by an input method, which is on screen and not in the buffer.
///
/// <para><b>Not written into the buffer, and that is the whole of the difficulty.</b> A composition
/// is display state: it changes with every keystroke, it is drawn at the cursor with the underline
/// the convention expects, and cancelling it must leave nothing behind. A client that wrote it into
/// the buffer as it evolved would have to erase it again on every change and would leave abandoned
/// characters after a cancel — which is the failure this class exists to make impossible, because
/// the buffer is never touched at all.</para>
///
/// <para><b>Width is counted in cells and never in characters.</b> A composition of CJK takes two
/// cells per character, so a candidate list placed by counting characters lands halfway back through
/// what the user is typing. <see cref="Cells"/> is the real width and
/// <see cref="Candidate"/> is what an input method is told.</para>
///
/// <para>The committed result goes to the host as encoded characters, down the same path any other
/// typed character takes — this class hands it over and does not send it, because the path is
/// <see cref="Keys"/>' and the sending is the session's.</para>
/// </summary>
public sealed class Composition
{
    /// <summary>
    /// How long a composition may get before it stops growing.
    ///
    /// <para>A composition is a phrase somebody is typing, not a document. The ceiling is here for
    /// the same reason every other ceiling in this assembly is: the length is somebody else's choice,
    /// and an input method driven by a script is still somebody else.</para>
    /// </summary>
    public const int MaximumLength = 1024;

    private readonly char[] _text = new char[MaximumLength];

    private int _length;
    private int _caret;

    /// <summary>Whether an input method is composing right now.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The text as it currently stands, valid until the next call.</summary>
    public ReadOnlySpan<char> Text => _text.AsSpan(0, _length);

    /// <summary>Where the caret sits inside it, in characters.</summary>
    public int Caret => _caret;

    /// <summary>How many cells the whole composition occupies.</summary>
    public int Cells => CharacterWidth.Of(Text);

    /// <summary>How many cells the text before the caret occupies, which is where a candidate list
    /// goes.</summary>
    public int CellsBeforeCaret => CharacterWidth.Of(_text.AsSpan(0, _caret));

    /// <summary>Opens a composition, replacing anything left over from a previous one.</summary>
    public void Start()
    {
        IsActive = true;
        _length = 0;
        _caret = 0;
    }

    /// <summary>
    /// Replaces the text in progress. Called for every keystroke an input method takes.
    /// </summary>
    /// <param name="text">The composition as it now stands.</param>
    /// <param name="caret">Where the caret is inside it, in characters.</param>
    public void Update(ReadOnlySpan<char> text, int caret)
    {
        IsActive = true;

        // Truncated rather than refused: a composition past the ceiling is one the user can still see
        // the beginning of, and refusing it outright would lose what they had typed.
        _length = Math.Min(text.Length, MaximumLength);
        text[.._length].CopyTo(_text);

        // Never split a surrogate pair by truncating, because half a character is not one.
        if (_length > 0 && char.IsHighSurrogate(_text[_length - 1]))
        {
            _length--;
        }

        _caret = Math.Clamp(caret, 0, _length);
    }

    /// <summary>
    /// Ends the composition with nothing committed, which is what escape does.
    ///
    /// <para>Nothing has to be erased, because nothing was ever written.</para>
    /// </summary>
    public void Cancel()
    {
        IsActive = false;
        _length = 0;
        _caret = 0;
    }

    /// <summary>
    /// Takes the committed text and closes the composition.
    /// </summary>
    /// <param name="result">What the input method settled on.</param>
    /// <returns>The bytes to send, in the caller's buffer.</returns>
    /// <param name="destination">Where to put them, in the session's encoding.</param>
    public int Commit(ReadOnlySpan<char> result, Span<byte> destination)
    {
        Cancel();

        return result.IsEmpty ? 0 : System.Text.Encoding.UTF8.GetBytes(result, destination);
    }

    /// <summary>
    /// Where the candidate list goes, given where the cursor is and how wide the screen is.
    ///
    /// <para>The cursor's cell plus the width of what has been composed before the caret — <b>in
    /// cells</b>. A composition of eight Japanese characters is sixteen columns, and a list placed
    /// eight columns along would sit in the middle of what the user is reading.</para>
    ///
    /// <para>It wraps like text does, because the composition it follows does.</para>
    /// </summary>
    /// <param name="cursorColumn">The cursor's column.</param>
    /// <param name="cursorRow">The cursor's row.</param>
    /// <param name="columns">How wide the screen is.</param>
    public CandidatePlacement Candidate(int cursorColumn, int cursorRow, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);

        int at = cursorColumn + CellsBeforeCaret;

        return new CandidatePlacement(at % columns, cursorRow + (at / columns));
    }
}
