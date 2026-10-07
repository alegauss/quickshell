using System.Globalization;
using System.IO;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// Files dropped onto an SSH terminal with Shift held, sent into the directory the shell says it is
/// in rather than typed as paths (QS189).
///
/// <para><b>Only where the shell has said where it is.</b> The directory is the emulator's reading of
/// OSC 7 on this session's own host, which QS184 follows for the same reason. Where nothing has been
/// reported the drop sends nothing and says why: the account's home is the likeliest wrong directory
/// there is, because a file landing there is one nobody notices.</para>
///
/// <para><b>Over the session's own connection</b>, a file channel opened for the drop and closed
/// after it, never a second login (QS59). A name already taken on the server is left alone, as an
/// unattended copy leaves it: a drop is not a place to ask whether to overwrite somebody's file.</para>
/// </summary>
public static class DropSend
{
    /// <summary>Sends dropped files into the shell's directory, and says what came of it.</summary>
    /// <param name="transport">The pane's SSH connection, or null where the pane is a local shell.</param>
    /// <param name="reported">What OSC 7 last set in the pane.</param>
    /// <param name="host">What the session connected to.</param>
    /// <param name="paths">What was dropped, as this computer's paths.</param>
    /// <param name="cancellationToken">Stops the transfer.</param>
    /// <returns>The sentence the window shows.</returns>
    public static async Task<string> SendAsync(ISshTransport? transport, string reported, string host,
                                               IReadOnlyList<string> paths,
                                               CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reported);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(paths);

        if (transport is null)
        {
            return "Nothing was sent: this pane is not an SSH session. Drop without Shift to type the paths.";
        }

        if (ShellDirectory.On(reported, host) is not { } directory)
        {
            return "Nothing was sent: the shell has not said which directory it is in. A shell reports it "
                   + "with OSC 7 from its prompt; drop without Shift to type the paths instead.";
        }

        try
        {
            IFileTransferChannel channel = await transport.OpenFileTransferAsync(cancellationToken)
                                                          .ConfigureAwait(false);

            await using (channel.ConfigureAwait(false))
            {
                return await SendAsync(channel, directory, paths, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            return $"Nothing was sent to {directory}: {failed.Message}";
        }
    }

    /// <summary>Sends dropped files into a directory over a channel that is already open.</summary>
    /// <returns>The sentence the window shows.</returns>
    public static async Task<string> SendAsync(IFileTransferChannel channel, string directory,
                                               IReadOnlyList<string> paths,
                                               CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(paths);

        TransferQueue queue = new(channel);
        List<string> left = [];

        foreach (string path in paths)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            string target = directory.EndsWith('/') ? directory + name : directory + "/" + name;

            if (Directory.Exists(path))
            {
                TransferPlan plan = TransferPlan.ToCopyUp(path, target);

                await plan.EnqueueAsync(queue, channel, cancellationToken).ConfigureAwait(false);

                left.AddRange(plan.Skipped);
            }
            else
            {
                queue.Enqueue(TransferDirection.Upload, path, target);
            }
        }

        await queue.RunAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TransferEntry> entries = queue.Entries;
        int sent = entries.Count(entry => entry.State == TransferState.Done);
        int kept = entries.Count(entry => entry.State == TransferState.Skipped) + left.Count;
        TransferEntry? failed = entries.FirstOrDefault(entry => entry.State == TransferState.Failed);

        string said = $"Sent {sent.ToString("N0", CultureInfo.InvariantCulture)} "
                      + (sent == 1 ? "file" : "files") + $" to {directory}";

        if (kept > 0)
        {
            said += $"; {kept.ToString("N0", CultureInfo.InvariantCulture)} already there, left alone";
        }

        if (failed is not null)
        {
            said += $"; {Path.GetFileName(failed.From)} failed: {failed.Why}";
        }

        return said + ".";
    }
}
