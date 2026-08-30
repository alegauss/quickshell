using Quickshell.Render;
using Vortice.DirectWrite;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// Shaping against real DirectWrite and real faces. Nothing here is mocked, for the same reason the
/// atlas tests are not: a shaper that agrees with a stub and disagrees with the font is exactly the
/// failure the line asks about.
///
/// <para><b>Three fonts, and the three of them are the argument.</b> <b>Consolas</b> is on every
/// Windows and has no programming ligatures, so it is what proves this class does not invent them.
/// <b>Cascadia Code</b> has them. <b>Cascadia Mono</b> is the same face with them taken out, which
/// is the control: the two differ in nothing else, so a glyph that changes between them changed
/// because of a ligature and not because of anything else about the font.</para>
///
/// <para>Both Cascadias ship with Windows Terminal rather than with Windows, so the tests that need
/// them skip where they are absent and say so instead of passing quietly.</para>
/// </summary>
public sealed class TextShaperTests
{
    /// <summary>A monospaced face on every Windows, and one with no programming ligature in it.</summary>
    private static readonly FontSettings Plain = new("Consolas", 16f, 96f) { Ligatures = true };

    /// <summary>A programming face with ligatures, where this machine has it.</summary>
    private static readonly FontSettings Programming = new("Cascadia Code", 16f, 96f) { Ligatures = true };

    /// <summary>The same face with the ligatures taken out, which is what makes the pair a control.</summary>
    private static readonly FontSettings Control = new("Cascadia Mono", 16f, 96f) { Ligatures = true };

    /// <summary>The arrow every argument about terminal ligatures is actually about.</summary>
    private const string Arrow = "=>";

    /// <summary>
    /// The shaper found the script it needs. Every other test in this file degrades quietly to one
    /// cluster per character without it — including the ones asserting that nothing ligated — so
    /// this is the one that has to fail first.
    /// </summary>
    [Fact]
    public void TheShaperFoundTheScriptEveryProgrammingLigatureLivesIn()
    {
        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        Assert.True(shaper.CanShape,
                    "DirectWrite named no Latin script, so nothing below is shaping anything");
    }

    /// <summary>
    /// The symptom, gone: the arrow's two characters become two glyphs the character map does not
    /// give, which is a monospaced face forming its ligature the only way it can.
    /// </summary>
    [Fact]
    public void AProgrammingFaceSubstitutesAGlyphTheCharacterMapDoesNotGive()
    {
        SkipWithout(Programming);

        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        ShapedRun run = shaper.Shape(Arrow, Programming);

        Assert.Equal(2, run.Count);
        Assert.NotEqual(Mapped(rasteriser, Programming, '='), run.Clusters[0].Glyph);
        Assert.NotEqual(Mapped(rasteriser, Programming, '>'), run.Clusters[1].Glyph);
    }

    /// <summary>
    /// The control: the same face without ligatures leaves both characters exactly as the character
    /// map has them. Without this, the test above would pass against a shaper that returned any two
    /// wrong numbers.
    /// </summary>
    [Fact]
    public void TheSameFaceWithoutLigaturesSubstitutesNothing()
    {
        SkipWithout(Control);

        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        ShapedRun run = shaper.Shape(Arrow, Control);

        Assert.Equal(Mapped(rasteriser, Control, '='), run.Clusters[0].Glyph);
        Assert.Equal(Mapped(rasteriser, Control, '>'), run.Clusters[1].Glyph);
    }

    /// <summary>A face with no programming ligatures forms none, on the Windows every user has.</summary>
    [Fact]
    public void AFaceWithNoLigatureFormsNone()
    {
        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        ShapedRun run = shaper.Shape(Arrow, Plain);

        Assert.False(run.HasLigature);
        Assert.Equal(2, run.Count);
        Assert.Equal(Mapped(rasteriser, Plain, '='), run.Clusters[0].Glyph);
        Assert.Equal(Mapped(rasteriser, Plain, '>'), run.Clusters[1].Glyph);
    }

    /// <summary>
    /// The grid's own invariant, and the reason a terminal can have ligatures at all: substitution
    /// changes which glyph a cell holds and never how many cells a run occupies.
    /// </summary>
    [Fact]
    public void ALigatureOccupiesExactlyTheCellsItsCharactersDid()
    {
        SkipWithout(Programming);

        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        CellMetrics metrics = rasteriser.Measure(Programming);
        Span<ShapedCluster> clusters = stackalloc ShapedCluster[16];

        foreach (string text in new[] { Arrow, "!=", "->", "<==>", "a => b" })
        {
            int written = shaper.Draw(text, Programming, FontWeight.Normal, FontStyle.Normal,
                                      metrics.Width, -1, clusters);
            int cells = 0;

            foreach (ShapedCluster cluster in clusters[..written])
            {
                Assert.InRange(cluster.Length, 1, CellInstance.MaximumSpan);
                cells += cluster.Length;
            }

            Assert.Equal(text.Length, cells);
        }
    }

