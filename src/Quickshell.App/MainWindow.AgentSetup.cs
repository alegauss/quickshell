using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Input;

using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// The guided setup of an agent account on a server (QS340), from the session's menu: the person's own tab
/// on it, a key made for the agent, the exact script shown, run there under the person's sudo, and the
/// identity recorded on the session once it ran.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Asks what to set up, given the dialog built for the server, and answers whether to run it. The dialog,
    /// unless a caller says otherwise — which is how a test answers it.
    /// </summary>
    public Func<AgentSetupDialog, bool>? AsksAgentSetup { get; set; }

    /// <summary>Where agent keys are made: <see cref="Locations.AgentKeys"/> unless a caller says otherwise.</summary>
    public string? AgentKeysFolder { get; set; }

    /// <summary>The policy the list starts from: <see cref="Locations.AgentPolicy"/> unless a caller says otherwise.</summary>
    public string? AgentPolicyFile { get; set; }

    /// <summary>Where the setup is written in the audit: <see cref="Locations.Audit"/> unless a caller says otherwise.</summary>
    public string? AuditFolder { get; set; }

    /// <summary>How long the person has to answer sudo in their tab before the setup stops waiting.</summary>
    public TimeSpan AgentSetupWaits { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary><see cref="SetUpAgentAccount"/>, started from the menu and left to say how it went.</summary>
    public void SetUpAgentAccountOrSay(string path) => _ = SetUpAgentAccount(path);

    /// <summary>
    /// Sets up an agent account on the server of the session at <paramref name="path"/>, and records it on
    /// the session. Every way it stops short is said, and leaves the session as it was.
    /// </summary>
    /// <returns>Whether the identity was recorded.</returns>
    public async Task<bool> SetUpAgentAccount(string path)
    {
        string? made = null;

        try
        {
            ResolvedSession session = SessionTree.ReadFrom(StoreFile).Session(path)
                                      ?? throw new AgentSetupRefusal($"{path} is not a saved session.");

            // The person's own tab on the server, which the script then runs in, under their sudo.
            List<TerminalTab> before = [.. Held];

            ConnectSession(path);

            TerminalLeaf leaf = Held.Except(before).FirstOrDefault()?.Focused
                                ?? throw new AgentSetupRefusal($"{path} did not open in a tab, so there is nowhere to run the setup.");

            ICommandChannel person = await Connected(leaf).ConfigureAwait(true);

            if (CommandPrompts.IsWindows(person.ServerVersion))
            {
                throw new AgentSetupRefusal($"{session.Host} is a Windows server. The agent account setup writes sudoers, so it needs a POSIX server.");
            }

            string from = await AgentAccountSetup.SeenFromAsync(person, CancellationToken.None).ConfigureAwait(true);
            string key = AgentAccountSetup.KeyFor(AgentKeysFolder ?? Locations.Current.AgentKeys, path);

            if (await Task.Run(() => AgentAccountSetup.MakeKey(key)).ConfigureAwait(true))
            {
                made = key;
            }

            // quickshell's authority, made the first time a server is set up (QS341).
            AgentAuthority authority = new(AgentKeysFolder ?? Locations.Current.AgentKeys);
            string authorityKey = await Task.Run(authority.PublicKey).ConfigureAwait(true);

            AgentSetupDialog dialog = new(session.Host, await File.ReadAllTextAsync(key + ".pub").ConfigureAwait(true), from,
                                          AgentPolicy.Load(AgentPolicyFile ?? Locations.Current.AgentPolicy).GrantedAsRoot(session),
                                          authorityKey);

            bool run = (AsksAgentSetup ?? (shown =>
            {
                shown.Owner = this;

                return shown.ShowDialog() == true;
            }))(dialog);

            if (!run || dialog.Plan is not { } plan)
            {
                return false;
            }

            string account = dialog.Account;

            await AgentAccountSetup.StageAsync(person, plan, CancellationToken.None).ConfigureAwait(true);

            leaf.Typist.Type(AgentAccountSetup.Typed, ModifierKeys.None);
            Focus();

            if (!await Ran(person).ConfigureAwait(true))
            {
                Say($"The setup on {session.Host} did not finish in your tab, so the session is unchanged: sudo was not answered, "
                    + "or the script stopped. The tab says which.", "Agent account");

                return false;
            }

            AgentAccountSetup.Record(SessionTree.ReadFrom(StoreFile), path, account, key, dialog.UsesCertificate).WriteTo(StoreFile);
            made = null;
            Audited(session, account, from, plan);
            RefreshSessions();

            Say($"The agent account {account} is set up on {session.Host}. The agent gateway now signs in to {path} as {account}, "
                + $"with the key in {key}. Root on that server is limited to the list you saw.", "Agent account");

            return true;
        }
        catch (AgentSetupRefusal refused)
        {
            Say(refused.Message, "Agent account");
        }
        catch (SessionStoreException unreadable)
        {
            Sessionally($"{unreadable.Message}\n\n{unreadable.Means}\n\n{unreadable.Remedy}".Trim());
        }
        catch (Exception failed) when (failed is IOException or SshException or TimeoutException)
        {
            Say($"The agent account setup stopped: {failed.Message}", "Agent account");
        }
        finally
        {
            // A key made for a setup that did not finish is not one the agent should keep.
            if (made is not null)
            {
                AgentAccountSetup.ForgetKey(made);
            }
        }

        return false;
    }

    /// <summary>The pane's connection once it is up, as a command channel beside its shell.</summary>
    private static async Task<ICommandChannel> Connected(TerminalLeaf leaf)
    {
        DateTime giveUp = DateTime.UtcNow.AddMinutes(2);

        while (DateTime.UtcNow < giveUp)
        {
            if (leaf.Transport is ICommandChannel { ServerVersion: not null } channel)
            {
                return channel;
            }

            if (leaf.Ended is not null)
            {
                throw new AgentSetupRefusal($"The tab to {leaf.Host} closed before it connected, so the setup has nowhere to run.");
            }

            await Task.Delay(250).ConfigureAwait(true);
        }

        throw new AgentSetupRefusal($"The tab to {leaf.Host} did not connect within two minutes, so the setup did not start.");
    }

    /// <summary>Whether the typed line ran the script to the end, asked every two seconds until the wait runs out.</summary>
    private async Task<bool> Ran(ICommandChannel person)
    {
        DateTime giveUp = DateTime.UtcNow + AgentSetupWaits;

        while (DateTime.UtcNow < giveUp)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);

            if (await AgentAccountSetup.ReadyAsync(person, CancellationToken.None).ConfigureAwait(true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The setup, as one line of an audit run of its own, beside the gateway's.</summary>
    private void Audited(ResolvedSession session, string account, string from, AgentSetupPlan plan)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        AuditLog.Start(AuditFolder ?? Locations.Current.Audit, now).Record(
            now, "agent_setup", session,
            new JsonObject
            {
                ["host"] = session.Path,
                ["account"] = account,
                ["from"] = from,
                ["certificate"] = plan.Script.Contains("cert-authority,", StringComparison.Ordinal),
                ["script"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Script))),
            },
            new JsonObject { ["exitCode"] = 0 }, failed: false, approval: "the person, with their sudo in their own tab");
    }
}
