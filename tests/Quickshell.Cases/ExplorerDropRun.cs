using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

using Winwright.Processes;
using Winwright.Verdicts;
using Winwright.Windowing;

using Xunit;

namespace Quickshell.Cases;

/// <summary>
/// QS188's falsification on a real desk: a file dragged out of the host's pane and dropped on an
/// Explorer folder is there afterwards, byte for byte.
///
/// <para><b>Not a case file, because a case cannot spell it yet.</b> winwright's <c>drag</c>
/// releases over an element of the window it started in, and this drop lands in Explorer's
/// (WW530, filed 2026-10-13). So this does what the engine would: the client is launched on the
/// guest's own sshd as connected.cases.json launches it, everything is found in the accessibility
/// tree, and the gesture itself is real input — a press on the row, a move past the drag threshold,
/// a release over Explorer's folder view. Replace it with a case once WW530 lands.</para>
///
/// <para><b>The guest's desk only.</b> It puts real input on the desk, which AGENTS.md allows in
/// the guest and nowhere else, and it needs what tools/provision-guest-sshd.ps1 sets up there: the
/// server on 127.0.0.1:2240 and the key it made. Where either is missing it skips naming the
/// script — which is every desk but the guest.</para>
/// </summary>
public sealed partial class ExplorerDropRun : IDisposable
{
    private const string Address = "127.0.0.1:2240";
    private const string Key = @"C:\ProgramData\quickshell-cases\probe_ed25519";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // On the server, which is this same machine: under Public, which the probe account can read.
    private readonly string _served = Path.Combine(@"C:\Users\Public", "qs188-" + Guid.NewGuid().ToString("N")[..8]);

    // Where Explorer is opened, and where the drop has to leave the file.
    private readonly string _landing = Path.Combine(Path.GetTempPath(), "qs188-drop-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _data = Path.Combine(Path.GetTempPath(), "qs188-data-" + Guid.NewGuid().ToString("N")[..8]);

    private Process? _client;
    private AutomationElement? _explorer;

    public void Dispose()
    {
        // Closed by its handle: WindowPattern.Close answered and left Explorer's window on the guest's
        // desk, where the next run found it.
        try
        {
            if (_explorer is { } open && open.Current.NativeWindowHandle is not 0 and var handle)
            {
                _ = PostMessageW(handle, 0x0010, 0, 0);
            }
        }
        catch (ElementNotAvailableException)
        {
            // Already closed.
        }

        if (_client is { HasExited: false })
        {
            _client.Kill(entireProcessTree: true);
            _client.WaitForExit(5000);
        }

        _client?.Dispose();

        foreach (string folder in (string[])[_served, _landing, _data])
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (IOException)
            {
                // Explorer may hold the folder a moment after its window closed; it is a temp folder.
            }
        }
    }

