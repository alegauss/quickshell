using SharpGen.Runtime;
using Vortice.DirectWrite;

namespace Quickshell.Render;

/// <summary>
/// One cluster of a shaped run: the characters that went in and the single glyph that came out.
///
/// <para>A cluster of one is the ordinary case — and, measured, it is also the ligature case. See
/// <see cref="TextShaper"/>: a monospaced face ligates by substituting a different glyph into each
/// cell, so <c>=&gt;</c> is two clusters of one whose glyphs are not the two the character map gives.
/// A cluster of two or more is the other kind of ligature, one glyph over several cells, which a
/// proportional face produces and a terminal's face does not.</para>
/// </summary>
/// <param name="First">The index within the run of the first character this cluster covers.</param>
/// <param name="Length">How many characters it covers, which for a terminal is how many cells.</param>
/// <param name="Glyph">The glyph index in the face the run was shaped against.</param>
/// <param name="Advance">How far the pen moves, in pixels, after drawing it.</param>
public readonly record struct ShapedCluster(int First, int Length, ushort Glyph, float Advance)
{
    /// <summary>Whether this glyph spans more than one character, and so more than one cell.</summary>
    public bool IsLigature => Length > 1;
}

/// <summary>
/// One run of text as the font wants to draw it, cached against the run's own characters.
///
/// <para>The atlas is keyed on a glyph index rather than a codepoint, so a ligature needs nothing
/// from it: the shaped glyph caches exactly like a letter. What has no home there is the step
/// before — which characters became which glyph — and that is what this holds.</para>
/// </summary>
public sealed class ShapedRun
{
    private readonly ShapedCluster[] _clusters;

    internal ShapedRun(string text, ShapedCluster[] clusters)
    {
        Text = text;
        _clusters = clusters;

        foreach (ShapedCluster cluster in clusters)
        {
            if (cluster.IsLigature)
            {
                HasLigature = true;
                break;
            }
        }
    }

    /// <summary>The characters this was shaped from.</summary>
    public string Text { get; }

    /// <summary>The clusters, in order, covering every character of <see cref="Text"/> exactly once.</summary>
    public ReadOnlySpan<ShapedCluster> Clusters => _clusters;

    /// <summary>
    /// Whether any cluster covers more than one character.
    ///
    /// <para><b>This is not "did a ligature form".</b> On every monospaced face measured here it is
    /// false even for <c>=&gt;</c>, because the ligature arrived as one substituted glyph per cell
    /// rather than as one glyph over two. Whether a run ligated is answered by comparing a cluster's
    /// glyph against the face's character map, which is what the tests do.</para>
    /// </summary>
    public bool HasLigature { get; }

    /// <summary>How many clusters there are. One per character where nothing ligated.</summary>
    public int Count => _clusters.Length;
}

/// <summary>
/// DirectWrite's shaper, kept to the one question a cell grid can act on: which characters of this
/// run became one glyph.
///
/// <para><b>A grid and a ligature were expected to disagree by construction, and measured they do
/// not.</b> The design this was written against assumed <c>=&gt;</c> is one glyph spanning two cells,
/// so the renderer would have to draw a glyph belonging to no single cell. Cascadia Code does not do
/// that, and neither can any face a terminal would be set in: it ligates by <em>substitution</em>,
/// putting a different glyph in each cell — <c>=&gt;</c> shapes to two glyphs where the character
/// map gives two others, and <c>&lt;==&gt;</c> to four. That is how a monospaced font stays
/// monospaced. The same font's no-ligature twin, Cascadia Mono, leaves both characters alone, which
/// is what says the substitution is the ligature and not some other effect.</para>
///
/// <para>So the grid costs nothing here and the atlas needs nothing new: a substituted glyph is a
/// glyph index, and <see cref="GlyphAtlas"/> was already keyed on one of those rather than on a
/// codepoint. What was missing is only the step before — which glyph a character becomes in the
/// company of its neighbours — and that is all this class supplies. The multi-cell case is still
/// handled, because a face could do it, but it is refused past
/// <see cref="CellInstance.MaximumSpan"/> rather than drawn clipped.</para>
///
/// <para><b>Shaping is per run and a run is an attribute span</b>, so a line where every cell
/// carries a different colour degenerates into one shaping call per cell. That is not a hypothetical
/// line, it is a syntax-highlighted source file, and it is the case the cache has to be measured
/// against rather than prose.</para>
///
/// <para><b>The cursor is not the font's business.</b> The grid owns it, the selection and the copy;
/// a ligature changes what is drawn and nothing else. <see cref="Draw"/> takes the caret and puts
/// the cell under it back to the character the model says is there — which, given the substitution
/// above, is the difference between seeing the character you are on and seeing half an arrow.</para>
///
/// <para>This holds no GPU state, so a device loss costs it nothing.</para>
/// </summary>
public sealed class TextShaper : IDisposable
{
    /// <summary>
    /// ISO 15924's number for the Latin script, which is how the script DirectWrite calls Latin is
    /// found rather than assumed. The number DirectWrite uses internally is not documented and is
    /// not the same one; asking it to describe each script and matching on the published code is the
    /// difference between a constant that is right and one that happens to be right here.
    /// </summary>
    private const int LatinIsoScript = 215;

