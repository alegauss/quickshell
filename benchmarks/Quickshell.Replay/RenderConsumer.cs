using Quickshell.Render;
using Quickshell.Terminal;

namespace Quickshell.Replay;

/// <summary>
/// The with-renderer arm: bytes all the way to a frame on the glass.
///
/// <para>Parse, decode, segment into clusters, resolve each through the atlas, fill instances, and
/// draw. <b>The gap between this and the headless arm is the figure this harness exists for</b>: it
/// is what everything above the parser costs, per megabyte of what a host actually sends.</para>
///
/// <para><b>What this is not.</b> There is no terminal buffer yet — no scrollback, no scrolling
/// regions, no modes — so the cursor logic here is the crudest thing that keeps the grid full:
/// advance, wrap, carriage return, line feed, and clear on erase-display. It is scaffolding, and
/// when the buffer lands it is the buffer that belongs here. What the number measures is the volume
/// of glyph and instance work a stream implies, which is the part that does not change.</para>
///
/// <para><b>It draws on a real device and presents nothing.</b> A present would measure the display
/// refresh rather than the renderer: a vsync-locked present caps the whole replay at sixty frames
/// a second whatever the code costs. The frame is drawn and dropped, which is what isolates the
/// work from the wait.</para>
/// </summary>
public sealed class RenderConsumer : IStreamConsumer, IDisposable
{
    /// <summary>How many bytes of stream make a frame. A rough stand-in for a coalescing window.</summary>
    private const int BytesPerFrame = 16 * 1024;

    private readonly ReplayWindow _window;
    private readonly GraphicsDevice _device;
    private readonly PresentSurface _surface;
    private readonly GlyphRasteriser _rasteriser;
    private readonly GlyphAtlas _atlas;
    private readonly CellRenderer _renderer;
    private readonly CellInstance[] _cells;
    private readonly int _columns;
    private readonly int _rows;

    private Grid _grid;
    private long _sinceFrame;
    private long _frames;

    public RenderConsumer(uint width = 1280, uint height = 720)
    {
        _window = new ReplayWindow((int)width, (int)height);
        _device = GraphicsDevice.Open(outputWindow: _window.Handle);
        _surface = PresentSurface.For(_device, _window.Handle, width, height);
        _rasteriser = new GlyphRasteriser();

        FontSettings font = new("Consolas", 11f, 96f);
        _atlas = GlyphAtlas.For(_device, font, rasteriser: _rasteriser);

        CellMetrics metrics = _rasteriser.Measure(font);
        _renderer = CellRenderer.For(_device, _atlas, metrics);

        (_columns, _rows) = metrics.GridFor(width, height);
        _cells = new CellInstance[_columns * _rows];
        _grid = new Grid(_cells, _atlas, _columns, _rows);
    }

    public string Name => "render";

    public string What => "bytes to a drawn frame - parse, decode, cluster, atlas, instances and one draw call";

    public long Result => _frames;

    /// <summary>How many frames the stream produced, which is what the per-frame cost divides by.</summary>
    public long Frames => _frames;

    public void Reset()
    {
        _grid.Reset();
        _sinceFrame = 0;
        _frames = 0;
    }

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        _grid.Feed(chunk);
        _sinceFrame += chunk.Length;

