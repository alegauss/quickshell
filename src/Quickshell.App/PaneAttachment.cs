using System.Windows;
using System.Windows.Media;
using Quickshell.Render;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// A pane, a model and the loop between them, for as long as this is alive.
///
/// <para><b>This is the object the client holds.</b> A <see cref="TerminalView"/> needs a handle and
/// a size, and a window has neither until WPF has laid it out — so something has to wait, and a
/// client that waited on the way up would be a client that shows nothing until a device opens. This
/// waits instead, off the first-paint path, and the window is on screen throughout.</para>
///
/// <para>Disposing it stops the loop before releasing the device, in that order and not the other:
/// a device released under a thread that is mid-frame is a crash on the way out, which is the worst
/// kind because the user has already decided to leave.</para>
/// </summary>
public sealed class PaneAttachment : IDisposable
{
    private readonly TerminalPane _pane;
    private readonly Emulator _emulator;
    private readonly DamageSignal _damage;
    private readonly string _family;
    private readonly float _sizeInPoints;
    private readonly CancellationTokenSource _stop = new();

    private Task _loop = Task.CompletedTask;
    private bool _disposed;

    internal PaneAttachment(TerminalPane pane, Emulator emulator, DamageSignal damage,
                            string family, float sizeInPoints)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(damage);
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        _pane = pane;
        _emulator = emulator;
        _damage = damage;
        _family = family;
        _sizeInPoints = sizeInPoints;

        _pane.SizeChanged += Sized;

        // In case the pane already has both, which is the case when a caller attaches to a window
        // that is already up.
        Begin();
    }

    /// <summary>The view, once there was something to open it on. Null until then.</summary>
    public TerminalView? View { get; private set; }

    /// <summary>
    /// What went wrong opening the device, or null.
    ///
    /// <para>Kept rather than thrown: this runs on a layout callback, and a machine with no usable
    /// adapter should get a client with an unpainted pane and a line in a diagnostic bundle, not a
    /// window that vanishes during its own first layout.</para>
    /// </summary>
    public Exception? Failed { get; private set; }

    /// <summary>Stops the loop and releases the device.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pane.SizeChanged -= Sized;

        _stop.Cancel();

        // Bounded, because a loop that will not stop must not hold a window open. The device is
        // released either way: the process is leaving.
        _loop.Wait(TimeSpan.FromSeconds(2));

        View?.Dispose();
        View = null;

        _stop.Dispose();
    }

    private void Sized(object sender, SizeChangedEventArgs e) => Begin();

    private void Begin()
    {
        if (View is not null || Failed is not null || _disposed)
        {
            return;
        }

        if (_pane.PaneHandle == nint.Zero || _pane.ActualWidth < 1d || _pane.ActualHeight < 1d)
        {
            return;
        }

        // Pixels, not DIPs. The manifest declares this process per-monitor aware, which means
        // nothing is scaled for it afterwards and the numbers here are the ones that count.
        DpiScale dpi = VisualTreeHelper.GetDpi(_pane);

        try
        {
            View = TerminalView.Open(
                _pane.PaneHandle,
                (uint)Math.Max(1d, _pane.ActualWidth * dpi.DpiScaleX),
                (uint)Math.Max(1d, _pane.ActualHeight * dpi.DpiScaleY),
                new FontSettings(_family, _sizeInPoints, (float)dpi.PixelsPerInchX),
                _emulator.Palette);
        }
        catch (Exception error)
        {
            Failed = error;

            return;
        }

        // The grid the window turned out to hold is the size the model takes, and later the size the
        // far end is told. Here it is the first size rather than a resize, which is why nothing is
        // debounced and nobody is told: there is no previous size to have been wrong.
        _emulator.Resize(View.Columns, View.Rows);

        // A screen reader reads this buffer, and the texture is unreadable to assistive technology
        // by construction — this is the only path. Set here only for a caller that did not: WPF
        // builds an element's peer once, so a pane already asked about keeps whatever it answered.
        _pane.Reading ??= _emulator.Buffer;

        // Off the UI thread from here. Nothing else touches the device, which is what makes an
        // unsynchronised D3D11 context correct.
        _loop = Task.Run(() => View.RunAsync(_emulator, _damage, _stop.Token));

        // The pane no longer needs watching for a first size; what it needs next is a resize, and
        // that is QS32's three parties rather than this one's opening.
        _pane.SizeChanged -= Sized;
    }
}
