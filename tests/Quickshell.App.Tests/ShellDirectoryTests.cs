using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS184: the remote pane opens where the session's shell has said it is, and goes where it goes
/// until the user moves it themselves.
/// </summary>
public sealed class ShellDirectoryTests
{
    /// <summary>The URL's host decides whether its path is this session's to follow.</summary>
    [Theory]
    [InlineData("file://web1.example.com/srv/app", "web1.example.com", "/srv/app")]
    [InlineData("file://WEB1/srv/app", "web1.example.com", "/srv/app")]
    [InlineData("file://web1.example.com/srv/app", "web1", "/srv/app")]
    [InlineData("file://web1/srv/app", "deploy@web1.example.com:2222", "/srv/app")]
    [InlineData("file:///srv/app", "web1.example.com", "/srv/app")]
    [InlineData("/srv/app", "web1.example.com", "/srv/app")]
    [InlineData("file://web1/srv/my%20app", "web1", "/srv/my app")]
    [InlineData("file://db2/var/lib", "web1.example.com", null)]
    [InlineData("file://web1.other.net/srv/app", "web1.example.com", null)]
    [InlineData("file://10/srv/app", "10.0.0.5", null)]
    [InlineData("file://web1", "web1", null)]
    [InlineData("", "web1", null)]
    [InlineData("https://web1/srv", "web1", null)]
    public void APathIsFollowedOnlyOnTheSessionsOwnHost(string reported, string host, string? followed)
    {
        Assert.Equal(followed, ShellDirectory.On(reported, host));
    }

    /// <summary>A pane follows the shell until the user navigates it, and then stays where they put it.</summary>
    [Fact]
    public async Task APaneFollowsTheShellUntilTheUserMovesIt()
    {
        PanePump pump = new();
        DirectoryPane pane = new(new Empty(), pump.Post);

        await pane.Start("/srv/app");
        Assert.Equal("/srv/app", pane.Path);

        await pane.Follow("/srv/app/releases");
        Assert.Equal("/srv/app/releases", pane.Path);

        // Following is not one of the user's moves, so there is nothing for Back to undo.
        Assert.False(pane.CanGoBack);

        await pane.Go("/etc");
        await pane.Follow("/var/log");

        Assert.Equal("/etc", pane.Path);
        Assert.True(pane.Steered);
    }

    /// <summary>A shell that has reported nothing leaves the pane at the account's home.</summary>
    [Fact]
    public async Task NoReportIsHome()
    {
        DirectoryPane pane = new(new Empty(), new PanePump().Post);

        await pane.Start(null);
        await pane.Follow(null);

        Assert.Equal("/home/deploy", pane.Path);
    }

    /// <summary>
    /// The falsification, through the window: a shell that has reported a directory of the session's
    /// own host opens the browser there and not at home, and a later report moves it.
    /// </summary>
    [Fact]
    public void TheBrowserOpensWhereTheShellIsAndFollowsIt()
    {
        (string opened, string followed, string elsewhere) = Sta.Run(() =>
        {
            MainWindow window = new() { ShowsBrowser = _ => { }, RemoteFiles = _ => new Empty() };
            TerminalTab tab = TerminalTab.Open(Settings.Default, Shared, "web1.example.com");

            window.Add(tab);

            Report(tab, "file://web1/srv/app");

            FileBrowser browser = window.BrowseFiles();

            // What showing it does: the panes list once there is a window to list into.
            browser.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            string first = browser.Remote!.Path;

            Report(tab, "file://web1/srv/app/current");
            browser.FollowShell();

            string second = browser.Remote.Path;

            // A nested ssh reporting another machine's directory is not followed onto this one.
            Report(tab, "file://db2/var/lib");
            browser.FollowShell();

            return (first, second, browser.Remote.Path);
        });

        Assert.Equal("/srv/app", opened);
        Assert.Equal("/srv/app/current", followed);
        Assert.Equal("/srv/app/current", elsewhere);
    }

    private static void Report(TerminalTab tab, string url) =>
        tab.Focused.Emulator.Feed(Encoding.UTF8.GetBytes("\u001b]7;" + url + "\u0007"));

    /// <summary>The one device, atlas and render loop the tab in the window test would draw with.</summary>
    private static readonly TerminalShare Shared = new();

    /// <summary>A remote side whose every directory is empty, and whose home is the account's.</summary>
    private sealed class Empty : IFileSide
    {
        public string Title => "web1.example.com";

        public string Home => "/home/deploy";

        public async IAsyncEnumerable<FileItem> ListAsync(
            string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            yield break;
        }

        public string Into(string directory, string name) => directory.TrimEnd('/') + "/" + name;

        public string? Parent(string path) => null;

        public Task RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(FileItem entry, string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ChangeModeAsync(string path, int mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
