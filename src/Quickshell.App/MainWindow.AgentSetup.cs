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

    /// <summary>
    /// Asks whether to run the removal shown, given the server and the script; a dialog unless a caller says
    /// otherwise — which is how a test answers it.
    /// </summary>
    public Func<string, string, bool>? AsksAgentRemoval { get; set; }

    /// <summary>
    /// The answer to a gateway's request to sign an agent a certificate (QS360): Windows Hello, over this
    /// window, approves it; declined, or Hello not set up for this account, refuses it, saying which.
    /// </summary>
    public async Task<ApprovalAnswer> AnswerSigning(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        AskedCall asked = request.Calls[0];
        string message = $"An agent is signing in to {asked.Session} as {string.Join(' ', asked.Words)}. Confirm it is you.";

        HelloVerdict verdict = await (Verifies ?? (said => WindowsHello.VerifyAsync(new System.Windows.Interop.WindowInteropHelper(this).Handle, said)))(message)
            .ConfigureAwait(true);

        return verdict switch
        {
            HelloVerdict.Verified => new ApprovalAnswer(Decision.Approve, request.Hash, "confirmed with Windows Hello"),
            HelloVerdict.Unavailable => new ApprovalAnswer(Decision.Refuse, request.Hash,
                                                           "Windows Hello is not set up for this Windows account, so nobody could confirm the signing"),
            _ => new ApprovalAnswer(Decision.Refuse, request.Hash, "Windows Hello was declined at the desk"),
        };
    }

    /// <summary>Whether the session at <paramref name="path"/> names an agent account, its own or a folder's.</summary>
    public bool HasAgentAccount(string path) => _store?.Session(path)?.AgentUser is not null;

    /// <summary><see cref="RemoveAgentAccount"/>, started from the menu and left to say how it went.</summary>
    public void RemoveAgentAccountOrSay(string path) => _ = RemoveAgentAccount(path);

    /// <summary>
    /// Removes the agent account from the server of the session at <paramref name="path"/> (QS359), the way
    /// it was set up: in the person's own tab, the exact script shown first and run under their sudo. Once it
    /// ran, the session's own agent identity is taken off and its key deleted where no other session uses it.
    /// A folder's identity is left alone, and said.
    /// </summary>
    /// <returns>Whether the account was removed.</returns>
    public async Task<bool> RemoveAgentAccount(string path)
    {
        try
        {
            ResolvedSession session = SessionTree.ReadFrom(StoreFile).Session(path)
                                      ?? throw new AgentSetupRefusal($"{path} is not a saved session.");
            string account = session.AgentUser?.Value
                             ?? throw new AgentSetupRefusal($"{path} has no agent account to remove.");
            string script = AgentAccountSetup.Removal(account);

            bool remove = (AsksAgentRemoval ?? ((host, shown) => Quickshell.App.Choice.Ask(
                this, "Remove the agent account",
                $"This runs the script below on {host}, as root, in your own tab, where you answer sudo:\n\n{shown}",
                new ChoiceButton("Remove it", "remove"), new ChoiceButton("Keep it", "keep")) == "remove"))(session.Host, script);

            if (!remove)
            {
                return false;
            }

            List<TerminalTab> before = [.. Held];

            ConnectSession(path);

            TerminalLeaf leaf = Held.Except(before).FirstOrDefault()?.Focused
                                ?? throw new AgentSetupRefusal($"{path} did not open in a tab, so there is nowhere to run the removal.");

            ICommandChannel person = await Connected(leaf).ConfigureAwait(true);

            if (CommandPrompts.IsWindows(person.ServerVersion))
            {
                throw new AgentSetupRefusal($"{session.Host} is a Windows server, where no agent account was set up from here.");
            }

            await AgentAccountSetup.StageAsync(person, script, CancellationToken.None).ConfigureAwait(true);

            leaf.Typist.Type(AgentAccountSetup.Typed, ModifierKeys.None);
            Focus();

            if (!await Ran(person).ConfigureAwait(true))
            {
                Say($"The removal on {session.Host} did not finish in your tab, so the session is unchanged: sudo was not answered, "
                    + "or the script stopped. The tab says which.", "Agent account");

                return false;
            }

            // The session's own identity, and its key where nothing else names it; a folder's is the folder's.
            bool own = string.Equals(session.AgentUser?.From, path, StringComparison.OrdinalIgnoreCase);
            SessionTree store = SessionTree.ReadFrom(StoreFile);

            if (own)
            {
                store = AgentAccountSetup.Forget(store, path);
                store.WriteTo(StoreFile);

                if (session.AgentKey?.Value is { } key && !Names(store, key))
                {
                    AgentAccountSetup.ForgetKey(RemoteShell.Expand(key));
                }
            }

            AuditedRemoval(session, account);
            RefreshSessions();

            Say(own
                    ? $"The agent account {account} is removed from {session.Host}, and {path} no longer names an agent."
                    : $"The agent account {account} is removed from {session.Host}. {session.AgentUser?.From} still names it for the "
                      + "sessions under it, this one included, so the gateway will try it here until that folder is changed.",
                "Agent account");

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
            Say($"The agent account removal stopped: {failed.Message}", "Agent account");
        }

        return false;
    }

    /// <summary>Whether any session in the store still names <paramref name="key"/> as its agent key.</summary>
    private static bool Names(SessionTree store, string key)
    {
        return Walk(store.Root);

        bool Walk(SessionNode node) =>
            string.Equals(node.Settings.AgentKey, key, StringComparison.OrdinalIgnoreCase) || node.Children.Any(Walk);
    }

    /// <summary>The removal, as one line of an audit run of its own, beside the setup's.</summary>
    private void AuditedRemoval(ResolvedSession session, string account)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        AuditLog.Start(AuditFolder ?? Locations.Current.Audit, now).Record(
            now, "agent_removal", session,
            new JsonObject { ["host"] = session.Path, ["account"] = account },
            new JsonObject { ["exitCode"] = 0 }, failed: false, approval: "the person, with their sudo in their own tab");
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
