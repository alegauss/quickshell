using System.Runtime.InteropServices;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>What the mouse did on the pane.</summary>
public enum PaneMouseKind
{
    /// <summary>The left button went down, which begins a selection.</summary>
    Pressed,

    /// <summary>It moved, which extends one while the button is down.</summary>
    Moved,

    /// <summary>It came up, or the capture went to somebody else. Either ends the drag.</summary>
    Released,

    /// <summary>The wheel turned, and <c>Notches</c> says how far and which way.</summary>
    Wheeled,
}

/// <summary>One mouse event, in the pane's own pixels.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="X">Pixels from the pane's left edge. Negative while a drag is off the left of it.</param>
/// <param name="Y">Pixels from its top, and negative above it for the same reason.</param>
/// <param name="Notches">
/// Wheel detents, positive away from the user. Zero for everything that is not a wheel.
/// </param>
public readonly record struct PaneMouse(PaneMouseKind Kind, int X, int Y, int Notches = 0);

/// <summary>
/// The terminal's own window, hosted inside WPF's.
///
/// <para><b>A child HWND, and QS4 is why.</b> Three hosts were measured over three passes: this one
/// reaches a click-to-pixel floor of one refresh interval, and neither <c>D3DImage</c> nor a
/// <c>SwapChainPanel</c> reaches a single frame in any pass. The swapchain presents to this handle
/// directly, so nothing WPF does after the present is in the path.</para>
///
/// <para>It draws nothing itself. The renderer owns the device and the surface; this owns the
/// handle they need and the lifetime of it.</para>
///
/// <para><b>It does carry the mouse, and only because there is nowhere else.</b> Mouse messages go
/// to the window under the pointer, which is this one, and WPF never sees them — so a client that
/// waited for <c>MouseDown</c> on the host element would wait for ever.</para>
/// </summary>
public sealed class TerminalPane : HwndHost
{
    /// <summary>A child window with no border and no background of its own.</summary>
    private const uint ChildStyle = 0x40000000 | 0x10000000;   // WS_CHILD | WS_VISIBLE

    private static readonly nint Class = PaneClass.Register();

    /// <summary>
    /// The handle a swapchain is created against. Zero before the window is built.
    ///
    /// <para>Named apart from <see cref="HwndHost.Handle"/> deliberately: that one is the host's
    /// own, and a renderer given it would present into WPF's window rather than into the pane.</para>
    /// </summary>
    public nint PaneHandle { get; private set; }

    /// <summary>Raised once the handle exists, which is when a renderer may be opened on it.</summary>
    public event EventHandler? Ready;

    /// <summary>
    /// The buffer a screen reader reads through this pane, or null while there is no session.
    ///
    /// <para>Set before the pane is shown. Without it the automation peer has nothing to publish and
    /// assistive technology finds the rectangle it would have found anyway.</para>
    /// </summary>
    public TerminalBuffer? Reading { get; set; }

    /// <summary>What a screen reader sees, which for a texture is only what is built for it.</summary>
    public TerminalAutomationPeer? Automation { get; private set; }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        if (Reading is null)
        {
            return base.OnCreateAutomationPeer();
        }

        Automation = new TerminalAutomationPeer(this, Reading);

