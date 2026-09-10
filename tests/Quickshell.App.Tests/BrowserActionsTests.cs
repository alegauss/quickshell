using System.IO;
using Quickshell.App;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS60's second half: the operations between the panes, against this machine's disk and a real
/// server.
///
/// <para><b>The task's own criterion is the first test.</b> <em>Every operation between the panes
/// works on a real server, and a delete asks.</em> So one run against the fixture copies both ways
/// — a file and a tree with an empty directory in it — renames, makes a directory, changes a mode
/// and deletes, and reads the server back after each rather than trusting what the pane says.</para>
/// </summary>
public sealed class BrowserActionsTests
{
    private static CancellationToken Stop => TestContext.Current.CancellationToken;

    /// <summary>
    /// Copy up, copy down, rename, make, change a mode, delete — each checked on the server itself.
    /// </summary>
    [Fact]
    public async Task EveryOperationBetweenThePanesWorksOnARealServer()
    {
        SshFixture.SkipWithoutIt();

        string remote = "/tmp/qs60ops-" + Guid.NewGuid().ToString("N");
        string local = Directory.CreateTempSubdirectory("qs60ops-").FullName;

        Assert.SkipUnless(
            SshFixture.Docker($"mkdir -p {remote} && printf 'hello down' > {remote}/down.txt "
                              + $"&& chown -R probe {remote}"),
            "the fixture's container could not be asked to make the directory");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(local, "up.txt"), "hello up", Stop);
            Directory.CreateDirectory(Path.Combine(local, "tree", "empty"));
            await File.WriteAllTextAsync(Path.Combine(local, "tree", "inner.txt"), "inside", Stop);

            await using SshNetTransport session = await SshFixture.ConnectAsync(Stop);
            await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

            PanePump pump = new();
            DirectoryPane here = new(new LocalFiles(local), pump.Post);
            DirectoryPane there = new(new RemoteFiles(files, SshFixture.Title), pump.Post);

            CopyQuestion? copying = null;
            DeleteQuestion? deleting = null;

            BrowserActions actions = new(here, there, pump.Post)
            {
                AskingToCopy = question =>
                {
                    copying = question;

                    return true;
                },
                AskingToDelete = question =>
                {
                    deleting = question;

                    return true;
                },
            };

            await Settled(pump, here, here.Go(local));
            await Settled(pump, there, there.Go(remote));

            // Up: a file and a tree, from this computer's pane into the host's.
            actions.Active = here;
            here.Selected = [.. here.Items.Where(item => item.Name is "up.txt" or "tree")];

            await actions.CopyAsync(Stop);
            await Settled(pump, there, Task.CompletedTask);

            Assert.Equal($"Copy 2 entries to {remote} on {SshFixture.Title}?", copying?.Asking);
            Assert.Equal(8, (await files.StatAsync(remote + "/up.txt", Stop)).Length);
            Assert.Equal(6, (await files.StatAsync(remote + "/tree/inner.txt", Stop)).Length);
            Assert.True((await files.StatAsync(remote + "/tree/empty", Stop)).IsDirectory,
                        "the empty directory inside the tree did not survive the copy");
            Assert.StartsWith("Copied 2 files", there.Note, StringComparison.Ordinal);

            // Down: a file from the host's pane into this computer's.
            actions.Active = there;
            there.Selected = [.. there.Items.Where(item => item.Name == "down.txt")];

            await actions.CopyAsync(Stop);

            Assert.Equal("hello down", await File.ReadAllTextAsync(Path.Combine(local, "down.txt"), Stop));

            // Rename, on the server.
            there.Selected = [.. there.Items.Where(item => item.Name == "up.txt")];
            actions.AskingForName = _ => "renamed.txt";

            await actions.RenameAsync(Stop);
            await Settled(pump, there, Task.CompletedTask);

            Assert.Equal(8, (await files.StatAsync(remote + "/renamed.txt", Stop)).Length);
            Assert.Equal("Renamed up.txt to renamed.txt.", there.Note);

            // A new directory, on the server.
            actions.AskingForName = _ => "made";

            await actions.CreateDirectoryAsync(Stop);
            await Settled(pump, there, Task.CompletedTask);

