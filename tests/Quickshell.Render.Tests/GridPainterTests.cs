using System.Text;
using Quickshell.Render;
using Quickshell.Terminal;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// A real screen turned into the instances a draw call takes.
///
/// <para>QS116's first piece: this work existed only inside the golden suite's `Painter`, which is
/// why the client could open a window and open a session and not both. What is asserted here is that
/// what a host printed reaches the instance array — the same array <c>CellRenderer.Draw</c> is
/// handed.</para>
///
/// <para>The instances are compared whole rather than through accessors they do not have. A
/// <c>CellInstance</c> is packed for the GPU, and building the one this ought to produce and
/// comparing is a stronger claim than reading a field back: it asserts the colour, the flags, the
/// span and the glyph together.</para>
/// </summary>
public sealed class GridPainterTests
{
    /// <summary>A cell box. Only its width matters here, as the advance a glyph is fitted to.</summary>
    private static readonly CellMetrics Box = new(8, 16, 12);

    /// <summary>
    /// What the host printed is in the cells, in the colours the palette resolves.
    /// </summary>
    [Fact]
    public void WhatTheHostPrintedReachesTheInstances()
    {
        using Harness harness = new();

        Emulator emulator = new(20, 4);

        // Red, then default, so a resolved indexed colour and a default one are in the same frame.
        emulator.Feed(Encoding.UTF8.GetBytes("\u001b[31mAB\u001b[0mcd"));

        CellInstance[] cells = new CellInstance[20 * 4];

        GridPainter painter = new(harness.Atlas, emulator.Palette);

        painter.Paint(emulator.Buffer, cells, cursorRow: -1, cursorColumn: -1, CursorShape.None, Box);

        Assert.Equal(20 * 4, painter.Painted);

        Rgb red = emulator.Palette[1];
        Rgb ground = emulator.Palette.Resolve(Colour.Default, background: true);

        // The A, in red, with the glyph the atlas holds for it. Built the way the painter should
        // have built it and compared whole.
        Assert.Equal(
            CellInstance.For(harness.Atlas.Cache('A', maximumAdvance: Box.Width), red, ground),
            cells[0]);

        // And the c is not red, so the reset was honoured rather than the whole row taking the
        // first pen it saw.
        Assert.NotEqual(
            CellInstance.For(harness.Atlas.Cache('c', maximumAdvance: Box.Width), red, ground),
            cells[2]);

        Assert.Equal(
            CellInstance.For(harness.Atlas.Cache('c', maximumAdvance: Box.Width),
                             emulator.Palette.Resolve(Colour.Default), ground),
            cells[2]);
    }

    /// <summary>
    /// The cursor is on exactly one cell, and it is the one the buffer says.
    ///
    /// <para>Measured by painting the same screen twice — once with a cursor and once without — and
    /// counting what differs. A cursor on every cell and a cursor on none both look like a rendering
    /// fault rather than a painting one, and this tells them apart.</para>
    /// </summary>
    [Fact]
    public void TheCursorIsOnOneCellAndItIsTheBuffersOwn()
    {
        using Harness harness = new();

        Emulator emulator = new(20, 4);

        emulator.Feed(Encoding.UTF8.GetBytes("hello"));

        GridPainter painter = new(harness.Atlas, emulator.Palette);

        CellInstance[] without = new CellInstance[20 * 4];
        CellInstance[] with = new CellInstance[20 * 4];

        painter.Paint(emulator.Buffer, without, -1, -1, CursorShape.None, Box);
        painter.Paint(emulator.Buffer, with, emulator.Buffer.CursorRow, emulator.Buffer.CursorColumn,
                      CursorShape.Block, Box);

        int at = (emulator.Buffer.CursorRow * 20) + emulator.Buffer.CursorColumn;
        int differing = 0;

        for (int cell = 0; cell < without.Length; cell++)
        {
            if (!without[cell].Equals(with[cell]))
            {
                differing++;

                Assert.Equal(at, cell);
            }
        }

        Assert.Equal(1, differing);
    }

