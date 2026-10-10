using Quickshell.Render;
using Quickshell.Terminal;
using Vortice.DXGI;

namespace Quickshell.Soak;

/// <summary>
/// The graphics half of the soak: a device, an atlas, and every parsing session's screen painted
/// into it as a pane would paint it (QS78).
///
/// <para><b>The atlas is the one cache with an eviction policy</b>, so it is the one place a policy
/// defect looks exactly like a leak, and the design names it for that reason. Nothing is presented:
/// a frame is built the way <c>TerminalView</c> builds it, through <see cref="GridPainter"/>, which
/// is what asks the atlas for glyphs and makes it allocate pages on the device. A swapchain would
/// add a window to a run that has to leave the desk alone for three days.</para>
///
/// <para><b>GPU memory is the process's own</b>, as the adapter counts it for this process, so a
/// texture leaked anywhere on this device shows up here even where the atlas's own count is
/// flat.</para>
/// </summary>
internal sealed class Graphics : IDisposable
{
    /// <summary>A pane's frame rate is set by damage, and four a second is plenty to keep the
    /// atlas being asked for what the screens hold.</summary>
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(250);

    private readonly GraphicsDevice _device;
    private readonly GlyphRasteriser _rasteriser;
    private readonly GlyphAtlas _atlas;
    private readonly GridPainter _painter;
    private readonly CellMetrics _metrics;
    private readonly IDXGIAdapter3? _adapter;
    private readonly CancellationTokenSource _stopping = new();

    private CellInstance[] _cells = [];
    private Task _painting = Task.CompletedTask;
    private long _frames;

    private Graphics()
    {
        _device = GraphicsDevice.Open();
        _rasteriser = new GlyphRasteriser();
        _atlas = GlyphAtlas.For(_device, FontSettings.Default, rasteriser: _rasteriser);
        _painter = new GridPainter(_atlas, new Palette());
        _metrics = _rasteriser.Measure(FontSettings.Default);

        using IDXGIDevice dxgi = _device.Device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgi.GetAdapter();

        _adapter = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
    }

    /// <summary>Opens the device, or says why there is none so the report can say so too.</summary>
    public static Graphics? TryOpen(out string why)
    {
        try
        {
            why = string.Empty;

            return new Graphics();
        }
        catch (Exception failure)
        {
            why = failure.Message;

            return null;
        }
    }

    /// <summary>Frames painted so far.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>Distinct glyphs the atlas holds.</summary>
    public int Glyphs => _atlas.CachedGlyphs;

    /// <summary>What the atlas's pages occupy: a byte a pixel on a coverage page, four on a colour one.</summary>
    public double AtlasMb =>
        ((double)_atlas.PageCount + (4.0 * _atlas.ColourPageCount))
        * GlyphAtlas.PageSize * GlyphAtlas.PageSize / (1024.0 * 1024.0);

    /// <summary>Whole pages evicted, which is the policy working and not a leak.</summary>
    public int Evictions => _atlas.Evictions;

    /// <summary>Video memory this process holds on the adapter, local and shared together; zero where
    /// the adapter cannot say.</summary>
    public double VideoMb
    {
        get
        {
            if (_adapter is null)
            {
                return 0;
            }

            ulong local = _adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;
            ulong shared = _adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal).CurrentUsage;

            return (local + shared) / (1024.0 * 1024.0);
        }
    }

    /// <summary>Paints every parsing session's screen on a loop of its own, until disposed.</summary>
    public void Start(IReadOnlyList<Soaked> sessions) =>
        _painting = Task.Run(() => PaintAsync(sessions, _stopping.Token));

    private async Task PaintAsync(IReadOnlyList<Soaked> sessions, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            foreach (Soaked session in sessions)
            {
                if (session.Emulator is not { } emulator)
                {
                    continue;
                }

                // Read while the parser writes, as the view reads it: see TerminalView.DrawIfNeeded.
                TerminalBuffer buffer = emulator.Buffer;
                int needed = buffer.Columns * buffer.Rows;

                if (_cells.Length < needed)
                {
                    _cells = new CellInstance[needed];
                }

                _painter.Paint(buffer, _cells, -1, 0, CursorShape.None, _metrics);
            }

            Interlocked.Increment(ref _frames);

            try
            {
                await Task.Delay(Frame, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stopping.Cancel();

        try
        {
            _painting.Wait();
        }
        catch (AggregateException)
        {
            // Stopping is how this ends.
        }

        _stopping.Dispose();
        _adapter?.Dispose();
        _atlas.Dispose();
        _rasteriser.Dispose();
        _device.Dispose();
    }
}