    /// <summary>
    /// Off by default, and off means unshaped rather than shaped-and-discarded: a user who does not
    /// want ligatures should not be paying for a shaping call per run to not get them.
    /// </summary>
    [Fact]
    public void LigaturesOffIsTheCharacterMapAndNoShapingAtAll()
    {
        SkipWithout(Programming);

        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        Assert.False(FontSettings.Default.Ligatures, "ligatures are on by default");

        FontSettings off = Programming with { Ligatures = false };
        Span<ShapedCluster> clusters = stackalloc ShapedCluster[Arrow.Length];
        int written = shaper.Draw(Arrow, off, FontWeight.Normal, FontStyle.Normal, 0f, -1, clusters);

        Assert.Equal(2, written);
        Assert.Equal(Mapped(rasteriser, off, '='), clusters[0].Glyph);
        Assert.Equal(Mapped(rasteriser, off, '>'), clusters[1].Glyph);
        Assert.Equal(0, shaper.Shapings);
    }

    /// <summary>
    /// The falsification the design names, and the one this whole class exists to satisfy: with the
    /// cursor on either half of the arrow, that half is the character again rather than a piece of
    /// an arrow, so the user can see which one they are on.
    /// </summary>
    [Fact]
    public void TheCursorPutsBackTheCharacterItSitsOn()
    {
        SkipWithout(Programming);

        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        CellMetrics metrics = rasteriser.Measure(Programming);
        Span<ShapedCluster> clusters = stackalloc ShapedCluster[Arrow.Length];

        for (int caret = 0; caret < Arrow.Length; caret++)
        {
            int written = shaper.Draw(Arrow, Programming, FontWeight.Normal, FontStyle.Normal,
                                      metrics.Width, caret, clusters);

            Assert.Equal(2, written);
            Assert.Equal(Mapped(rasteriser, Programming, Arrow[caret]), clusters[caret].Glyph);

            // And only that cell: the other half is still the ligature's, so the arrow does not
            // vanish entirely the moment the cursor is anywhere near it.
            int other = 1 - caret;
            Assert.NotEqual(Mapped(rasteriser, Programming, Arrow[other]), clusters[other].Glyph);
        }
    }

    /// <summary>And with the cursor anywhere else on the line, the ligature is left alone.</summary>
    [Fact]
    public void ACaretElsewhereOnTheRunLeavesTheLigatureFormed()
    {
        SkipWithout(Programming);

        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        const string line = "a => b";

        CellMetrics metrics = rasteriser.Measure(Programming);
        Span<ShapedCluster> clusters = stackalloc ShapedCluster[16];

        int written = shaper.Draw(line, Programming, FontWeight.Normal, FontStyle.Normal,
                                  metrics.Width, 0, clusters);

        Assert.Equal(line.Length, written);
        Assert.NotEqual(Mapped(rasteriser, Programming, '='), clusters[2].Glyph);
        Assert.NotEqual(Mapped(rasteriser, Programming, '>'), clusters[3].Glyph);
    }

    /// <summary>A run's clusters cover every character exactly once, in order, whatever ligated.</summary>
    [Fact]
    public void ClustersCoverEveryCharacterExactlyOnceAndInOrder()
    {
        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        ShapedRun run = shaper.Shape("if (a != b) { return a->c; }", Plain);
        int expected = 0;

        foreach (ShapedCluster cluster in run.Clusters)
        {
            Assert.Equal(expected, cluster.First);
            expected += cluster.Length;
        }

        Assert.Equal(run.Text.Length, expected);
    }

    /// <summary>The same run is shaped once and answered from the cache thereafter.</summary>
    [Fact]
    public void TheSameRunIsShapedOnceAndAnsweredThereafter()
    {
        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        ShapedRun first = shaper.Shape("return", Plain);

        for (int again = 0; again < 50; again++)
        {
            Assert.Same(first, shaper.Shape("return", Plain));
        }

        Assert.Equal(1, shaper.Shapings);
        Assert.Equal(1, shaper.CachedRuns);
    }