            Assert.True((await files.StatAsync(remote + "/made", Stop)).IsDirectory);

            // A mode, on the server, read back off the listing.
            there.Selected = [.. there.Items.Where(item => item.Name == "renamed.txt")];

            ModeQuestion? asked = null;

            actions.AskingForMode = question =>
            {
                asked = question;

                return 0b_110_000_000;
            };

            await actions.ChangeModeAsync(Stop);
            await Settled(pump, there, Task.CompletedTask);

            // What the server made the file with is its umask's business; what the question owes is
            // that mode, read off the listing, in the three digits the box expects.
            Assert.Matches("^[0-7]{3}$", asked?.Current ?? string.Empty);
            Assert.False(asked?.OnlyWritable);
            Assert.Equal("-rw-------", there.Items.Single(item => item.Name == "renamed.txt").Permissions);

            // A delete that is refused removes nothing.
            there.Selected = [.. there.Items.Where(item => item.Name is "tree" or "renamed.txt")];
            actions.AskingToDelete = _ => false;

            await actions.DeleteAsync(Stop);

            Assert.Equal(8, (await files.StatAsync(remote + "/renamed.txt", Stop)).Length);

            // And one that is agreed to takes the tree with everything in it, having said so.
            actions.AskingToDelete = question =>
            {
                deleting = question;

                return true;
            };

            await actions.DeleteAsync(Stop);
            await Settled(pump, there, Task.CompletedTask);

