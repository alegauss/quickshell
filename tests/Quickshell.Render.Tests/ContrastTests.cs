using System.Globalization;
using System.Runtime.InteropServices;
using Quickshell.Terminal;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit;

namespace Quickshell.Render.Tests;

/// <summary>
/// QS107: how much lighter this renderer's text is than the text Windows draws, measured rather than
/// guessed.
///
/// <para><b>Why a measurement.</b> DirectWrite's coverage comes back raw. Direct2D applies the
/// rendering parameters on top — gamma, and an enhanced contrast that thickens stems — in a shader
/// whose curve is not published. This renderer blends coverage in linear light, which stands in for
/// the gamma, and applies no enhancement. So the same run is drawn both ways, white on black, and
/// the total light each put on the glass is compared. Direct2D here is the reference a test reads,
/// and nothing the client draws with.</para>
///
/// <para>The number is written to <c>TestResults/contrast.txt</c> and recorded in
/// <c>docs/measurements/contrast.md</c>. What this asserts is only that both drew the run, within a
/// factor that a missing glyph or a blank target would fall outside.</para>
/// </summary>
public sealed class ContrastTests
{
    private const uint Width = 320;
    private const uint Height = 64;
    private const string Run = "Hamburgefonstiv 0123456789";

    [Fact]
    public void ThisRenderersInkIsMeasuredAgainstDirect2Ds()
    {
        List<string> lines = [];
        List<double> ratios = [];

        foreach (bool dark in new[] { true, false })
        {
            foreach (bool clearType in new[] { false, true })
            {
                // Over the row of cells the run occupies and nothing else: past the last whole cell,
                // and below the row, this renderer draws nothing (QS177), which on a light ground
                // would count as ink neither picture put there.
                byte[] drawn = Ours(clearType, dark, out bool honoured, out int across, out int down);
                double ours = Ink(drawn, dark, across, down);
                double reference = Ink(Direct2D(honoured, dark), dark, across, down);
                double ratio = ours / reference;

                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{(dark ? "light on dark" : "dark on light")}, {(honoured ? "cleartype" : "grayscale")}: "
                    + $"ours {ours:F1}, direct2d {reference:F1}, ratio {ratio:F3}"));
                ratios.Add(ratio);
            }
        }

        string results = Path.Combine(Root(), "TestResults");
        Directory.CreateDirectory(results);
        File.WriteAllLines(Path.Combine(results, "contrast.txt"), lines);

        Assert.All(ratios, ratio => Assert.InRange(ratio, 0.5, 2.0));
    }

    /// <summary>The run through this renderer, as a pane draws it: one cell per character.</summary>
    private static byte[] Ours(bool clearType, bool dark, out bool honoured, out int across, out int down)
    {
        Rgb ink = dark ? Rgb.White : Rgb.Black;
        Rgb ground = dark ? Rgb.Black : Rgb.White;

        using TestWindow window = new((int)Width, (int)Height);
        using GraphicsDevice device = GraphicsDevice.Open(outputWindow: window.Handle);
        using PresentSurface surface = PresentSurface.For(device, window.Handle, Width, Height);
        using GlyphRasteriser rasteriser = new();

        FontSettings font = FontSettings.Default with { ClearType = clearType };

        using GlyphAtlas atlas = GlyphAtlas.For(device, font, rasteriser: rasteriser);
        CellMetrics metrics = rasteriser.Measure(font);
        using CellRenderer renderer = CellRenderer.For(device, atlas, metrics);

        honoured = atlas.IsClearType;

        (int columns, _) = metrics.GridFor(Width, Height);
        CellInstance[] cells = new CellInstance[columns];

        across = columns * metrics.Width;
        down = metrics.Height;

        for (int column = 0; column < columns; column++)
        {
            GlyphPlacement glyph = column < Run.Length && Run[column] != ' '
                ? atlas.Cache(Run[column], maximumAdvance: metrics.Width)
                : GlyphPlacement.Empty;

            cells[column] = CellInstance.For(glyph, ink, ground);
        }

        renderer.Draw(surface, cells, columns);

        using ID3D11Resource resource = surface.View.Resource;
        using ID3D11Texture2D back = resource.QueryInterface<ID3D11Texture2D>();

        return ReadBack(device.Device, device.Context, back);
    }

    /// <summary>The same run through Direct2D, with the system's own rendering parameters.</summary>
    private static byte[] Direct2D(bool clearType, bool dark)
    {
        Color4 ink = dark ? new Color4(1f, 1f, 1f, 1f) : new Color4(0f, 0f, 0f, 1f);
        Color4 ground = dark ? new Color4(0f, 0f, 0f, 1f) : new Color4(1f, 1f, 1f, 1f);

        // WARP where there is no hardware, as the renderer's own device falls back: the guest has no
        // GPU, and Direct2D's text is the same text on either.
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                                    [Vortice.Direct3D.FeatureLevel.Level_11_0], out ID3D11Device? created,
                                    out ID3D11DeviceContext? context).Failure)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                                    [Vortice.Direct3D.FeatureLevel.Level_11_0], out created,
                                    out context).CheckError();
        }

        using ID3D11Device device = created!;
        using ID3D11DeviceContext immediate = context!;
        using ID3D11Texture2D target = device.CreateTexture2D(new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });

        using IDXGISurface surface = target.QueryInterface<IDXGISurface>();
        using ID2D1Factory factory = D2D1.D2D1CreateFactory<ID2D1Factory>();
        using ID2D1RenderTarget canvas = factory.CreateDxgiSurfaceRenderTarget(surface,
            new RenderTargetProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                                                                       Vortice.DCommon.AlphaMode.Ignore)));
        using IDWriteFactory writes = DWrite.DWriteCreateFactory<IDWriteFactory>();
        using IDWriteTextFormat format = writes.CreateTextFormat(FontSettings.Default.Family,
            Vortice.DirectWrite.FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal, FontSettings.Default.SizeInPixels);
        using ID2D1SolidColorBrush brush = canvas.CreateSolidColorBrush(ink);

        canvas.TextAntialiasMode = clearType ? Vortice.Direct2D1.TextAntialiasMode.Cleartype
                                             : Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        canvas.BeginDraw();
        canvas.Clear(ground);
        canvas.DrawText(Run, format, new Rect(0, 0, Width, Height), brush);
        canvas.EndDraw();

        return ReadBack(device, immediate, target);
    }

    private static byte[] ReadBack(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source)
    {
        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        context.CopyResource(staging, source);

        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read);
        byte[] frame = new byte[Width * Height * 4];

        try
        {
            for (int row = 0; row < Height; row++)
            {
                Marshal.Copy(mapped.DataPointer + (row * (int)mapped.RowPitch), frame,
                             row * (int)Width * 4, (int)Width * 4);
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }

        return frame;
    }

    /// <summary>
    /// The ink: every pixel's three channels in linear light, summed — as light for light text on a
    /// dark ground, and as the light taken away for dark text on a light one.
    /// </summary>
    private static double Ink(byte[] frame, bool dark, int across, int down)
    {
        double total = 0;

        for (int y = 0; y < down; y++)
        {
            for (int x = 0; x < across; x++)
            {
                int offset = ((y * (int)Width) + x) * 4;
                double light = (Linear(frame[offset]) + Linear(frame[offset + 1]) + Linear(frame[offset + 2])) / 3.0;
                total += dark ? light : 1.0 - light;
            }
        }

        return total;
    }

    private static double Linear(byte encoded)
    {
        double value = encoded / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("no repository root above this test");
    }
}