        return Automation;
    }

    /// <summary>
    /// WM_GETOBJECT: what Windows sends a window when something wants its accessibility.
    /// </summary>
    private const int WmGetObject = 0x003D;

    private const int ButtonDown = 0x0201;
    private const int ButtonUp = 0x0202;
    private const int Moved = 0x0200;
    private const int CaptureLost = 0x0215;
    private const int Wheel = 0x020A;

    /// <summary>One detent, which is what Windows divides a wheel's delta by.</summary>
    private const int PerNotch = 120;

    /// <summary>
    /// The mouse, in this pane's own pixels.
    ///
    /// <para><b>Raised from the pane's window procedure and not from WPF's events</b>, because this
    /// is a child HWND: the mouse messages arrive here and WPF never sees them. Which is also why
    /// the capture below is taken with the Win32 call rather than with <c>CaptureMouse</c> — the
    /// element WPF would capture for is not the window the messages are going to.</para>
    /// </summary>
    public event Action<PaneMouse>? Mouse;

    /// <summary>
    /// Answers the child window's own accessibility question, and carries the mouse.
    ///
    /// <para><b>The <c>WM_GETOBJECT</c> half is a crash fix and QS148 is the whole of it.</b> Left to
    /// <see cref="HwndHost"/>, this message goes down a path that expects a peer of a type internal
    /// to WPF; the peer this pane publishes is not one, so the path produces no provider, hands the
    /// nothing to UIA, and the exception that follows comes out inside the message pump — which
    /// means the client exits. Any accessibility client asking once was enough: a screen reader, a
    /// UI case, or Windows itself.</para>
    ///
    /// <para><b>Nothing is lost by answering nothing.</b> This window is a texture a swapchain
    /// presents into; it has no controls and no text of its own, and MSAA falls back to the default
    /// provider for a window that declines. What a reader is meant to find is
    /// <see cref="TerminalAutomationPeer"/>, and that is published as the peer of this element in
    /// WPF's tree — a different path, which this message was never on.</para>
    ///
    /// <para><b>The mouse half exists because there is nowhere else for it.</b> A drag that leaves
    /// the window still belongs to the drag, so the button press takes the capture and the release
    /// gives it back; without that, selecting past the bottom edge of the pane stops the moment the
    /// pointer crosses it.</para>
    /// </summary>
    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmGetObject:
                handled = true;

                return nint.Zero;

            case ButtonDown:
                SetCapture(hwnd);
                Raise(PaneMouseKind.Pressed, lParam);

                return nint.Zero;

            case Moved:
                Raise(PaneMouseKind.Moved, lParam);

                return nint.Zero;

            case ButtonUp:
                ReleaseCapture();
                Raise(PaneMouseKind.Released, lParam);

                return nint.Zero;

            case Wheel:
                // The only message here whose coordinates are the screen's rather than this
                // window's, and they are dropped: what a notch does depends on what the program has
                // asked for, never on where the pointer was when it turned.
                Mouse?.Invoke(new PaneMouse(PaneMouseKind.Wheeled, 0, 0,
                                            (short)((wParam >> 16) & 0xFFFF) / PerNotch));

                return nint.Zero;

            case CaptureLost:
                // Somebody else took the mouse — an Alt-Tab, a dialog. The drag is over and the
                // selection stands; what must not happen is a pane that thinks a button is still
                // down and extends the selection on the next unrelated movement.
                Mouse?.Invoke(new PaneMouse(PaneMouseKind.Released, 0, 0));

                return nint.Zero;

            default:
                return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }
    }

    /// <summary>
    /// Unpacks a coordinate pair Windows put in one word each, signed.
    ///
    /// <para>Signed and not unsigned, which is the whole of this method: with the mouse captured a
    /// drag above or left of the pane reports a negative coordinate, and read as unsigned that is a
    /// pointer sixty-five thousand pixels the other way.</para>
    /// </summary>
    private void Raise(PaneMouseKind kind, nint packed) =>
        Mouse?.Invoke(new PaneMouse(kind, (short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF)));

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    /// <inheritdoc/>
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        PaneHandle = PaneClass.Create(Class, hwndParent.Handle, ChildStyle);

        Ready?.Invoke(this, EventArgs.Empty);

        return new HandleRef(this, PaneHandle);
    }

    /// <inheritdoc/>
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        PaneClass.Destroy(hwnd.Handle);
        PaneHandle = nint.Zero;
    }
}

/// <summary>The window class the pane's handle belongs to, registered once for the process.</summary>
internal static partial class PaneClass
{
    private const string Name = "QuickshellPane";

    /// <summary>Registers the class, once. A second call answers with the same atom.</summary>
    public static nint Register()
    {
        WindowClass description = new()
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),

            // No background brush: the swapchain paints every pixel of this window every frame, and
            // a brush would be Windows erasing it first — which is the classic flicker.
            Background = nint.Zero,
            WndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
            ClassName = Marshal.StringToHGlobalUni(Name),
        };

        ushort atom = RegisterClassExW(ref description);

        // 1410 is ERROR_CLASS_ALREADY_EXISTS, which is what a second window in the same process
        // gets and is not a failure.
        return atom != 0 || Marshal.GetLastWin32Error() == 1410 ? atom : 0;
    }

    /// <summary>Creates one pane window as a child of the given parent.</summary>
    public static nint Create(nint registered, nint parent, uint style) =>
        CreateWindowExW(0, Name, string.Empty, style, 0, 0, 1, 1, parent, 0, 0, 0);

    /// <summary>Destroys one.</summary>
    public static void Destroy(nint window) => DestroyWindow(window);

    /// <summary>
    /// The window procedure, which does nothing at all.
    ///
    /// <para>Deliberately: input arrives through WPF and pixels arrive through the swapchain, so
    /// this window's only job is to exist and have a handle. Anything it did here would be a second
    /// place input or painting could come from.</para>
    /// </summary>
    private static readonly WindowProcedure Procedure = DefWindowProcW;

    private delegate nint WindowProcedure(nint window, uint message, nint wide, nint low);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint IconSmall;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WindowClass description);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName,
                                                uint style, int x, int y, int width, int height,
                                                nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wide, nint low);
}