            Assert.Equal($"Delete 2 entries from {remote}? One of them is a directory, and everything "
                         + "in it goes too.", deleting?.Asking);
            Assert.Equal(["down.txt", "made"], [.. there.Items.Select(item => item.Name).Order()]);
            Assert.Equal("Deleted 2 entries.", there.Note);
        }
        finally
        {
            SshFixture.Docker($"rm -rf {remote}");
            Directory.Delete(local, recursive: true);
        }
    }

    /// <summary>
    /// A name already taken on the server is asked about, and the answer given is the one kept: a
    /// skip leaves what is there exactly as it was.
    /// </summary>
    [Fact]
    public async Task ANameAlreadyTakenIsAskedAboutAndASkipLeavesItAlone()
    {
        SshFixture.SkipWithoutIt();

        string remote = "/tmp/qs60clash-" + Guid.NewGuid().ToString("N");
        string local = Directory.CreateTempSubdirectory("qs60clash-").FullName;

        Assert.SkipUnless(
            SshFixture.Docker($"mkdir -p {remote} && printf 'theirs' > {remote}/same.txt && chown -R probe {remote}"),
            "the fixture's container could not be asked to make the directory");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(local, "same.txt"), "mine, and longer", Stop);

            await using SshNetTransport session = await SshFixture.ConnectAsync(Stop);
            await using IFileTransferChannel files = await session.OpenFileTransferAsync(Stop);

            PanePump pump = new();
            DirectoryPane here = new(new LocalFiles(local), pump.Post);
            DirectoryPane there = new(new RemoteFiles(files, SshFixture.Title), pump.Post);

            Collision? met = null;

            BrowserActions actions = new(here, there, pump.Post)
            {
                AskingToCopy = _ => true,
                OnCollision = (collision, _) =>
                {
                    met = collision;

                    return ValueTask.FromResult(new CollisionChoice(CollisionAnswer.Skip));
                },
            };

            await Settled(pump, here, here.Go(local));
            await Settled(pump, there, there.Go(remote));

            here.Selected = [.. here.Items];

            await actions.CopyAsync(Stop);
            await Settled(pump, there, Task.CompletedTask);

            Assert.Equal(remote + "/same.txt", met?.Path);
            Assert.Equal(6, (await files.StatAsync(remote + "/same.txt", Stop)).Length);
            Assert.Equal("Copied 0 files, 1 left alone.", there.Note);
        }
        finally
        {
            SshFixture.Docker($"rm -rf {remote}");
            Directory.Delete(local, recursive: true);
        }
    }

    /// <summary>
    /// This computer's side: rename, a new directory, read-only by mode, and a delete of a tree —
    /// and no copy at all where the tab has no remote side.
    /// </summary>
    [Fact]
    public async Task ThisComputersSideRenamesMakesProtectsAndDeletes()
    {
        string local = Directory.CreateTempSubdirectory("qs60local-").FullName;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(local, "a.txt"), "a", Stop);
            Directory.CreateDirectory(Path.Combine(local, "tree", "deeper"));
            await File.WriteAllTextAsync(Path.Combine(local, "tree", "deeper", "b.txt"), "b", Stop);

            PanePump pump = new();
            DirectoryPane here = new(new LocalFiles(local), pump.Post);
            bool askedToCopy = false;

            BrowserActions actions = new(here, null, pump.Post)
            {
                AskingToCopy = _ => askedToCopy = true,
                AskingToDelete = _ => true,
            };

            await Settled(pump, here, here.Go(local));

            here.Selected = [.. here.Items.Where(item => item.Name == "a.txt")];

            await actions.CopyAsync(Stop);

            Assert.False(askedToCopy, "a copy was offered with nowhere for it to go");

            actions.AskingForName = _ => "b.txt";

            await actions.RenameAsync(Stop);
            await Settled(pump, here, Task.CompletedTask);

            Assert.True(File.Exists(Path.Combine(local, "b.txt")));

            here.Selected = [.. here.Items.Where(item => item.Name == "b.txt")];
            actions.AskingForMode = question => question.OnlyWritable ? 0b_100_100_100 : null;

            await actions.ChangeModeAsync(Stop);

            Assert.True(File.GetAttributes(Path.Combine(local, "b.txt")).HasFlag(FileAttributes.ReadOnly));

            actions.AskingForMode = _ => 0b_110_100_100;

            await actions.ChangeModeAsync(Stop);

            Assert.False(File.GetAttributes(Path.Combine(local, "b.txt")).HasFlag(FileAttributes.ReadOnly));

            actions.AskingForName = _ => "new";

            await actions.CreateDirectoryAsync(Stop);

            Assert.True(Directory.Exists(Path.Combine(local, "new")));

            await Settled(pump, here, Task.CompletedTask);

            here.Selected = [.. here.Items.Where(item => item.Name == "tree")];

            await actions.DeleteAsync(Stop);

            Assert.False(Directory.Exists(Path.Combine(local, "tree")));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(local, recursive: true);
        }
    }

    /// <summary>The delete question says how many, and whether any of them is a directory.</summary>
    [Theory]
    [InlineData(new[] { "f" }, new bool[] { false }, "Delete f from /d?")]
    [InlineData(new[] { "d" }, new bool[] { true }, "Delete d from /d? It is a directory, and everything in it goes too.")]
    [InlineData(new[] { "f", "d" }, new bool[] { false, true }, "Delete 2 entries from /d? One of them is a directory, and everything in it goes too.")]
    [InlineData(new[] { "d", "e", "f" }, new bool[] { true, true, false }, "Delete 3 entries from /d? 2 of them are directories, and everything in them goes too.")]
    public void TheDeleteQuestionSaysHowManyAndWhetherAnyIsADirectory(string[] names, bool[] folders,
                                                                      string asking)
    {
        FileItem[] entries =
            [.. names.Select((name, at) => new FileItem(name, 0, folders[at], DateTimeOffset.UnixEpoch, "-", false))];

        Assert.Equal(asking, new DeleteQuestion(entries, "/d").Asking);
    }

    /// <summary>A mode string's permission bits are read back as the three octal digits a server writes.</summary>
    [Theory]
    [InlineData("-rwxr-x---", "750")]
    [InlineData("drwxr-xr-x", "755")]
    [InlineData("-rw-------", "600")]
    [InlineData("-rw", "")]
    public void AModeStringIsReadAsOctal(string permissions, string octal) =>
        Assert.Equal(octal, BrowserActions.Octal(permissions));

    /// <summary>Waits for a navigation, and for everything the pane was sent, to settle.</summary>
    private static async Task Settled(PanePump pump, DirectoryPane pane, Task navigating)
    {
        await navigating.WaitAsync(TimeSpan.FromSeconds(30), Stop);

        // What an operation posted — a note and a relisting — lands only when the thread is drained,
        // and the relisting it starts has to finish in its turn.
        pump.Drain();

        await pump.DrainUntil(() => !pane.Loading, TimeSpan.FromSeconds(30));
    }
}
