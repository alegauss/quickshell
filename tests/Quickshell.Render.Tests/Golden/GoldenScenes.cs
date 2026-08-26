using Quickshell.Render;
using Quickshell.Terminal;
using Vortice.DirectWrite;

namespace Quickshell.Render.Tests.Golden;

/// <summary>
/// The scenes the renderer is judged by looking at, and the writer that lays cells out for them.
///
/// <para>Each is a named function of an atlas and a grid, so the same scene renders identically on
/// every adapter and the only variable left is the driver — which is the whole point.</para>
/// </summary>
internal static class GoldenScenes
{
    /// <summary>The window every scene is rendered into. Fixed, because the reference is a picture.</summary>
    internal const uint Width = 560;

    /// <summary>The height of that window.</summary>
    internal const uint Height = 220;

    private static readonly Rgb Ground = new(16, 18, 24);
    private static readonly Rgb Text = new(214, 219, 228);
    private static readonly Rgb Green = new(126, 198, 153);
    private static readonly Rgb Amber = new(228, 190, 120);
    private static readonly Rgb Red = new(228, 120, 120);

    /// <summary>
    /// One scene: the name its reference is filed under, what it draws, and how far a pixel of it
    /// may drift before the difference is the renderer's rather than the machine's.
    /// </summary>
    /// <param name="Name">What the reference image is filed under.</param>
    /// <param name="Font">The font this scene is drawn in.</param>
    /// <param name="Paint">What it draws.</param>
    /// <param name="LevelTolerance">
    /// How far one channel may differ from the reference. <b>It is a property of the scene, not of
    /// the suite</b>, because a scene with glyphs in it is measuring two things: this renderer's
    /// arithmetic, which is ours and deterministic, and DirectWrite's glyph coverage, which is not
    /// ours and differs between machines. See <see cref="All"/> for the measurement.
    /// </param>
    internal sealed record Scene(string Name, FontSettings Font, Action<Painter> Paint,
                                 int LevelTolerance = TextTolerance);

    /// <summary>
    /// What a scene containing text may differ by.
    ///
    /// <para><b>Measured, and it is not the shader.</b> QS96: the guest's `attributes` scene differed
    /// from its reference in one pixel of 123,200, by six levels. Six levels of output needs about
    /// fifteen levels of coverage — and the shader's own arithmetic cannot move the output even one
    /// level, which was checked by perturbing its sRGB conversion by a part in a million across every
    /// coverage value and watching the encoded result not change. So the six is DirectWrite's glyph
    /// rasterisation differing between machines, which no amount of care here makes identical.</para>
    ///
    /// <para>Eight is that six with room. It is not a number chosen to make a vendor pass: a
    /// structural regression moves the picture by <em>hundreds</em> of levels — a one-pixel shift of
    /// the underline was measured at 204 — so this still refuses everything the suite exists to
    /// catch, and <see cref="GlyphFree"/> is what holds the part that is genuinely ours to one.</para>
    /// </summary>
    internal const int TextTolerance = 8;

    /// <summary>
    /// What a scene with no glyphs in it may differ by.
    ///
    /// <para>One, and that is the tight claim this suite can actually make. With no coverage in the
    /// picture, everything left is the renderer's own arithmetic: the linear blend, the rules, the
    /// cursor shapes, the selection. That is deterministic, and a driver disagreeing about it is a
    /// finding rather than a fact of life.</para>
    /// </summary>
    internal const int GlyphFree = 1;

    /// <summary>
    /// Every scene, in the order the design lists them.
    ///
    /// <para>The design also asks for a screen of <c>htop</c> output replayed from a captured
    /// corpus. There is no parser and no corpus yet, so that scene is not here and cannot be: it
    /// belongs to the line that lands the pseudo-console.</para>
    /// </summary>
    internal static IReadOnlyList<Scene> All { get; } =
    [
        new("text-small", new FontSettings("Consolas", 11f, 96f), PlainText),
        new("text-large", new FontSettings("Consolas", 20f, 96f), PlainText),
        new("attributes", new FontSettings("Consolas", 16f, 96f), Attributes),
        new("box-drawing", new FontSettings("Consolas", 16f, 96f), BoxDrawing),
        new("mixed-scripts", new FontSettings("Consolas", 16f, 96f), MixedScripts),
        new("cursors-over-selection", new FontSettings("Consolas", 16f, 96f), CursorsOverSelection),
        new("undercurl-run", new FontSettings("Consolas", 16f, 96f), UndercurlRun),

        // No glyphs at all, so nothing here is DirectWrite's. What this scene compares is the blend,
        // the rules and the cursor shapes - all of which are this renderer's own arithmetic, and all
        // of which must therefore agree across drivers to within a level.
        new("no-glyphs", new FontSettings("Consolas", 16f, 96f), NoGlyphs, GlyphFree),
    ];