    /// <summary>How many scripts to ask about before giving up. DirectWrite defines well under this.</summary>
    private const int ScriptCeiling = 256;

    /// <summary>
    /// Shaped runs held before the cache is emptied.
    ///
    /// <para>A run is an attribute span, so in highlighted source the key is a token and the set of
    /// distinct tokens has no bound. Four thousand is several screens of them; past it the whole
    /// cache is dropped rather than swept, for the same reason <see cref="GlyphAtlas"/> rebuilds on a
    /// font change — the sweep costs more bookkeeping than the memory is worth.</para>
    /// </summary>
    public const int MaximumEntries = 4096;

    /// <summary>The locale shaping is done in. Programming ligatures are locale-independent.</summary>
    private const string Locale = "en-us";

    private readonly Dictionary<Face, Runs> _byFace = [];
    private readonly GlyphRasteriser _rasteriser;
    private readonly IDWriteTextAnalyzer? _analyzer;
    private readonly ScriptAnalysis _latin;

    /// <summary>Opens a shaper over the faces and factory a rasteriser already holds.</summary>
    /// <param name="rasteriser">
    /// The rasteriser whose faces are shaped against. Not owned: shaping and rasterising must agree
    /// about which face a family resolves to, and two DirectWrite face caches is two chances to
    /// disagree.
    /// </param>
    public TextShaper(GlyphRasteriser rasteriser)
    {
        ArgumentNullException.ThrowIfNull(rasteriser);

        _rasteriser = rasteriser;
        _analyzer = rasteriser.CreateAnalyzer();

        if (LatinScript(_analyzer) is { } latin)
        {
            _latin = latin;
            return;
        }

        // Nothing described itself as Latin, so this DirectWrite cannot be asked to shape a run in
        // the script every programming ligature lives in. Shaping is then off rather than done
        // against a guessed script number, which would silently produce the wrong glyphs.
        _analyzer.Dispose();
        _analyzer = null;
    }

    /// <summary>The face a run's cache lives under. Everything that changes which glyphs come out.</summary>
    private readonly record struct Face(string Family, FontWeight Weight, FontStyle Slant, float SizeInPixels);