    /// <summary>
    /// The cost the design says must be measured against a highlighted source file rather than
    /// against prose.
    ///
    /// <para>A run is an attribute span, so a line where every cell carries a different colour is
    /// one run per cell, and that is what this records. What makes it survivable is the second
    /// frame, where the same line costs nothing — which is the claim the cache actually makes and
    /// the one worth asserting.</para>
    /// </summary>
    [Fact]
    public void AHighlightedLineCostsOneShapingPerDistinctRunAndThenNone()
    {
        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        const string line = "public static int Sum(int a, int b) => a + b;";

        for (int cell = 0; cell < line.Length; cell++)
        {
            shaper.Shape(line.AsSpan(cell, 1), Plain);
        }

        int firstFrame = shaper.Shapings;

        for (int again = 0; again < 10; again++)
        {
            for (int cell = 0; cell < line.Length; cell++)
            {
                shaper.Shape(line.AsSpan(cell, 1), Plain);
            }
        }

        // Distinct characters rather than cells: the cache is keyed on the run's text, so a line
        // that repeats a character pays for it once and the degenerate case is bounded by the
        // alphabet rather than by the width of the window.
        Assert.Equal(line.Distinct().Count(), firstFrame);
        Assert.Equal(firstFrame, shaper.Shapings);
    }

    /// <summary>
    /// A run is a token in highlighted source and the set of distinct tokens has no bound, so the
    /// cache has one. Reaching it empties the cache rather than growing without limit.
    /// </summary>
    [Fact]
    public void TheCacheIsEmptiedRatherThanGrownWithoutBound()
    {
        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        for (int run = 0; run <= TextShaper.MaximumEntries; run++)
        {
            shaper.Shape($"t{run}", Plain);
        }

        Assert.Equal(1, shaper.Rebuilds);
        Assert.True(shaper.CachedRuns <= TextShaper.MaximumEntries,
                    $"the cache holds {shaper.CachedRuns} runs, past its own ceiling");
    }

    /// <summary>Two faces are two caches: the same characters shape once in each.</summary>
    [Fact]
    public void TheSameCharactersInTwoFacesAreTwoShapings()
    {
        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        shaper.Shape(Arrow, Plain);
        shaper.Shape(Arrow, Plain with { SizeInPoints = 24f });

        Assert.Equal(2, shaper.Shapings);
        Assert.Equal(2, shaper.CachedRuns);
    }

    /// <summary>A destination too short to hold the worst case is refused rather than truncated.</summary>
    [Fact]
    public void ADestinationTooShortIsRefused()
    {
        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);

        Assert.Throws<ArgumentException>(() =>
        {
            ShapedCluster[] tooShort = new ShapedCluster[1];
            shaper.Draw("abc", Plain, FontWeight.Normal, FontStyle.Normal, 0f, -1, tooShort);
        });
    }

    /// <summary>
    /// The whole path, end to end: the glyph the shaper chose reaches the atlas, rasterises, and is
    /// a different picture from the character it replaced.
    ///
    /// <para>This is the claim the design made about the atlas — that its key would have to be the
    /// run rather than the character — answered by finding it already true. The key was a glyph
    /// index and not a codepoint from the day it was written, so a ligature caches like a letter and
    /// nothing about the atlas changes.</para>
    /// </summary>
    [Fact]
    public void TheSubstitutedGlyphCachesAndDrawsAsItsOwnPicture()
    {
        SkipWithout(Programming);

        using GlyphRasteriser rasteriser = new();
        using TextShaper shaper = new(rasteriser);
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device, Programming, rasteriser: rasteriser);

        ShapedRun run = shaper.Shape(Arrow, Programming);

        GlyphPlacement ligature = atlas.Cache(Key(run.Clusters[0].Glyph));
        GlyphPlacement plain = atlas.Cache(Key(Mapped(rasteriser, Programming, '=')));

        Assert.False(ligature.IsEmpty, "the left half of the arrow rasterised to nothing");
        Assert.NotEqual(plain, ligature);
        Assert.Equal(2, atlas.CachedGlyphs);
        Assert.Equal(2, atlas.Rasterisations);

        static GlyphKey Key(ushort glyph) => new(Programming.Family, FontWeight.Normal,
                                                 FontStyle.Normal, Programming.SizeInPixels, glyph, 0);
    }

    /// <summary>What the face's character map alone says a character is, with no neighbours.</summary>
    private static ushort Mapped(GlyphRasteriser rasteriser, FontSettings font, char character) =>
        rasteriser.GlyphIndex(font.Family, FontWeight.Normal, FontStyle.Normal, character);

    /// <summary>
    /// Skips where a font is not installed. The Cascadias ship with Windows Terminal rather than
    /// with Windows, and a test that quietly passed on a machine without one would be asserting
    /// nothing at all.
    /// </summary>
    private static void SkipWithout(FontSettings font)
    {
        using GlyphRasteriser rasteriser = new();
        bool installed;

        try
        {
            rasteriser.Measure(font);
            installed = true;
        }
        catch (InvalidOperationException)
        {
            installed = false;
        }

        Assert.SkipUnless(installed,
            $"'{font.Family}' is not installed, so there is no ligature here to form");
    }
}