    private static void NoGlyphs(Painter painter)
    {
        painter.Bare(0, 0, 40, Text, new Rgb(40, 44, 56));
        painter.Bare(1, 0, 40, Text, new Rgb(80, 30, 30), underline: UnderlineStyle.Single);
        painter.Bare(2, 0, 40, Text, new Rgb(30, 80, 30), underline: UnderlineStyle.Double);
        painter.Bare(3, 0, 40, Red, new Rgb(24, 26, 34), underline: UnderlineStyle.Curly);
        painter.Bare(4, 0, 20, Amber, Ground, underline: UnderlineStyle.Dotted);
        painter.Bare(4, 20, 20, Amber, Ground, underline: UnderlineStyle.Dashed);
        painter.Bare(5, 0, 40, Text, Ground, CellFlags.Overline | CellFlags.Strike);
        painter.Bare(6, 0, 40, Text, Ground, CellFlags.Selected);

        painter.Cursor(7, 2, ' ', Text, Ground, CursorShape.Block);
        painter.Cursor(7, 6, ' ', Text, Ground, CursorShape.Bar);
        painter.Cursor(7, 10, ' ', Text, Ground, CursorShape.Underline);
        painter.Cursor(7, 14, ' ', Text, Ground, CursorShape.Block, CellFlags.Selected);
    }

    private static void PlainText(Painter painter)
    {
        painter.Write(0, 0, "The quick brown fox jumps over the lazy dog", Text);
        painter.Write(1, 0, "0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~", Text);
        painter.Write(2, 0, "quickshell $ ssh user@host -p 2222", Green);
        painter.Write(3, 0, "Connection established in 41 ms", Amber);
        painter.Write(5, 0, "iiiiiiiiii mmmmmmmmmm WWWWWWWWWW", Text);
    }

    private static void Attributes(Painter painter)
    {
        painter.Write(0, 0, "regular", Text);
        painter.Write(0, 10, "bold", Text, CellFlags.Bold, weight: FontWeight.Bold);
        painter.Write(0, 16, "italic", Text, CellFlags.Slant, slant: FontStyle.Italic);
        painter.Write(0, 24, "inverse", Text, CellFlags.Inverse);
        painter.Write(1, 0, "single", Text, underline: UnderlineStyle.Single);
        painter.Write(1, 8, "double", Text, underline: UnderlineStyle.Double);
        painter.Write(1, 16, "curly", Red, underline: UnderlineStyle.Curly);
        painter.Write(1, 23, "dotted", Amber, underline: UnderlineStyle.Dotted);
        painter.Write(1, 31, "dashed", Amber, underline: UnderlineStyle.Dashed);
        painter.Write(2, 0, "overline", Text, CellFlags.Overline);
        painter.Write(2, 10, "strike", Text, CellFlags.Strike);
        painter.Write(2, 18, "both", Text, CellFlags.Overline | CellFlags.Strike);
        painter.Write(3, 0, "selected", Text, CellFlags.Selected);
        painter.Write(3, 10, "selected+underlined", Text, CellFlags.Selected, UnderlineStyle.Single);
    }

    private static void BoxDrawing(Painter painter)
    {
        painter.Write(0, 0, "┌──────────┬──────────┐", Text);
        painter.Write(1, 0, "│ left     │ right    │", Text);
        painter.Write(2, 0, "├──────────┼──────────┤", Text);
        painter.Write(3, 0, "│ ▁▂▃▄▅▆▇█ │ ░▒▓█▀▄▌▐ │", Green);
        painter.Write(4, 0, "└──────────┴──────────┘", Text);
        painter.Write(5, 0, "╔══════╗ ╭──────╮ ┏━━━━━━┓", Amber);
        painter.Write(6, 0, "╚══════╝ ╰──────╯ ┗━━━━━━┛", Amber);
    }

    private static void MixedScripts(Painter painter)
    {
        painter.Write(0, 0, "latin Ж greek Ω cyrillic д", Text);
        painter.Write(1, 0, "中文字 日本語 한국어", Text);
        painter.Write(2, 0, "\U0001F600 \U0001F680 \U0001F534 \U0001F9E0", Text);
        painter.Write(3, 0, "mixed 中 and \U0001F600 inline", Green);
    }

