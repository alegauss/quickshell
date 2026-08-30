using System.Reflection;
using System.Runtime.InteropServices;
using Quickshell.Terminal;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Quickshell.Render;

/// <summary>
/// The whole grid in one <c>DrawInstanced</c>.
///
/// <para><b>One draw, whatever the window's size.</b> A unit quad of four vertices, one instance per
/// visible cell, and the cell's position derived on the GPU from its instance id. Frame cost then
/// scales with pixels rather than with draw calls, which is the difference between a terminal that
/// stays smooth when maximised on a 4K display and one that does not.</para>
///
/// <para><b>The instance buffer is write-discard and double-buffered.</b> Discard is what lets the
/// driver hand back fresh memory instead of waiting for the last frame to finish reading the old;
/// the second buffer is what keeps that true even where a driver takes the request as advice.</para>
///
/// <para><b>There is no vertex buffer.</b> The four corners come from <c>SV_VertexID</c>, so the
/// only thing bound per frame is the cells themselves.</para>
/// </summary>
public sealed class CellRenderer : IDeviceResource, IDisposable
{
    /// <summary>How many cells the first instance buffer holds before it has to grow.</summary>
    private const int InitialCapacity = 4096;

    /// <summary>Corners of the unit quad, issued as a triangle strip.</summary>
    private const int QuadVertices = 4;

    /// <summary>
    /// Atlas pages the shader can reach. Feature level 11_0 cannot index a texture array
    /// dynamically, so each page is a named register and a branch, and four of them is the ceiling
    /// this renderer and <see cref="GlyphAtlas"/> agree on.
    /// </summary>
    public const int AtlasSlots = 4;

    private readonly GraphicsDevice _graphics;
    private readonly GlyphAtlas _atlas;
    private readonly ID3D11Buffer?[] _instances = new ID3D11Buffer?[2];

    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _layout;
    private ID3D11Buffer? _frame;
    private ID3D11RasterizerState? _rasteriser;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private ID3D11Device? _device;
    private TimeSpan? _elapsed;
    private int _capacity;
    private int _next;

    private CellRenderer(GraphicsDevice graphics, GlyphAtlas atlas, CellMetrics metrics)
    {
        _graphics = graphics;
        _atlas = atlas;
        Metrics = metrics;
    }