    /// <summary>
    /// A viewport scrolled back paints the history, and the cursor is not drawn in it.
    ///
    /// <para><b>The cursor is the half that would ship broken.</b> It is a row number on the live
    /// screen, and a painter that kept using it while showing lines from an hour ago would blink a
    /// caret in the middle of somebody's scrollback — on a line the user is reading, at a column
    /// nothing is being typed into.</para>
    /// </summary>
    [Fact]
    public void AViewportScrolledBackPaintsTheHistoryAndNoCursor()
    {
        using Harness harness = new();

        Emulator emulator = new(20, 4);

        // Ten lines through a four-row screen, so six of them are history.
        for (int line = 0; line < 10; line++)
        {
            emulator.Feed(Encoding.UTF8.GetBytes($"line{line}\r\n"));
        }

        GridPainter painter = new(harness.Atlas, emulator.Palette);
        Viewport viewport = new();

        CellInstance[] bottom = new CellInstance[20 * 4];
        CellInstance[] back = new CellInstance[20 * 4];

        painter.Paint(emulator.Buffer, bottom, emulator.Buffer.CursorRow,
                      emulator.Buffer.CursorColumn, CursorShape.Block, Box, null, viewport);

        Assert.True(viewport.ScrollBy(emulator.Buffer, -4), "there was no history to scroll into");

        painter.Paint(emulator.Buffer, back, emulator.Buffer.CursorRow,
                      emulator.Buffer.CursorColumn, CursorShape.Block, Box, null, viewport);

        Assert.NotEqual(bottom, back);

        // The top row now holds a line that was four lines above the screen.
        long top = viewport.Top(emulator.Buffer);
        int retained = (int)(top - (emulator.Buffer.TopLine - emulator.Buffer.ScrollbackLines));

        Assert.Equal(Instance(harness, emulator, emulator.Buffer.Line(retained)[0]), back[0]);

        // And no cell differs from the same screen painted with no cursor at all, because the
        // cursor's line is not one of the lines on screen.
        CellInstance[] plain = new CellInstance[20 * 4];

        painter.Paint(emulator.Buffer, plain, -1, -1, CursorShape.None, Box, null, viewport);

        Assert.Equal(plain, back);
    }

    /// <summary>The instance the painter should build for one cell of a line.</summary>
    private static CellInstance Instance(Harness harness, Emulator emulator, Cell cell) =>
        CellInstance.For(
            cell.Width == 0 || cell.Codepoint == ' '
                ? GlyphPlacement.Empty
                : harness.Atlas.Cache(cell.Codepoint, maximumAdvance: Box.Width * Math.Max(1, cell.Width)),
            emulator.Palette.Resolve(cell.Foreground),
            emulator.Palette.Resolve(cell.Background, background: true),
            cell.Flags, Math.Max(1, cell.Width), cell.Underline, CursorShape.None);

    /// <summary>
    /// Painting a frame allocates nothing, which is Block C's criterion where a frame is built.
    /// </summary>
    [Fact]
    public void PaintingAFrameAllocatesNothing()
    {
        using Harness harness = new();

        Emulator emulator = new(80, 25);

        emulator.Feed(Encoding.UTF8.GetBytes(new string('x', 80 * 20)));

        CellInstance[] cells = new CellInstance[80 * 25];

        GridPainter painter = new(harness.Atlas, emulator.Palette);

        // Once to warm the atlas: caching a glyph the first time is real work and is not per frame.
        painter.Paint(emulator.Buffer, cells, -1, -1, CursorShape.None, Box);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int frame = 0; frame < 10; frame++)
        {
            painter.Paint(emulator.Buffer, cells, -1, -1, CursorShape.None, Box);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"ten frames allocated {allocated} bytes");
    }

    /// <summary>A real device and a real atlas, because a glyph cache without one is a dictionary.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly GraphicsDevice _device = GraphicsDevice.Open();

        public Harness() =>
            Atlas = GlyphAtlas.For(_device, new FontSettings("Consolas", 16f, 96f));

        public GlyphAtlas Atlas { get; }

        public void Dispose()
        {
            Atlas.Dispose();
            _device.Dispose();
        }
    }
}