    private static void CursorsOverSelection(Painter painter)
    {
        painter.Write(0, 0, "block cursor here", Text, CellFlags.Selected);
        painter.Cursor(0, 6, 'c', Text, Ground, CursorShape.Block, CellFlags.Selected);

        painter.Write(1, 0, "bar cursor here", Text, CellFlags.Selected);
        painter.Cursor(1, 4, 'c', Text, Ground, CursorShape.Bar, CellFlags.Selected);

        painter.Write(2, 0, "underline cursor here", Text, CellFlags.Selected);
        painter.Cursor(2, 10, 'c', Text, Ground, CursorShape.Underline, CellFlags.Selected);

        painter.Write(4, 0, "no cursor, no selection", Text);
    }

    private static void UndercurlRun(Painter painter)
    {
        painter.Write(0, 0, "a long undercurl running the whole width of it", Red,
                      underline: UnderlineStyle.Curly);
        painter.Write(2, 0, "error: cannot find symbol 'quickshell::render'", Red,
                      underline: UnderlineStyle.Curly);
        painter.Write(4, 0, "warning: unused variable", Amber, underline: UnderlineStyle.Curly);
    }

    /// <summary>Lays characters into a grid of instances, resolving each glyph through the atlas.</summary>
    internal sealed class Painter
    {
        private readonly CellInstance[] _cells;
        private readonly GlyphAtlas _atlas;
        private readonly CellMetrics _metrics;
        private readonly int _columns;
        private readonly int _rows;

        internal Painter(CellInstance[] cells, GlyphAtlas atlas, CellMetrics metrics, int columns, int rows)
        {
            _cells = cells;
            _atlas = atlas;
            _metrics = metrics;
            _columns = columns;
            _rows = rows;

            Array.Fill(_cells, CellInstance.For(GlyphPlacement.Empty, Text, Ground));
        }

        internal void Write(int row, int column, string text, Rgb foreground,
                            CellFlags flags = CellFlags.None,
                            UnderlineStyle underline = UnderlineStyle.None,
                            FontWeight weight = FontWeight.Normal,
                            FontStyle slant = FontStyle.Normal)
        {
            for (int index = 0; index < text.Length && column < _columns && row < _rows; index++)
            {
                int codepoint = text[index];

                if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length)
                {
                    codepoint = char.ConvertToUtf32(text[index], text[index + 1]);
                    index++;
                }

                int span = CharacterWidth.Of(codepoint);

                if (span == 0 || column + span > _columns)
                {
                    continue;
                }

                GlyphPlacement glyph = _atlas.Cache(codepoint, weight, slant,
                                                    maximumAdvance: _metrics.Width * span);

                _cells[(row * _columns) + column] =
                    CellInstance.For(glyph, foreground, Ground, flags, span, underline);

                if (span == 2)
                {
                    _cells[(row * _columns) + column + 1] =
                        CellInstance.For(GlyphPlacement.Empty, foreground, Ground, flags, 0, underline);
                }

                column += span;
            }
        }

        /// <summary>A run of cells with no glyph in them: colour, rules and nothing DirectWrite drew.</summary>
        internal void Bare(int row, int column, int count, Rgb foreground, Rgb background,
                           CellFlags flags = CellFlags.None,
                           UnderlineStyle underline = UnderlineStyle.None)
        {
            if (row < 0 || row >= _rows)
            {
                return;
            }

            for (int index = 0; index < count && column + index < _columns; index++)
            {
                _cells[(row * _columns) + column + index] =
                    CellInstance.For(GlyphPlacement.Empty, foreground, background, flags, 1, underline);
            }
        }

        internal void Cursor(int row, int column, char character, Rgb foreground, Rgb background,
                             CursorShape shape, CellFlags flags = CellFlags.None)
        {
            // A scene that reaches past the grid is a scene written against a font size it does not
            // have. Silently dropping it would put a reference on disk missing what it meant to show,
            // so this is checked here and the scene's own bounds are what a reader sees.
            if (row < 0 || row >= _rows || column < 0 || column >= _columns)
            {
                throw new ArgumentOutOfRangeException(nameof(row),
                    $"a scene placed a cursor at ({column},{row}) in a {_columns}x{_rows} grid");
            }

            _cells[(row * _columns) + column] = CellInstance.For(
                _atlas.Cache(character), foreground, background, flags, 1, UnderlineStyle.None, shape);
        }
    }
}