    /// <summary>
    /// What the shader is told once per frame. Eighty bytes, laid out so that no three-float vector
    /// straddles a sixteen-byte boundary — HLSL would silently move one that did, and the picture
    /// would be wrong in a way that looks like a shader bug rather than a packing one.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public float CellWidth;
        public float CellHeight;
        public float ViewportWidth;
        public float ViewportHeight;
        public uint Columns;
        public float Baseline;
        public float UnderlineY;
        public float UnderlineThickness;
        public float StrikeY;
        public float StrikeThickness;
        public float CursorShowing;
        public float ClearType;
        public float CursorRed;
        public float CursorGreen;
        public float CursorBlue;
        public float Reserved2;
        public float SelectionRed;
        public float SelectionGreen;
        public float SelectionBlue;
        public float Reserved3;
    }

    /// <summary>Opens the renderer and registers it, so a device loss rebuilds its shaders with everything else.</summary>
    public static CellRenderer For(GraphicsDevice graphics, GlyphAtlas atlas, CellMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentOutOfRangeException.ThrowIfLessThan(metrics.Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(metrics.Height, 1);

        if (atlas.MaximumPages > AtlasSlots)
        {
            // Refused here rather than drawn wrongly later: a cell on page four would sample page
            // zero and show whichever character happened to be at those coordinates.
            throw new ArgumentException(
                $"the atlas may hold {atlas.MaximumPages} pages and this shader reaches {AtlasSlots}",
                nameof(atlas));
        }

        CellRenderer renderer = new(graphics, atlas, metrics);
        graphics.Register(renderer);
        return renderer;
    }

    /// <summary>The grid geometry every instance is placed against.</summary>
    public CellMetrics Metrics { get; private set; }

    /// <summary>
    /// Whether the cursor is showing right now, and when that next changes.
    ///
    /// <para>Turning <see cref="CursorBlink.Enabled"/> off makes <see cref="NextCursorWake"/> answer
    /// null, which is the difference between a cursor that stops flickering and an idle window that
    /// genuinely stops drawing.</para>
    /// </summary>
    public CursorBlink Blink { get; } = new();

    /// <summary>The cursor's colour, which a block cursor inverts the glyph against.</summary>
    public Rgb CursorColour { get; set; } = new(220, 220, 220);

    /// <summary>The background a selected cell takes.</summary>
    public Rgb SelectionColour { get; set; } = new(52, 78, 120);

    /// <summary>
    /// How long this renderer has been running, which is what the blink phase is measured from.
    /// Settable so a test can put the cursor in either phase without waiting for a real clock.
    /// </summary>
    public TimeSpan Elapsed
    {
        get => _elapsed ?? _clock.Elapsed;
        set => _elapsed = value;
    }

    /// <summary>Whether the cursor is drawn on the next frame.</summary>
    public bool CursorShowing => Blink.IsShowingAt(Elapsed);

    /// <summary>
    /// How long until the cursor's phase changes, or null when nothing is going to change on its
    /// own. An idle loop sleeps on this, and a null is a window that issues no draw calls at all.
    /// </summary>
    public TimeSpan? NextCursorWake() => Blink.NextChangeAfter(Elapsed);

    /// <summary>Draw calls issued. One per frame drawn, which is the whole claim this line makes.</summary>
    public long Draws { get; private set; }

    /// <summary>Cells the instance buffer currently holds without reallocating.</summary>
    public int Capacity => _capacity;

    /// <summary>Points the grid at a different cell geometry, after a font or DPI change.</summary>
    public void UseMetrics(CellMetrics metrics)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(metrics.Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(metrics.Height, 1);

        Metrics = metrics;
    }

    /// <summary>
    /// Draws one frame's worth of cells into a surface. The caller owns the wait and the present:
    /// this puts the grid on the back buffer and nothing else.
    /// </summary>
    /// <param name="surface">The surface to draw into.</param>
    /// <param name="cells">Row-major cells, <paramref name="columns"/> to a row.</param>
    /// <param name="columns">How many cells make a row.</param>
    public void Draw(PresentSurface surface, ReadOnlySpan<CellInstance> cells, int columns)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);

        if (_device is null)
        {
            throw new InvalidOperationException("the renderer has no device");
        }

        if (cells.IsEmpty)
        {
            return;
        }

        ID3D11DeviceContext context = _graphics.Context;
        ID3D11Buffer instances = Upload(cells);

        WriteFrameConstants(context, surface, columns);

        context.OMSetRenderTargets(surface.View);
        context.RSSetViewport(0f, 0f, surface.Width, surface.Height);
        context.RSSetState(_rasteriser);

        context.IASetInputLayout(_layout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetVertexBuffer(0, instances, CellInstance.Stride);

        context.VSSetShader(_vertexShader);
        context.VSSetConstantBuffer(0, _frame);
        context.PSSetShader(_pixelShader);
        context.PSSetConstantBuffer(0, _frame);
        BindAtlas(context);

        context.DrawInstanced(QuadVertices, (uint)cells.Length, 0, 0);
        Draws++;
    }

    void IDeviceResource.Create(ID3D11Device device)
    {
        _device = device;

        ReadOnlyMemory<byte> vertexCode = Compile("VertexMain", "vs_5_0");
        ReadOnlyMemory<byte> pixelCode = Compile("PixelMain", "ps_5_0");

        _vertexShader = device.CreateVertexShader(vertexCode.Span);
        _pixelShader = device.CreatePixelShader(pixelCode.Span);

        // Every element is per-instance: there is no per-vertex stream at all.
        InputElementDescription[] elements =
        [
            new("FOREGROUND", 0, Format.R32_UInt, 0, 0, InputClassification.PerInstanceData, 1),
            new("BACKGROUND", 0, Format.R32_UInt, 4, 0, InputClassification.PerInstanceData, 1),
            new("GLYPHORIGIN", 0, Format.R32_UInt, 8, 0, InputClassification.PerInstanceData, 1),
            new("GLYPHSIZE", 0, Format.R32_UInt, 12, 0, InputClassification.PerInstanceData, 1),
            new("GLYPHBEARING", 0, Format.R32_UInt, 16, 0, InputClassification.PerInstanceData, 1),
        ];

        _layout = device.CreateInputLayout(elements, vertexCode.Span);

        _frame = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<FrameConstants>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        // No culling: a quad's winding is not something a grid should have an opinion about, and a
        // back-facing cell is a black rectangle nobody can explain.
        _rasteriser = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
        });

        _capacity = 0;
        Grow(InitialCapacity);
    }

    void IDeviceResource.Release()
    {
        for (int buffer = 0; buffer < _instances.Length; buffer++)
        {
            _instances[buffer]?.Dispose();
            _instances[buffer] = null;
        }

        _rasteriser?.Dispose();
        _rasteriser = null;
        _frame?.Dispose();
        _frame = null;
        _layout?.Dispose();
        _layout = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _capacity = 0;
        _device = null;
    }

    /// <summary>Releases the shaders and buffers. The atlas and the device are not disposed here.</summary>
    public void Dispose() => ((IDeviceResource)this).Release();

    private unsafe ID3D11Buffer Upload(ReadOnlySpan<CellInstance> cells)
    {
        if (cells.Length > _capacity)
        {
            Grow(cells.Length);
        }

        _next = (_next + 1) % _instances.Length;
        ID3D11Buffer buffer = _instances[_next]!;

        MappedSubresource mapped = _graphics.Context.Map(buffer, MapMode.WriteDiscard);

        try
        {
            ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(cells);
            source.CopyTo(new Span<byte>((void*)mapped.DataPointer, _capacity * CellInstance.Stride));
        }
        finally
        {
            _graphics.Context.Unmap(buffer, 0);
        }

        return buffer;
    }

    private unsafe void WriteFrameConstants(ID3D11DeviceContext context, PresentSurface surface, int columns)
    {
        FrameConstants constants = new()
        {
            CellWidth = Metrics.Width,
            CellHeight = Metrics.Height,
            ViewportWidth = surface.Width,
            ViewportHeight = surface.Height,
            Columns = (uint)columns,
            Baseline = Metrics.Baseline,
            UnderlineY = Metrics.UnderlineY,
            UnderlineThickness = Metrics.UnderlineThickness,
            StrikeY = Metrics.StrikeY,
            StrikeThickness = Metrics.StrikeThickness,
            CursorShowing = CursorShowing ? 1f : 0f,

            // The atlas's answer and not the renderer's: it is the one that knows both what the font
            // asked for and what the display said about its stripes.
            ClearType = _atlas.IsClearType ? 1f : 0f,
            CursorRed = CursorColour.Red / 255f,
            CursorGreen = CursorColour.Green / 255f,
            CursorBlue = CursorColour.Blue / 255f,
            SelectionRed = SelectionColour.Red / 255f,
            SelectionGreen = SelectionColour.Green / 255f,
            SelectionBlue = SelectionColour.Blue / 255f,
        };

        MappedSubresource mapped = context.Map(_frame!, MapMode.WriteDiscard);

        try
        {
            *(FrameConstants*)mapped.DataPointer = constants;
        }
        finally
        {
            context.Unmap(_frame!, 0);
        }
    }

    private void BindAtlas(ID3D11DeviceContext context)
    {
        // A slot past the last page gets page zero rather than being left as whatever was there:
        // no instance can name it, and a stale binding from somewhere else is worse than a
        // duplicate. Four of each is the shader's own ceiling, and the atlas is opened under it.
        for (int slot = 0; slot < AtlasSlots && _atlas.PageCount > 0; slot++)
        {
            context.PSSetShaderResource((uint)slot, _atlas.PageView(Math.Min(slot, _atlas.PageCount - 1)));
        }

        for (int slot = 0; slot < AtlasSlots && _atlas.ColourPageCount > 0; slot++)
        {
            context.PSSetShaderResource(
                (uint)(AtlasSlots + slot),
                _atlas.ColourPageView(Math.Min(slot, _atlas.ColourPageCount - 1)));
        }
    }

    private void Grow(int cells)
    {
        ID3D11Device device = _device ?? throw new InvalidOperationException("the renderer has no device");

        int capacity = Math.Max(InitialCapacity, _capacity);

        while (capacity < cells)
        {
            capacity *= 2;
        }

        for (int buffer = 0; buffer < _instances.Length; buffer++)
        {
            _instances[buffer]?.Dispose();
            _instances[buffer] = device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)(capacity * CellInstance.Stride),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.VertexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            });
        }

        _capacity = capacity;
    }

    private static ReadOnlyMemory<byte> Compile(string entryPoint, string profile)
    {
        // The shader is compiled here rather than shipped as bytecode so that the source in the
        // repository is the source that ran: a stale .cso is a class of bug with no symptom.
        using Stream stream = typeof(CellRenderer).Assembly
            .GetManifestResourceStream("Quickshell.Render.Grid.hlsl")
            ?? throw new InvalidOperationException("Grid.hlsl was not embedded in this assembly");

        using StreamReader reader = new(stream);
        string source = reader.ReadToEnd();

        Compiler.Compile(source, entryPoint, "Grid.hlsl", profile, out Blob code, out Blob? errors);

        using (errors)
        using (code)
        {
            if (code is null)
            {
                throw new InvalidOperationException(
                    $"Grid.hlsl failed to compile {entryPoint}: {errors?.AsString()}");
            }

            return code.AsBytes();
        }
    }
}
