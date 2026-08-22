using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HostProbe.Core;

/// <summary>
/// Reads the composited desktop through DXGI output duplication.
///
/// GDI cannot do this job: a screen DC does not see a flip-model swapchain's content at all,
/// so the first version of this probe measured a pane that was, as far as it could tell, never
/// drawn. Duplication sees what the compositor put on the glass, whichever host produced it,
/// and hands back <c>LastPresentTime</c> - the QPC instant that desktop frame was presented.
/// That timestamp is what makes the interval a click-to-pixel measurement and not a
/// click-to-application-present one, and it is the same instrument for all three hosts.
/// </summary>
public sealed class DesktopProbe : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _staging;
    private readonly int _originX;
    private readonly int _originY;

    /// <summary>Diagnostics, so a run that saw nothing can say what it saw instead.</summary>
    public long FramesAcquired { get; private set; }

    public long AcquireFailures { get; private set; }

    public long PointerOnlyFrames { get; private set; }

    public uint LastPixel { get; private set; }

    private DesktopProbe(ID3D11Device device, ID3D11DeviceContext context,
                         IDXGIOutputDuplication duplication, int originX, int originY)
    {
        _device = device;
        _context = context;
        _duplication = duplication;
        _originX = originX;
        _originY = originY;

        _staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    /// <summary>Duplicates the output that owns a screen point, on that output's own adapter.</summary>
    public static DesktopProbe ForPoint(int screenX, int screenY)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1 adapter).Success; index++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success; outputIndex++)
                {
                    using (output)
                    {
                        Vortice.RawRect bounds = output.Description.DesktopCoordinates;

                        if (screenX < bounds.Left || screenX >= bounds.Right ||
                            screenY < bounds.Top || screenY >= bounds.Bottom)
                        {
                            continue;
                        }

                        D3D11.D3D11CreateDevice(
                            adapter,
                            DriverType.Unknown,
                            DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                            out ID3D11Device device,
                            out ID3D11DeviceContext context).CheckError();

                        using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                        IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);

                        return new DesktopProbe(device, context, duplication, bounds.Left, bounds.Top);
                    }
                }
            }
        }

        throw new InvalidOperationException($"no DXGI output owns the point {screenX},{screenY}");
    }

    /// <summary>
    /// Waits for a desktop frame in which the sampled pixel is lit, and answers the instant that
    /// frame was presented. Returns -1 if the deadline passes first.
    /// </summary>
    public long WaitForLit(int screenX, int screenY, int timeoutMs) =>
        Wait(screenX, screenY, timeoutMs, wantLit: true);

    /// <summary>Waits for the sampled pixel to be back in the idle band.</summary>
    public bool WaitForDark(int screenX, int screenY, int timeoutMs) =>
        Wait(screenX, screenY, timeoutMs, wantLit: false) >= 0;

    private long Wait(int screenX, int screenY, int timeoutMs, bool wantLit)
    {
        long deadline = Clock.Now + (long)(timeoutMs * (System.Diagnostics.Stopwatch.Frequency / 1000.0));

        while (Clock.Now < deadline)
        {
            if (!TryReadFrame(screenX, screenY, out uint pixel, out long presentedAt))
            {
                continue;
            }

            if (IsLit(pixel) == wantLit)
            {
                return presentedAt > 0 ? presentedAt : Clock.Now;
            }
        }

        return -1;
    }

    private bool TryReadFrame(int screenX, int screenY, out uint pixel, out long presentedAt)
    {
        pixel = 0;
        presentedAt = 0;

        Result result = _duplication.AcquireNextFrame(8, out OutduplFrameInfo info, out IDXGIResource resource);

        if (result.Failure)
        {
            AcquireFailures++;
            return false;
        }

        FramesAcquired++;

        try
        {
            if (info.LastPresentTime == 0)
            {
                // A pointer-only update: the desktop image did not change, so it answers nothing.
                PointerOnlyFrames++;
                return false;
            }

            using ID3D11Texture2D frame = resource.QueryInterface<ID3D11Texture2D>();

            _context.CopySubresourceRegion(
                _staging, 0, 0, 0, 0, frame, 0,
                new Box(screenX - _originX, screenY - _originY, 0,
                        screenX - _originX + 1, screenY - _originY + 1, 1));

            MappedSubresource mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                pixel = (uint)Marshal.ReadInt32(mapped.DataPointer);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            LastPixel = pixel;
            presentedAt = info.LastPresentTime;
            return true;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    /// <summary>
    /// Saves a region of the composited desktop. It goes through duplication for the same reason
    /// the pixel read does: a GDI screen capture of a window with a swapchain in it shows a hole
    /// where the pane is, which would make the airspace evidence a picture of nothing.
    /// </summary>
    public bool CaptureRegion(int screenX, int screenY, int width, int height, string path)
    {
        using ID3D11Texture2D staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        long deadline = Clock.Now + System.Diagnostics.Stopwatch.Frequency;

        while (Clock.Now < deadline)
        {
            Result result = _duplication.AcquireNextFrame(16, out OutduplFrameInfo info, out IDXGIResource resource);

            if (result.Failure)
            {
                continue;
            }

            try
            {
                if (info.LastPresentTime == 0)
                {
                    continue;
                }

                using ID3D11Texture2D frame = resource.QueryInterface<ID3D11Texture2D>();

                int left = screenX - _originX;
                int top = screenY - _originY;

                _context.CopySubresourceRegion(
                    staging, 0, 0, 0, 0, frame, 0,
                    new Box(left, top, 0, left + width, top + height, 1));

                MappedSubresource mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    using System.Drawing.Bitmap bitmap = new(
                        width, height, (int)mapped.RowPitch,
                        System.Drawing.Imaging.PixelFormat.Format32bppRgb, mapped.DataPointer);

                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                finally
                {
                    _context.Unmap(staging, 0);
                }

                return true;
            }
            finally
            {
                resource.Dispose();
                _duplication.ReleaseFrame();
            }
        }

        return false;
    }

    /// <summary>The pixel arrives as BGRA. The response is near-white; the idle band is dark.</summary>
    private static bool IsLit(uint bgra)
    {
        uint b = bgra & 0xFF;
        uint g = (bgra >> 8) & 0xFF;
        uint r = (bgra >> 16) & 0xFF;
        return r > 200 && g > 200 && b > 200;
    }

    public void Dispose()
    {
        _staging.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
