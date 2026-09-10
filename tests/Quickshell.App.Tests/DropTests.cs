using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Quickshell.App;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS64: files dragged from Explorer onto a terminal or onto the browser.
///
/// <para><b>The falsification is about quoting, and it is checked three ways.</b> <em>Falsified when
/// a path typed into a terminal by a drop is not quoted for the shell.</em> So the quoting itself is
/// read for each shell with the names that break a careless quote, the pane's own drop message is
/// sent to a real pane with a drop Windows could have built, and what reaches the host is compared
/// byte for byte.</para>
/// </summary>
public sealed class DropTests
{
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>The one device, atlas and render loop the drop tests' panes would draw with.</summary>
    private static readonly TerminalShare Shared = new();

    /// <summary>
    /// Every path comes out as one word of the shell it is typed into, including the names that
    /// break a careless quote: a space, an apostrophe, a dollar and a backtick.
    /// </summary>
    [Theory]
    [InlineData(ShellKind.Cmd, @"C:\Users\joão\My Files\a b.txt", "\"C:\\Users\\joão\\My Files\\a b.txt\"")]
    [InlineData(ShellKind.PowerShell, @"C:\it's here\$HOME.txt", @"'C:\it''s here\$HOME.txt'")]
    [InlineData(ShellKind.Posix, "/home/probe/it's $HOME and `ls`", @"'/home/probe/it'\''s $HOME and `ls`'")]
    public void APathIsOneWordOfTheShellItIsTypedInto(ShellKind shell, string path, string word) =>
        Assert.Equal(word, ShellQuoting.Quote(path, shell));