        while (_sinceFrame >= BytesPerFrame)
        {
            _sinceFrame -= BytesPerFrame;
            Draw();
        }
    }

    private void Draw()
    {
        _renderer.Draw(_surface, _cells, _columns);
        _frames++;
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _atlas.Dispose();
        _rasteriser.Dispose();
        _surface.Dispose();
        _device.Dispose();
        _window.Dispose();
    }

    /// <summary>
    /// The crudest thing that keeps a grid full: where the cursor is, what colour the text is, and
    /// nothing else. Every real terminal behaviour is the buffer's, and the buffer is a later line.
    /// </summary>
    private struct Grid : IAnsiHandler
    {
        private static readonly Rgb Ground = new(16, 18, 24);
        private static readonly Rgb Text = new(214, 219, 228);

        private readonly CellInstance[] _cells;
        private readonly GlyphAtlas _atlas;
        private readonly int _columns;
        private readonly int _rows;
        private readonly AnsiParser _parser;
        private readonly StreamDecoder _decoder;
        private readonly GraphemeSegmenter _segmenter;

        private int _column;
        private int _row;
        private Rgb _foreground;

        internal Grid(CellInstance[] cells, GlyphAtlas atlas, int columns, int rows)
        {
            _cells = cells;
            _atlas = atlas;
            _columns = columns;
            _rows = rows;
            _parser = new AnsiParser();
            _decoder = new StreamDecoder();
            _segmenter = new GraphemeSegmenter();
            _column = 0;
            _row = 0;
            _foreground = Text;

            Array.Fill(_cells, CellInstance.For(GlyphPlacement.Empty, Text, Ground));
        }

        internal void Feed(ReadOnlySpan<byte> chunk)
        {
            Grid self = this;
            _parser.Parse(chunk, ref self);
            this = self;
        }

        internal void Reset()
        {
            _parser.Reset();
            _decoder.Reset();
            _segmenter.Reset();
            _column = 0;
            _row = 0;
            _foreground = Text;

            Array.Fill(_cells, CellInstance.For(GlyphPlacement.Empty, Text, Ground));
        }

        public void Print(ReadOnlySpan<byte> text)
        {
            // The whole path a printed run takes: decode across read boundaries, segment into what
            // a cell holds, then one atlas lookup per cluster.
            foreach (string cluster in _segmenter.Feed(_decoder.Decode(text)))
            {
                Place(cluster);
            }
        }

        private void Place(string cluster)
        {
            int codepoint = char.ConvertToUtf32(cluster, 0);
            int span = CharacterWidth.OfCluster(cluster);

            if (span == 0)
            {
                return;
            }

            if (_column + span > _columns)
            {
                NewLine();
            }

            GlyphPlacement glyph = _atlas.Cache(codepoint);
            _cells[(_row * _columns) + _column] = CellInstance.For(glyph, _foreground, Ground, span: span);

            if (span == 2)
            {
                _cells[(_row * _columns) + _column + 1] =
                    CellInstance.For(GlyphPlacement.Empty, _foreground, Ground, span: 0);
            }

            _column += span;
        }

        public void Execute(byte control)
        {
            switch (control)
            {
                case 0x0D:
                    _column = 0;
                    break;

                case 0x0A:
                case 0x0B:
                case 0x0C:
                    NewLine();
                    break;

                case 0x09:
                    _column = Math.Min(_columns - 1, (_column + 8) & ~7);
                    break;

                case 0x08:
                    _column = Math.Max(0, _column - 1);
                    break;

                default:
                    break;
            }
        }

        private void NewLine()
        {
            _column = 0;
            _row++;

            if (_row < _rows)
            {
                return;
            }

            // Scrolling is the buffer's job and the buffer does not exist, so this wraps to the top.
            // The instance work per byte is the same either way, which is what is being measured.
            _row = 0;
        }

        public void CsiDispatch(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final)
        {
            switch (final)
            {
                case (byte)'H':
                    _row = Math.Clamp(parameters.Value(0, 1) - 1, 0, _rows - 1);
                    _column = Math.Clamp(parameters.Value(1, 1) - 1, 0, _columns - 1);
                    break;

                case (byte)'J':
                    Array.Fill(_cells, CellInstance.For(GlyphPlacement.Empty, Text, Ground));
                    break;

                case (byte)'m':
                    _foreground = Sgr(parameters);
                    break;

                default:
                    break;
            }
        }

        /// <summary>Enough of SGR to make the colour change with the stream, and no more.</summary>
        private static Rgb Sgr(in CsiParameters parameters)
        {
            for (int group = 0; group < parameters.Count; group++)
            {
                ReadOnlySpan<int> values = parameters.Group(group);

                if (values.Length >= 5 && values[0] == 38 && values[1] == 2)
                {
                    return new Rgb((byte)values[^3], (byte)values[^2], (byte)values[^1]);
                }

                if (values.Length >= 1 && values[0] is >= 30 and <= 37)
                {
                    int index = values[0] - 30;
                    return new Rgb((byte)(((index & 1) * 180) + 40), (byte)((((index >> 1) & 1) * 180) + 40),
                                   (byte)((((index >> 2) & 1) * 180) + 40));
                }
            }

            return Text;
        }

        public void EscapeDispatch(ReadOnlySpan<byte> intermediates, byte final)
        {
        }

        public void OscStart()
        {
        }

        public void OscPut(ReadOnlySpan<byte> bytes)
        {
        }

        public void OscEnd()
        {
        }

        public void DcsHook(in CsiParameters parameters, ReadOnlySpan<byte> intermediates, byte final)
        {
        }

        public void DcsPut(ReadOnlySpan<byte> bytes)
        {
        }

        public void DcsUnhook()
        {
        }
    }
}