    [Fact]
    public void AFileDraggedFromTheHostsPaneLandsInExplorerByteForByte()
    {
        Precondition listening = Reaching.Probe([Address], Reaching.DefaultTimeoutMs);

        Assert.SkipUnless(listening.Satisfied && File.Exists(Key),
            $"{Address}: run tools/provision-guest-sshd.ps1 once, which sets up the server this drop is dragged from. {listening.Absence}");

        Assert.SkipUnless(Desk.Read().CanObserve, "this desk cannot be observed, so nothing can be dragged on it");

        byte[] payload = new byte[1 << 20];
        Random.Shared.NextBytes(payload);

        // Readable by the account the server signs in, which arrives by a network logon: a folder under
        // Public inherits read for interactive, service and batch logons and for nobody else.
        DirectoryInfo served = Directory.CreateDirectory(_served);
        System.Security.AccessControl.DirectorySecurity rules = served.GetAccessControl();
        rules.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule("probe",
            System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        served.SetAccessControl(rules);
        File.WriteAllBytes(Path.Combine(_served, "qs188.bin"), payload);
        Directory.CreateDirectory(_landing);
        Directory.CreateDirectory(_data);
        File.Copy(Path.Combine(Repository.Root, "cases", "fixtures", "connected.json"), Path.Combine(_data, "sessions.json"));

        AutomationElement window = Launch();

        // The server's key, trusted for this connection only, as connected.cases.json does.
        Invoke(Until(() => Anywhere(new AndCondition(
                   new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                   new PropertyCondition(AutomationElement.AutomationIdProperty, "once"))), "the host-key question"));

        Until(() => window.FindFirst(TreeScope.Descendants, new AndCondition(
                  new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                  new PropertyCondition(AutomationElement.ItemStatusProperty, "Connected"))), "a connected tab");

        // The file channel opens in the background once the shell is up (QS219), and a browser opened
        // before it has no remote side for good, so it is given a moment first.
        Thread.Sleep(3000);

        // The browser, the way a user opens it: the palette, typed into.
        Click(Centre(window));
        Chord(VkControl, VkShift, 'P');
        Thread.Sleep(500);
        Type("Browse files");
        Thread.Sleep(300);
        Chord(VkReturn);

        AutomationElement path = Until(() => Anywhere(RemotePath), "the browser's path box on the host's side");

        AutomationElement browser = WindowOf(path);
        string host = path.Current.Name["Path on ".Length..];

        // To the folder the file was put in, typed as Windows OpenSSH spells a path: /C:/Users/...
        ((ValuePattern)path.GetCurrentPattern(ValuePattern.Pattern)).SetValue("/" + _served.Replace('\\', '/'));
        path.SetFocus();
        Chord(VkReturn);

        // Quoted by both waits below (QS299): one red here said only that the list never came, which
        // cannot tell a Return that went to another window from a listing the browser refused.
        string PathBox() => ((ValuePattern)path.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

        string Words() => string.Join(" | ", browser.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
            .Select(each => each.Current.Name).Where(name => name.Length > 12).Distinct());

        AutomationElement list = Until(() => browser.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Files on " + host)), "the host's file list",
            () => $"the path box holds '{PathBox()}'; the browser holds: {Names(browser, 20)}; the browser's words: {Words()}");

        AutomationElement row = Until(() => list.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "qs188.bin")), "qs188.bin listed on the host's side",
            () => $"the path box holds '{PathBox()}'; the host's list shows: {Names(list, 20)}; the browser's words: {Words()}");

        // Explorer on the landing folder, beside the browser rather than under it.
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_landing}\"") { UseShellExecute = true })?.Dispose();

        string title = Path.GetFileName(_landing);

        // Found by its title holding the folder's name: Windows 11 may add to the title, and opens a
        // folder as a tab of a window already open.
        Condition cabinet = new PropertyCondition(AutomationElement.ClassNameProperty, "CabinetWClass");

        _explorer = Until(() => AutomationElement.RootElement.FindAll(TreeScope.Children, cabinet).Cast<AutomationElement>()
                                    .FirstOrDefault(each => each.Current.Name.Contains(title, StringComparison.OrdinalIgnoreCase)),
                          "an Explorer window on the landing folder",
                          () => "Explorer windows open: " + string.Join(" | ", AutomationElement.RootElement
                                    .FindAll(TreeScope.Children, cabinet).Cast<AutomationElement>().Select(each => $"'{each.Current.Name}'")));

        int width = GetSystemMetrics(0);
        int height = GetSystemMetrics(1);

        Place(browser, 0, 0, width / 2, height - 60);
        Place(_explorer, width / 2, 0, width / 2, height - 60);
        Thread.Sleep(500);

        AutomationElement folderView = Until(() => _explorer.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, "UIItemsView"))
            ?? _explorer.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List)),
            "Explorer's folder view");

        // The gesture: pressed on the row, past the threshold, across, released over the folder.
        (int fromX, int fromY) = Centre(row);
        (int toX, int toY) = Centre(folderView);

        MoveTo(fromX, fromY);
        Thread.Sleep(150);
        Button(LeftDown);
        Thread.Sleep(150);

        for (int step = 1; step <= 4; step++)
        {
            MoveTo(fromX + step * 4, fromY + step * 2);
            Thread.Sleep(40);
        }

        for (int step = 1; step <= 30; step++)
        {
            MoveTo(fromX + (toX - fromX) * step / 30, fromY + (toY - fromY) * step / 30);
            Thread.Sleep(30);
        }

        Thread.Sleep(400);
        Button(LeftUp);

        string landed = Path.Combine(_landing, "qs188.bin");
        byte[]? arrived = null;
        Stopwatch waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience)
        {
            try
            {
                if (File.Exists(landed) && new FileInfo(landed).Length == payload.Length)
                {
                    arrived = File.ReadAllBytes(landed);

                    break;
                }
            }
            catch (IOException)
            {
                // Explorer is still writing it.
            }

            Thread.Sleep(200);
        }

        Assert.True(arrived is not null,
            $"nothing of {payload.Length} bytes landed at {landed} within {Patience.TotalSeconds:0}s of the drop; "
            + $"the folder holds: {string.Join(", ", Directory.GetFiles(_landing).Select(Path.GetFileName))}");

        Assert.Equal(payload, arrived);
    }

    private static readonly Condition RemotePath = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new NotCondition(new PropertyCondition(AutomationElement.NameProperty, "Path on This computer")),
        new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));

    private AutomationElement Launch()
    {
        string configuration = typeof(ExplorerDropRun).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>()
            .Select(one => one.Configuration)
            .FirstOrDefault() ?? "Debug";

        string executable = Path.Combine(Repository.Root, "src", "Quickshell.App", "bin", configuration,
                                         "net10.0-windows", "win-x64", "quickshell.exe");

        ProcessStartInfo start = new(executable, "--session probe")
        {
            UseShellExecute = false,
            WorkingDirectory = Repository.Root,
        };

        start.Environment["QUICKSHELL_DATA"] = _data;

        _client = Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start");

        return Until(() => Window(w => w.Current.Name.Length > 0), "the client's window");
    }

    /// <summary>A top-level window of the client's that satisfies <paramref name="which"/>.</summary>
    private AutomationElement? Window(Func<AutomationElement, bool> which)
    {
        AutomationElementCollection windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, _client!.Id));

        foreach (AutomationElement each in windows)
        {
            try
            {
                if (which(each))
                {
                    return each;
                }
            }
            catch (ElementNotAvailableException)
            {
                // Closed while it was being looked at.
            }
        }

        return null;
    }

    /// <summary>
    /// The first element of the client's matching <paramref name="condition"/>, in any of its windows —
    /// an owned window such as the host-key question or the browser can sit under its owner in the
    /// tree as well as beside it.
    /// </summary>
    private AutomationElement? Anywhere(Condition condition)
    {
        AutomationElementCollection windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, _client!.Id));

        foreach (AutomationElement each in windows)
        {
            if (each.FindFirst(TreeScope.Element | TreeScope.Descendants, condition) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The nearest window holding <paramref name="element"/>.</summary>
    private static AutomationElement WindowOf(AutomationElement element)
    {
        TreeWalker walker = TreeWalker.ControlViewWalker;
        AutomationElement at = element;

        while (walker.GetParent(at) is { } parent && parent != AutomationElement.RootElement)
        {
            at = parent;

            if (at.Current.ControlType == ControlType.Window)
            {
                return at;
            }
        }

        return at;
    }

    /// <summary>The names of the first few elements under <paramref name="root"/>, for a failure to quote.</summary>
    private static string Names(AutomationElement root, int most) =>
        string.Join(" | ", root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
            .Select(each => $"{each.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")} '{each.Current.Name}'")
            .Where(each => !each.EndsWith("''", StringComparison.Ordinal))
            .Take(most));

    private static T Until<T>(Func<T?> found, string what, Func<string>? saw = null) where T : class
    {
        Stopwatch waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience)
        {
            try
            {
                if (found() is { } it)
                {
                    return it;
                }
            }
            catch (ElementNotAvailableException)
            {
                // The tree moved under the search; ask again.
            }

            Thread.Sleep(100);
        }

        Assert.Fail($"{what} did not appear within {Patience.TotalSeconds:0}s{(saw is null ? "" : "; " + saw())}");

        return null!;
    }

    private static void Invoke(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static void Place(AutomationElement window, int x, int y, int width, int height)
    {
        if (window.TryGetCurrentPattern(WindowPattern.Pattern, out object? state)
            && ((WindowPattern)state).Current.WindowVisualState != WindowVisualState.Normal)
        {
            ((WindowPattern)state).SetWindowVisualState(WindowVisualState.Normal);
        }

        TransformPattern moving = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);

        moving.Move(x, y);
        moving.Resize(width, height);
    }

    private static (int X, int Y) Centre(AutomationElement element)
    {
        System.Windows.Rect bounds = element.Current.BoundingRectangle;

        return ((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
    }

    // ---- real input, on the guest's desk ----

    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkReturn = 0x0D;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;

    private static void Click((int X, int Y) at)
    {
        MoveTo(at.X, at.Y);
        Button(LeftDown);
        Button(LeftUp);
        Thread.Sleep(300);
    }

    private static void MoveTo(int x, int y)
    {
        int left = GetSystemMetrics(76);
        int top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78);
        int height = GetSystemMetrics(79);

        Send(new Input
        {
            Type = InputMouse,
            Mouse = new MouseInput
            {
                X = (int)((x - left) * 65535L / Math.Max(1, width - 1)),
                Y = (int)((y - top) * 65535L / Math.Max(1, height - 1)),
                Flags = MouseMove | MouseAbsolute | MouseVirtualDesk,
            },
        });
    }

    private static void Button(uint flags) => Send(new Input { Type = InputMouse, Mouse = new MouseInput { Flags = flags } });

    /// <summary>The keys held in order, then released in reverse — a chord, or one key alone.</summary>
    private static void Chord(params ushort[] keys)
    {
        foreach (ushort key in keys)
        {
            Send(new Input { Type = InputKeyboard, Keyboard = new KeyboardInput { Key = key } });
        }

        foreach (ushort key in keys.Reverse())
        {
            Send(new Input { Type = InputKeyboard, Keyboard = new KeyboardInput { Key = key, Flags = KeyUp } });
        }

        Thread.Sleep(100);
    }

    private static void Type(string text)
    {
        foreach (char letter in text)
        {
            Send(new Input { Type = InputKeyboard, Keyboard = new KeyboardInput { Scan = letter, Flags = KeyUnicode } });
            Send(new Input { Type = InputKeyboard, Keyboard = new KeyboardInput { Scan = letter, Flags = KeyUnicode | KeyUp } });
            Thread.Sleep(20);
        }
    }

    private static void Send(Input input)
    {
        Input[] one = [input];

        Assert.True(SendInput(1, one, Marshal.SizeOf<Input>()) == 1,
            $"the desk refused synthesised input (error {Marshal.GetLastPInvokeError()})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