    /// <summary>The shell is told from what the pane runs.</summary>
    [Theory]
    [InlineData(@"C:\WINDOWS\system32\cmd.exe", ShellKind.Cmd)]
    [InlineData("cmd.exe", ShellKind.Cmd)]
    [InlineData("pwsh.exe", ShellKind.PowerShell)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ShellKind.PowerShell)]
    [InlineData("prod-db", ShellKind.Posix)]
    public void TheShellIsToldFromWhatThePaneRuns(string program, ShellKind shell) =>
        Assert.Equal(shell, ShellQuoting.Of(program));

    /// <summary>
    /// A drop built the way Windows builds one, sent to a real pane: the paths arrive at the pane's
    /// host quoted for its shell, one argument each and a space after the last.
    ///
    /// <para><b>A real pane with a real handle, and the message it is sent is the one Explorer
    /// sends.</b> What is not exercised is Explorer deciding to send it, which needs a person and a
    /// mouse; everything after that — the window accepting files, the drop being read and given back,
    /// the typing — is.</para>
    /// </summary>
    [Fact]
    public void FilesDroppedOnATerminalAreTypedAsQuotedPaths()
    {
        (string typed, bool focused) = OnShownTab(window =>
        {
            TerminalTab tab = window.Current!;

            // Split, so the drop lands on a pane that does not have the keyboard.
            window.SplitPane(Divide.Beside);
            window.UpdateLayout();

            TerminalLeaf target = tab.Leaves[0];
            StringBuilder heard = new();

            target.Typist.Sending = bytes =>
            {
                heard.Append(Encoding.UTF8.GetString(bytes.Span));

                return ValueTask.CompletedTask;
            };

            Assert.True(target.Pane.PaneHandle != nint.Zero, "the pane never built a handle");

            nint drop = Drop([@"C:\Users\joão\My Files\a b.txt", @"C:\temp\plain.txt"]);

            SendMessageW(target.Pane.PaneHandle, DropFiles, drop, nint.Zero);

            return (heard.ToString(), ReferenceEquals(tab.Focused, target));
        });

        Assert.Equal("\"C:\\Users\\joão\\My Files\\a b.txt\" \"C:\\temp\\plain.txt\" ", typed);
        Assert.True(focused, "the pane a file was dropped on did not take the keyboard");
    }

    /// <summary>
    /// Files dropped onto this computer's pane are copied into the directory it shows, and a name
    /// already there is asked about rather than overwritten.
    /// </summary>
    [Fact]
    public async Task FilesDroppedOnThisComputersPaneAreCopiedIntoItsDirectory()
    {
        string from = Directory.CreateTempSubdirectory("qs64-from-").FullName;
        string into = Directory.CreateTempSubdirectory("qs64-into-").FullName;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(from, "new.txt"), "new", Stop);
            await File.WriteAllTextAsync(Path.Combine(from, "same.txt"), "mine", Stop);
            Directory.CreateDirectory(Path.Combine(from, "tree"));
            await File.WriteAllTextAsync(Path.Combine(from, "tree", "inner.txt"), "inside", Stop);
            await File.WriteAllTextAsync(Path.Combine(into, "same.txt"), "theirs", Stop);

            PanePump pump = new();
            DirectoryPane pane = new(new LocalFiles(into), pump.Post);
            Collision? met = null;

            BrowserActions actions = new(pane, null, pump.Post)
            {
                OnCollision = (collision, _) =>
                {
                    met = collision;

                    return ValueTask.FromResult(new CollisionChoice(CollisionAnswer.Skip));
                },
            };

            await pane.Go(into).WaitAsync(TimeSpan.FromSeconds(10), Stop);
            await pump.DrainUntil(() => !pane.Loading, TimeSpan.FromSeconds(5));

            await actions.DropAsync(pane, [Path.Combine(from, "new.txt"), Path.Combine(from, "same.txt"),
                                           Path.Combine(from, "tree")], Stop);

            pump.Drain();

            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(into, "new.txt"), Stop));
            Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(into, "tree", "inner.txt"), Stop));
            Assert.Equal("theirs", await File.ReadAllTextAsync(Path.Combine(into, "same.txt"), Stop));
            Assert.Equal(Path.Combine(into, "same.txt"), met?.Path);
            Assert.Equal("Copied 2 files, 1 left alone.", pane.Note);
        }
        finally
        {
            Directory.Delete(from, recursive: true);
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>Files dropped onto the host's pane are uploaded into the directory it shows, over the session.</summary>
    [Fact]
    public async Task FilesDroppedOnTheHostsPaneAreUploadedIntoItsDirectory()
    {
        SshFixture.SkipWithoutIt();

        string remote = "/tmp/qs64-" + Guid.NewGuid().ToString("N");
        string from = Directory.CreateTempSubdirectory("qs64-up-").FullName;

        Assert.SkipUnless(SshFixture.Docker($"mkdir -p {remote} && chown -R probe {remote}"),
                          "the fixture's container could not be asked to make the directory");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(from, "dropped.txt"), "dropped", Stop);
            Directory.CreateDirectory(Path.Combine(from, "folder", "empty"));

            await using SshNetTransport session = await SshFixture.ConnectAsync(Stop);
            await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

            PanePump pump = new();
            DirectoryPane here = new(new LocalFiles(from), pump.Post);
            DirectoryPane there = new(new RemoteFiles(files, SshFixture.Title), pump.Post);
            BrowserActions actions = new(here, there, pump.Post);

            await there.Go(remote).WaitAsync(TimeSpan.FromSeconds(10), Stop);
            await pump.DrainUntil(() => !there.Loading, TimeSpan.FromSeconds(10));

            await actions.DropAsync(there, [Path.Combine(from, "dropped.txt"), Path.Combine(from, "folder")], Stop);

            pump.Drain();

            Assert.Equal(7, (await files.StatAsync(remote + "/dropped.txt", Stop)).Length);
            Assert.True((await files.StatAsync(remote + "/folder/empty", Stop)).IsDirectory,
                        "the empty directory in the dropped folder did not arrive");
            Assert.StartsWith("Copied 1 file", there.Note, StringComparison.Ordinal);
        }
        finally
        {
            SshFixture.Docker($"rm -rf {remote}");
            Directory.Delete(from, recursive: true);
        }
    }

    /// <summary>
    /// A drop exactly as Windows hands one to a window: a DROPFILES header and the paths after it,
    /// wide, each ended by a null and the list ended by another. The pane gives it back.
    /// </summary>
    private static nint Drop(IReadOnlyList<string> paths)
    {
        const int Header = 20;
        string list = string.Join('\0', paths) + "\0\0";
        byte[] names = Encoding.Unicode.GetBytes(list);

        nint memory = GlobalAlloc(0x0042, (nuint)(Header + names.Length));
        nint at = GlobalLock(memory);

        try
        {
            Marshal.WriteInt32(at, 0, Header);   // where the names start
            Marshal.WriteInt32(at, 4, 0);        // the drop point
            Marshal.WriteInt32(at, 8, 0);
            Marshal.WriteInt32(at, 12, 0);       // in the client area
            Marshal.WriteInt32(at, 16, 1);       // wide
            Marshal.Copy(names, 0, at + Header, names.Length);
        }
        finally
        {
            GlobalUnlock(memory);
        }

        return memory;
    }

    /// <summary>Builds the client's window with one tab, shows it, and hands it over on its thread.</summary>
    private static T OnShownTab<T>(Func<MainWindow, T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            MainWindow? window = null;

            try
            {
                window = new MainWindow
                {
                    Width = 640,
                    Height = 360,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };

                window.Add(TerminalTab.Open(Settings.Default, Shared, "cmd.exe"));
                window.Show();
                window.UpdateLayout();

                result = work(window);
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                window?.Close();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the window could not be built", failed);
        }

        return result;
    }

    /// <summary>WM_DROPFILES, which is what Explorer sends a window that accepts files.</summary>
    private const uint DropFiles = 0x0233;

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nint wide, nint low);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);
}
