using System.Runtime.InteropServices;
using Quickshell.Render;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// The atlas against a real device and real DirectWrite. Nothing here is mocked: a cache that works
/// against a fake rasteriser and produces the wrong pixels on a machine is the failure the line
/// asks about.
/// </summary>
public sealed class GlyphAtlasTests
{
    /// <summary>
    /// Big enough that a page holds only a handful of glyphs, so eviction can be reached in a test
    /// that finishes. 500 points at 96 DPI is about 666 pixels to the em.
    /// </summary>
    private static readonly FontSettings Huge = new("Consolas", 500f, 96f);

    [Fact]
    public void TheSameGlyphIsRasterisedOnceAndSampledThereafter()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device);

        GlyphPlacement first = atlas.Cache('A');
        int afterFirst = atlas.Rasterisations;

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(first, atlas.Cache('A'));
        }

        Assert.False(first.IsEmpty);
        Assert.Equal(afterFirst, atlas.Rasterisations);
        Assert.Equal(1, atlas.CachedGlyphs);
        Assert.Equal(1, atlas.PageCount);
    }

    /// <summary>
    /// The falsification the design names: one character drawn at two positions on a line must not
    /// differ visibly in weight. It would if the subpixel offset were dropped from the key, because
    /// then the first fraction to arrive is the one every later column gets.
    /// </summary>
    [Fact]
    public void TwoSubpixelPositionsAreTwoEntries()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device);

        GlyphPlacement onPixel = atlas.Cache('m', penX: 4f);
        GlyphPlacement halfway = atlas.Cache('m', penX: 4.5f);

        Assert.Equal(2, atlas.CachedGlyphs);
        Assert.NotEqual(onPixel, halfway);

        // And a third column landing on the same fraction is the first entry again, which is what
        // keeps four positions from becoming one per column.
        Assert.Equal(onPixel, atlas.Cache('m', penX: 37f));
        Assert.Equal(2, atlas.CachedGlyphs);
    }

    [Fact]
    public void TheQuantiserFoldsAWholePixelIntoTheNextColumnsZero()
    {
        Assert.Equal(0, GlyphKey.Quantise(3f));
        Assert.Equal(1, GlyphKey.Quantise(3.25f));
        Assert.Equal(2, GlyphKey.Quantise(3.5f));
        Assert.Equal(3, GlyphKey.Quantise(3.75f));
        Assert.Equal(0, GlyphKey.Quantise(3.99f));
    }

    [Fact]
    public void AGlyphWithNoShapeIsCachedAsNothingToDraw()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device);

        GlyphPlacement space = atlas.Cache(' ');

        Assert.True(space.IsEmpty);
        Assert.Equal(0, atlas.PageCount);
        Assert.Equal(space, atlas.Cache(' '));
        Assert.Equal(1, atlas.Rasterisations);
    }

    [Fact]
    public void AFontChangeRebuildsRatherThanEvicts()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device);

        atlas.Cache('A');
        atlas.Cache('B');
        Assert.Equal(2, atlas.CachedGlyphs);

        atlas.UseFont(FontSettings.Default with { SizeInPoints = 22f });

        Assert.Equal(1, atlas.Rebuilds);
        Assert.Equal(0, atlas.Evictions);
        Assert.Equal(0, atlas.CachedGlyphs);
        Assert.Equal(1, atlas.PageCount);

        // The page came back to its packer rather than being freed, so the same character lands at
        // the origin again - at its new size.
        GlyphPlacement again = atlas.Cache('A');
        Assert.Equal(0, again.X);
        Assert.Equal(0, again.Y);
    }

    /// <summary>Only the pixel size is in the key, so points and DPI that agree on it agree.</summary>
    [Fact]
    public void ADpiChangeThatLandsOnTheSamePixelSizeIsNotADifferentFont()
    {
        Assert.Equal(new FontSettings("Consolas", 12f, 192f).SizeInPixels,
                     new FontSettings("Consolas", 24f, 96f).SizeInPixels, 3);
    }

    /// <summary>
    /// The atlas is GPU state, so it must hold nothing that outlived the device: after a loss it is
    /// empty and reconstructs from nothing but the font.
    /// </summary>
    [Fact]
    public void DeviceLossDiscardsTheAtlasAndTheFontIsEnoughToRebuildIt()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device);

        GlyphPlacement before = atlas.Cache('W');
        Assert.Equal(1, atlas.PageCount);

        device.Recover();

        Assert.Equal(0, atlas.PageCount);
        Assert.Equal(0, atlas.CachedGlyphs);
        Assert.Equal(FontSettings.Default, atlas.Font);

        GlyphPlacement after = atlas.Cache('W');

        Assert.Equal(before, after);
        Assert.Equal(1, atlas.PageCount);
    }

    /// <summary>
    /// Eviction takes a whole page and not a glyph, so what leaves is every entry that page held and
    /// nothing else. One page and a font big enough that a page holds a handful is how that is
    /// reached without rasterising thousands of glyphs.
    /// </summary>
    [Fact]
    public void AFullAtlasEvictsAWholePage()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device, Huge, maximumPages: 1);

        int cached = 0;

        for (int codepoint = 'A'; codepoint <= 'Z' && atlas.Evictions == 0; codepoint++)
        {
            atlas.Cache(codepoint);
            cached++;
        }

        Assert.True(atlas.Evictions >= 1,
                    $"{cached} glyphs at {Huge.SizeInPixels:F0} pixels did not fill one {GlyphAtlas.PageSize}-square page");
        Assert.Equal(1, atlas.PageCount);

        // The page that went took its entries with it, so the cache is smaller than what was asked
        // for - and the atlas still answers.
        Assert.True(atlas.CachedGlyphs < cached, "eviction dropped no entries, so it freed nothing");
        Assert.False(atlas.Cache('A').IsEmpty);
    }

    /// <summary>
    /// The bounds being non-empty is not evidence that any pixels arrived: a coverage call that
    /// failed and an upload the box confined to nothing both leave a rectangle of zeroes. So the
    /// page is read back off the GPU and the ink is counted where the placement says it is.
    /// </summary>
    [Fact]
    public void ThePageReallyHoldsTheGlyphsCoverage()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device, new FontSettings("Consolas", 24f, 96f));

        GlyphPlacement placement = atlas.Cache('#');
        Assert.False(placement.IsEmpty);

        using ID3D11Resource resource = atlas.PageView(placement.Page).Resource;
        using ID3D11Texture2D page = resource.QueryInterface<ID3D11Texture2D>();
        using ID3D11Texture2D readback = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = GlyphAtlas.PageSize,
            Height = GlyphAtlas.PageSize,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        device.Context.CopyResource(readback, page);

        MappedSubresource mapped = device.Context.Map(readback, 0, MapMode.Read);
        int darkest = 0;
        int inked = 0;

        try
        {
            for (int row = 0; row < placement.Height; row++)
            {
                for (int column = 0; column < placement.Width; column++)
                {
                    int offset = ((placement.Y + row) * (int)mapped.RowPitch) + placement.X + column;
                    int coverage = Marshal.ReadByte(mapped.DataPointer, offset);

                    darkest = Math.Max(darkest, coverage);
                    inked += coverage > 0 ? 1 : 0;
                }
            }
        }
        finally
        {
            device.Context.Unmap(readback, 0);
        }

        Assert.True(inked > 0, "the placement rectangle on the page is entirely blank");
        Assert.True(darkest > 128, $"the darkest pixel of a '#' is {darkest}, which is not a stroke");
    }

    [Fact]
    public void WeightAndSlantAreSeparateEntries()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device);

        atlas.Cache('a');
        atlas.Cache('a', FontWeight.Bold);
        atlas.Cache('a', FontWeight.Normal, FontStyle.Italic);

        Assert.Equal(3, atlas.CachedGlyphs);
    }

    [Fact]
    public void AFamilyNobodyHasIsRefusedByName()
    {
        using GraphicsDevice device = GraphicsDevice.Open();
        using GlyphAtlas atlas = GlyphAtlas.For(device, new FontSettings("No Such Face Exists", 11f, 96f));

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => atlas.Cache('A'));

        Assert.Contains("No Such Face Exists", refused.Message, StringComparison.Ordinal);
    }
}