    /// <summary>
    /// The runs shaped against one face, with the span lookup beside the dictionary that answers it.
    /// A hit costs no string: the run arrives as characters and is only materialised on a miss.
    /// </summary>
    private sealed class Runs
    {
        public Dictionary<string, ShapedRun> Map { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ShapedRun>.AlternateLookup<ReadOnlySpan<char>> Lookup { get; }

        public Runs() => Lookup = Map.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// Whether this shaper can shape at all. False where DirectWrite would not name its Latin
    /// script, in which case <see cref="Shape"/> answers one cluster per character and
    /// <see cref="Draw"/> is exactly the unshaped grid.
    /// </summary>
    public bool CanShape => _analyzer is not null;

    /// <summary>
    /// How many runs have actually been shaped. The cache exists to keep this near the number of
    /// distinct runs on screen rather than near the number drawn, so a test that asserts caching
    /// asserts on this, and the highlighted-source case is measured by it.
    /// </summary>
    public int Shapings { get; private set; }

    /// <summary>How many times the cache has been emptied for reaching <see cref="MaximumEntries"/>.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>How many runs the cache is currently holding, across every face.</summary>
    public int CachedRuns
    {
        get
        {
            int total = 0;

            foreach (Runs runs in _byFace.Values)
            {
                total += runs.Map.Count;
            }

            return total;
        }
    }

    /// <summary>
    /// Shapes one run, or answers the shaping already held for these exact characters in this exact
    /// face.
    ///
    /// <para>The run must be a single attribute span — that is what makes it a run — and it must be
    /// text rather than a screen: a caller that hands this a whole line hands it a cache key nothing
    /// will ever ask for twice.</para>
    /// </summary>
    /// <param name="text">The run's characters.</param>
    /// <param name="font">The font it is set in; only the family and pixel size are read here.</param>
    /// <param name="weight">The weight the face is matched at.</param>
    /// <param name="slant">Upright, italic or oblique.</param>
    public ShapedRun Shape(ReadOnlySpan<char> text, FontSettings font,
                           FontWeight weight = FontWeight.Normal, FontStyle slant = FontStyle.Normal)
    {
        Face face = new(font.Family, weight, slant, font.SizeInPixels);

        if (!_byFace.TryGetValue(face, out Runs? runs))
        {
            runs = new Runs();
            _byFace[face] = runs;
        }

        if (runs.Lookup.TryGetValue(text, out ShapedRun? hit))
        {
            return hit;
        }

        if (CachedRuns >= MaximumEntries)
        {
            _byFace.Clear();
            Rebuilds++;

            runs = new Runs();
            _byFace[face] = runs;
        }

        string run = text.ToString();
        ShapedRun shaped = new(run, Clusters(run, font, weight, slant));

        runs.Map[run] = shaped;
        Shapings++;

        return shaped;
    }

    /// <summary>
    /// The clusters to draw for a run, with the grid's three refusals already applied. A refused
    /// cluster comes back as its characters drawn from the face's own character map, which is
    /// exactly what this renderer drew before shaping existed.
    ///
    /// <para><b>The cluster under the caret is put back.</b> This is the refusal that matters most
    /// and the one that is easy to get wrong: a monospaced face ligates by substituting a piece of
    /// the shape into each cell, so with the cursor on the <c>=</c> of <c>=&gt;</c> the user would
    /// otherwise be looking at the left half of an arrow and unable to tell which character they are
    /// on. It is not enough to break apart the multi-cell clusters — the caret's cell must go back to
    /// the character the model says is there, whatever the font substituted for it.</para>
    ///
    /// <para><b>A cluster wider than the instance format can address is broken apart.</b>
    /// <see cref="CellInstance.MaximumSpan"/> is what a cell can say about itself, and a glyph over
    /// more cells than that would be drawn clipped to the first few rather than not at all. No
    /// monospaced face measured here produces one; the refusal is here because a face that did would
    /// otherwise be drawn wrong rather than drawn plainly.</para>
    ///
    /// <para><b>A cluster whose advance is not its cells' is broken apart.</b> The grid is what
    /// decides columns; a face whose ligature is narrower or wider than the characters it replaced
    /// would draw correctly and leave every column after it disagreeing with the host about where
    /// the cursor is.</para>
    /// </summary>
    /// <param name="text">The run's characters.</param>
    /// <param name="font">The font it is set in.</param>
    /// <param name="weight">The weight the face is matched at.</param>
    /// <param name="slant">Upright, italic or oblique.</param>
    /// <param name="cellAdvance">
    /// One cell's width in pixels, which every cluster's advance is checked against. Zero skips that
    /// check, which is for a caller measuring the font rather than drawing with it.
    /// </param>
    /// <param name="caret">The character the cursor is on, or a negative number where it is elsewhere.</param>
    /// <param name="destination">Where the clusters are written; at least as long as <paramref name="text"/>.</param>
    /// <returns>How many clusters were written.</returns>
    public int Draw(ReadOnlySpan<char> text, FontSettings font, FontWeight weight, FontStyle slant,
                    float cellAdvance, int caret, Span<ShapedCluster> destination)
    {
        if (destination.Length < text.Length)
        {
            throw new ArgumentException(
                $"a run of {text.Length} characters can produce that many clusters and this holds "
                + $"{destination.Length}",
                nameof(destination));
        }

        if (!font.Ligatures || text.IsEmpty)
        {
            return Unshaped(text, font, weight, slant, 0, text.Length, destination);
        }

        ShapedRun shaped = Shape(text, font, weight, slant);
        int written = 0;

        foreach (ShapedCluster cluster in shaped.Clusters)
        {
            if (Keeps(cluster, cellAdvance, caret))
            {
                destination[written++] = cluster;
                continue;
            }

            written += Unshaped(text, font, weight, slant, cluster.First, cluster.Length,
                                destination[written..]);
        }

        return written;
    }

    /// <summary>Whether a cluster survives the grid's three refusals and may be drawn as it shaped.</summary>
    private static bool Keeps(ShapedCluster cluster, float cellAdvance, int caret)
    {
        // Checked before the length, and that ordering is the whole of the cursor rule: a cluster of
        // one character can still be a substituted piece of a ligature, so the caret's own cell has
        // to be put back whether or not anything around it merged.
        if (caret >= cluster.First && caret < cluster.First + cluster.Length)
        {
            return false;
        }

        if (!cluster.IsLigature)
        {
            return true;
        }

        if (cluster.Length > CellInstance.MaximumSpan)
        {
            return false;
        }

        if (cellAdvance <= 0f)
        {
            return true;
        }

        // A cell is the font's advance rounded up, so a ligature that is exactly its characters'
        // width is up to one pixel per cell narrower than the cells it covers, and never wider.
        // Stating the bound that way rather than as a symmetric tolerance is what keeps it a claim
        // about the font instead of a number chosen to make this font pass.
        float cells = cellAdvance * cluster.Length;

        return cluster.Advance <= cells + 0.5f && cluster.Advance > cells - cluster.Length;
    }

    /// <summary>One cluster per character, straight from the face's character map.</summary>
    private int Unshaped(ReadOnlySpan<char> text, FontSettings font, FontWeight weight, FontStyle slant,
                         int first, int length, Span<ShapedCluster> destination)
    {
        int written = 0;

        for (int index = first; index < first + length; index++)
        {
            int codepoint = text[index];
            int characters = 1;

            if (char.IsHighSurrogate(text[index]) && index + 1 < first + length)
            {
                codepoint = char.ConvertToUtf32(text[index], text[index + 1]);
                characters = 2;
                index++;
            }

            ushort glyph = _rasteriser.GlyphIndex(font.Family, weight, slant, codepoint);

            destination[written++] = new ShapedCluster(index - characters + 1, characters, glyph, 0f);
        }

        return written;
    }

    /// <summary>Releases the analyzer. The rasteriser is not owned and is not touched.</summary>
    public void Dispose() => _analyzer?.Dispose();

    /// <summary>
    /// Runs DirectWrite's shaper over the whole run and folds its two parallel answers — a map from
    /// characters to glyphs, and the glyphs' advances — into clusters.
    ///
    /// <para>A cluster that produced more than one glyph is not one this grid can draw: a mark and
    /// its base are two glyphs in one cell, and placing them is a different piece of work from
    /// placing a ligature. Those come back as one cluster per character, so the caller draws them
    /// the way it drew everything before this class existed.</para>
    /// </summary>
    private ShapedCluster[] Clusters(string run, FontSettings font, FontWeight weight, FontStyle slant)
    {
        if (_analyzer is null)
        {
            return PerCharacter(run, font, weight, slant);
        }

        IDWriteFontFace face = _rasteriser.FaceFor(font.Family, weight, slant);

        // DirectWrite's own recommendation for how many glyphs a run of this length can produce.
        int maximum = (3 * run.Length / 2) + 16;
        ushort[] clusterMap = new ushort[run.Length];
        ushort[] glyphs = new ushort[maximum];
        float[] advances = new float[maximum];

        uint produced = Glyphs(run, face, font.SizeInPixels, maximum, clusterMap, glyphs, advances);

        if (produced == 0)
        {
            return PerCharacter(run, font, weight, slant);
        }

        List<ShapedCluster> clusters = new(run.Length);
        int character = 0;

        while (character < run.Length)
        {
            int last = character;

            while (last + 1 < run.Length && clusterMap[last + 1] == clusterMap[character])
            {
                last++;
            }

            int firstGlyph = clusterMap[character];
            int nextGlyph = last + 1 < run.Length ? clusterMap[last + 1] : (int)produced;
            int length = last - character + 1;

            if (nextGlyph - firstGlyph != 1)
            {
                // More than one glyph for these characters, or none: not a ligature, and not
                // something a one-glyph-per-cluster grid can place.
                for (int index = character; index <= last; index++)
                {
                    clusters.Add(new ShapedCluster(
                        index, 1, _rasteriser.GlyphIndex(font.Family, weight, slant, run[index]), 0f));
                }
            }
            else
            {
                clusters.Add(new ShapedCluster(character, length, glyphs[firstGlyph], advances[firstGlyph]));
            }

            character = last + 1;
        }

        return [.. clusters];
    }

    /// <summary>
    /// The two DirectWrite calls that turn characters into glyphs.
    ///
    /// <para><b>No feature list is passed, and that is a measurement rather than an omission.</b>
    /// The obvious thing to do is name the features a terminal wants — standard ligatures for the
    /// typographer's kind, contextual alternates for the programmer's — and it was built that way
    /// first. Asked for explicitly and left to DirectWrite's default, Cascadia Code produces the
    /// same glyph for every operator tried. The explicit form is unsafe pointer marshalling for an
    /// identical picture, so it is not here. Discretionary ligatures stay off, which for a terminal
    /// is the right default and not an oversight.</para>
    /// </summary>
    /// <returns>How many glyphs came out; zero where shaping produced nothing usable.</returns>
    private uint Glyphs(string run, IDWriteFontFace face, float sizeInPixels, int maximum,
                        ushort[] clusterMap, ushort[] glyphs, float[] advances)
    {
        ShapingTextProperties[] textProperties = new ShapingTextProperties[run.Length];
        ShapingGlyphProperties[] glyphProperties = new ShapingGlyphProperties[maximum];
        GlyphOffset[] offsets = new GlyphOffset[maximum];

        _analyzer!.GetGlyphs(run, (uint)run.Length, face, false, false, _latin, Locale, null, null,
                             null, 0, (uint)maximum, clusterMap, textProperties, glyphs,
                             glyphProperties, out uint produced);

        if (produced == 0)
        {
            return 0;
        }

        _analyzer.GetGlyphPlacements(run, clusterMap, textProperties, (uint)run.Length, glyphs,
                                     glyphProperties, produced, face, sizeInPixels, false, false,
                                     _latin, Locale, null, null, 0, advances, offsets);

        return produced;
    }

    /// <summary>The run as the character map alone would draw it: one glyph per character.</summary>
    private ShapedCluster[] PerCharacter(string run, FontSettings font, FontWeight weight, FontStyle slant)
    {
        ShapedCluster[] clusters = new ShapedCluster[run.Length];

        for (int index = 0; index < run.Length; index++)
        {
            clusters[index] = new ShapedCluster(
                index, 1, _rasteriser.GlyphIndex(font.Family, weight, slant, run[index]), 0f);
        }

        return clusters;
    }

    /// <summary>
    /// The script number this DirectWrite calls Latin, found by asking it to describe each script
    /// in turn and matching on the ISO code it publishes.
    ///
    /// <para>The alternative is a literal, and the number is not documented: it would be a constant
    /// that is right on the machine it was read off and unfalsifiable everywhere else. This costs
    /// one scan at startup and is correct by construction.</para>
    /// </summary>
    private static ScriptAnalysis? LatinScript(IDWriteTextAnalyzer analyzer)
    {
        using IDWriteTextAnalyzer1? described = analyzer.QueryInterfaceOrNull<IDWriteTextAnalyzer1>();

        if (described is null)
        {
            return null;
        }

        for (ushort script = 0; script < ScriptCeiling; script++)
        {
            ScriptAnalysis candidate = new() { Script = script, Shapes = ScriptShapes.Default };

            try
            {
                if (described.GetScriptProperties(candidate).IsoScriptNumber == LatinIsoScript)
                {
                    return candidate;
                }
            }
            catch (SharpGenException)
            {
                // A number this DirectWrite does not define. Asking is how the defined ones are
                // found, so a refusal is a state and not an error.
            }
        }

        return null;
    }
}
