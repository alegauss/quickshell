using System.Windows;
using System.Windows.Controls;
using Quickshell.App;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// The dialog, and what it does not ask for.
///
/// <para>The tree here is the shape that makes the question real: a folder that sets four things,
/// and a session under it that sets none of them. What the dialog demands of that session is the
/// whole design.</para>
/// </summary>
public sealed class SessionEditorTests
{
    /// <summary>A fleet organised the way people organise one.</summary>
    private static SessionTree Fleet() =>
        SessionTree.Of(new SessionNode
        {
            Name = string.Empty,
            Settings = new SessionSettings { Scheme = "quickshell" },
            Children =
            [
                new SessionNode
                {
                    Name = "prod",
                    Settings = new SessionSettings
                    {
                        User = "deploy",
                        Port = 2222,
                        Key = "~/.ssh/id_prod",
                        JumpHost = "bastion.example",
                    },
                    Tags = ["production"],
                    Children =
                    [
                        new SessionNode { Name = "web", Host = "web.prod.example" },
                        new SessionNode
                        {
                            Name = "db",
                            Host = "db.prod.example",
                            Settings = new SessionSettings { User = "postgres" },
                        },
                    ],
                },
            ],
        });

    // ---- The falsification ----

    /// <summary>
    /// The line's own falsification: the dialog requires nothing the store could have inherited.
    ///
    /// <para>A host is the one thing no folder can supply. Everything else this session needs — the
    /// account, the port, the key, the bastion, the scheme — is already above it, and a dialog that
    /// asked again would be asking the user to retype what it knows.</para>
    /// </summary>
    [Fact]
    public void TheDialogRequiresNothingTheStoreCouldHaveInherited()
    {
        SessionEditor editor = SessionEditor.Creating(Fleet(), "prod", "cache");

        // Nothing filled in but the one thing a folder cannot know.
        editor.Host = "cache.prod.example";

        Assert.Empty(editor.Complaints);
        Assert.True(editor.CanSave);

        // And it is not that the fields are absent — they are there, answered from above.
        Assert.Equal("deploy", editor.Field(nameof(SessionSettings.User)).Effective);
        Assert.Equal("2222", editor.Field(nameof(SessionSettings.Port)).Effective);
        Assert.Equal("bastion.example", editor.Field(nameof(SessionSettings.JumpHost)).Effective);
        Assert.Equal("quickshell", editor.Field(nameof(SessionSettings.Scheme)).Effective);
    }

    /// <summary>And a host is genuinely required, because nothing else can stand in for it.</summary>
    [Fact]
    public void AHostIsTheOneThingItInsistsOn()
    {
        SessionEditor editor = SessionEditor.Creating(Fleet(), "prod", "cache");

        Assert.False(editor.CanSave);
        Assert.Contains(editor.Complaints, said => said.Contains("host", StringComparison.Ordinal));
    }

    // ---- Where a value came from ----

    /// <summary>
    /// An inherited value names the folder it came from, in the words the dialog shows.
    ///
    /// <para>Without this a user cannot tell why one session behaves unlike its siblings, and the
    /// only way to find out is to walk the tree.</para>
    /// </summary>
    [Fact]
    public void AnInheritedValueNamesWhereItCameFrom()
    {
        SessionEditor editor = SessionEditor.Editing(Fleet(), "prod/web");

        EditableField user = editor.Field(nameof(SessionSettings.User));

        Assert.True(user.IsInherited);
        Assert.False(user.IsOverridden);
        Assert.Equal("prod", user.Inherited!.Value.From);
        Assert.Equal("inherited from prod", user.Explains);
    }

    /// <summary>A value the session set itself says so, and does not claim a folder said it.</summary>
    [Fact]
    public void AValueSetHereSaysItWasSetHere()
    {
        SessionEditor editor = SessionEditor.Editing(Fleet(), "prod/db");

        EditableField user = editor.Field(nameof(SessionSettings.User));

        Assert.True(user.IsOverridden);
        Assert.False(user.IsInherited);
        Assert.Equal("postgres", user.Effective);
        Assert.Equal("set here", user.Explains);

        // And what it is overriding is still visible, which is what makes it an override.
        Assert.Equal("deploy", user.Inherited!.Value.Value);
    }

    /// <summary>A field nothing sets anywhere says that, rather than looking inherited.</summary>
    [Fact]
    public void AFieldNothingSetsSaysSo()
    {
        EditableField credential =
            SessionEditor.Editing(Fleet(), "prod/web").Field(nameof(SessionSettings.Credential));

        Assert.False(credential.IsInherited);
        Assert.False(credential.IsOverridden);
        Assert.Null(credential.Effective);
        Assert.Equal("not set", credential.Explains);
    }

    // ---- Saving writes what was decided, and nothing else ----

    /// <summary>
    /// Opening a session and saving it changes nothing, which is the property that keeps a folder's
    /// default one value instead of a hundred copies.
    /// </summary>
    [Fact]
    public void SavingAnUntouchedSessionWritesNoInheritedValues()
    {
        SessionTree saved = SessionEditor.Editing(Fleet(), "prod/web").Save();

        SessionNode web = saved.Find("prod/web")!;

        Assert.True(web.Settings.IsEmpty);
        Assert.Equal("web.prod.example", web.Host);

        // And the value still arrives, from where it always came from.
        Assert.Equal("deploy", saved.Session("prod/web")!.User!.Value.Value);
        Assert.Equal("prod", saved.Session("prod/web")!.User!.Value.From);
    }

    /// <summary>An override is written, and only the one that was made.</summary>
    [Fact]
    public void AnOverrideIsWrittenAndNothingElseIs()
    {
        SessionEditor editor = SessionEditor.Editing(Fleet(), "prod/web");

        editor.Field(nameof(SessionSettings.User)).Override("release");

        SessionNode web = editor.Save().Find("prod/web")!;

        Assert.Equal("release", web.Settings.User);
        Assert.Null(web.Settings.Port);
        Assert.Null(web.Settings.JumpHost);
    }

    /// <summary>And giving a field back leaves the folder's value in force again.</summary>
    [Fact]
    public void GivingAFieldBackRestoresWhatItInherits()
    {
        SessionEditor editor = SessionEditor.Editing(Fleet(), "prod/db");

        editor.Field(nameof(SessionSettings.User)).Inherit();

        SessionTree saved = editor.Save();

        Assert.Null(saved.Find("prod/db")!.Settings.User);
        Assert.Equal("deploy", saved.Session("prod/db")!.User!.Value.Value);
    }

    /// <summary>A new session lands in its folder and inherits from it.</summary>
    [Fact]
    public void ANewSessionLandsInItsFolder()
    {
        SessionEditor editor = SessionEditor.Creating(Fleet(), "prod", "cache");

        editor.Host = "cache.prod.example";

        ResolvedSession cache = editor.Save().Session("prod/cache")!;

        Assert.Equal("cache.prod.example", cache.Host);
        Assert.Equal("deploy", cache.User!.Value.Value);
        Assert.Equal(2222, cache.Port!.Value.Value);
        Assert.Contains("production", cache.Tags);
    }

    /// <summary>A port that is not a port is refused before it reaches the file.</summary>
    [Fact]
    public void APortThatIsNotAPortIsRefused()
    {
        SessionEditor editor = SessionEditor.Creating(Fleet(), "prod", "cache");

        editor.Host = "cache.prod.example";
        editor.Field(nameof(SessionSettings.Port)).Override("ssh");

        Assert.False(editor.CanSave);
        Assert.Contains(editor.Complaints, said => said.Contains("65535", StringComparison.Ordinal));
    }

    // ---- Per-session terminal settings ----

    /// <summary>
    /// A session's own scheme and size beat the global ones, which is what lets a production host
    /// look visibly unlike a staging one — a safety feature far more than a preference.
    /// </summary>
    [Fact]
    public void ASessionsOwnTerminalSettingsOverrideTheGlobalOnes()
    {
        SessionEditor editor = SessionEditor.Editing(Fleet(), "prod/web");

        editor.Field(nameof(SessionSettings.Scheme)).Override("danger");
        editor.Field(nameof(SessionSettings.FontSize)).Override("15.5");
        editor.Field(nameof(SessionSettings.Scrollback)).Override("50000");
        editor.Field(nameof(SessionSettings.TerminalType)).Override("xterm-256color");

        ResolvedSession web = editor.Save().Session("prod/web")!;

        Assert.Equal("danger", web.Scheme!.Value.Value);
        Assert.Equal("prod/web", web.Scheme!.Value.From);
        Assert.Equal(15.5, web.FontSize!.Value.Value);
        Assert.Equal(50000, web.Scrollback!.Value.Value);
        Assert.Equal("xterm-256color", web.TerminalType!.Value.Value);
    }

    // ---- After login ----

    /// <summary>
    /// A post-login command is never taken from a folder, and the model is what guarantees it: the
    /// field is not on the type that inherits.
    ///
    /// <para>A folder that could set this would type into every machine under it, and a user adding
    /// a host to that folder would never see it happen.</para>
    /// </summary>
    [Fact]
    public void APostLoginCommandIsNeverInheritedFromAFolder()
    {
        SessionTree tree = SessionTree.Of(new SessionNode
        {
            Name = string.Empty,
            Children =
            [
                new SessionNode
                {
                    Name = "prod",
                    PostLogin = "tmux attach",
                    Children = [new SessionNode { Name = "web", Host = "web.prod.example" }],
                },
            ],
        });

        Assert.Null(SessionEditor.Editing(tree, "prod/web").PostLogin);
        Assert.Null(tree.Session("prod/web")!.PostLogin);

        // There is no inheriting field for it to have come through.
        Assert.Null(typeof(SessionSettings).GetProperty("PostLogin"));
    }

    /// <summary>And where there is one, the dialog states what it does rather than what it is.</summary>
    [Fact]
    public void ThePostLoginCommandSaysWhatWillHappen()
    {
        SessionEditor editor = SessionEditor.Creating(Fleet(), "prod", "cache");

        Assert.Equal(string.Empty, editor.PostLoginWarning);

        editor.PostLogin = "tmux attach -t work";

        Assert.Contains("as though you had typed it", editor.PostLoginWarning,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// A password sent this way is refused, with the credential store named as the alternative.
    ///
    /// <para>Naming it matters as much as the refusal: a user told "no" and not told "instead" finds
    /// a worse way, and the worse way is a secret in a file they will commit.</para>
    /// </summary>
    [Theory]
    [InlineData("sshpass -p hunter2 ssh inner")]
    [InlineData("echo hunter2 | sudo -S systemctl restart web")]
    [InlineData("export DB_PASSWORD=hunter2")]
    public void APasswordInAPostLoginCommandIsRefusedAndAnAlternativeNamed(string command)
    {
        SessionEditor editor = SessionEditor.Creating(Fleet(), "prod", "cache");

        editor.Host = "cache.prod.example";
        editor.PostLogin = command;

        Assert.False(editor.CanSave);

        string said = Assert.Single(editor.Complaints);

        Assert.Contains("credential", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never written to this file", said, StringComparison.Ordinal);
    }

    /// <summary>An ordinary command is not caught by that, which is what makes the refusal usable.</summary>
    [Theory]
    [InlineData("tmux attach -t work")]
    [InlineData("cd /var/log && tail -f syslog")]
    [InlineData("sudo systemctl status nginx")]
    public void AnOrdinaryCommandIsNotRefused(string command)
    {
        SessionEditor editor = SessionEditor.Creating(Fleet(), "prod", "cache");

        editor.Host = "cache.prod.example";
        editor.PostLogin = command;

        Assert.True(editor.CanSave);
    }

    // ---- The window over it ----

    /// <summary>
    /// What a new-session dialog shows before anything is opened: a host, a name, and the way on.
    ///
    /// <para>Run against a real window on a real STA thread, because asking the model what it would
    /// show would be asking the object that decides. Everything else is inside a disclosure that is
    /// shut, and a dialog that opened with it showing would be the twelve-field dialog with an extra
    /// click in it.</para>
    /// </summary>
    [Fact]
    public void ANewSessionDialogAsksForAHostAndANameAndNothingElse()
    {
        (int boxes, bool expanded, bool enabled) = OnStaThread(() =>
        {
            SessionDialog dialog = new(SessionEditor.Creating(Fleet(), "prod", "cache"));

            dialog.Measure(new Size(600, 900));

            Expander more = Named<Expander>(dialog, "More")!;

            return (Showing<TextBox>(dialog, more).Count, more.IsExpanded,
                    Named<Button>(dialog, "Save")!.IsEnabled);
        });

        Assert.Equal(2, boxes);
        Assert.False(expanded);

        // And it will not save yet, because the one thing it asked for is empty.
        Assert.False(enabled);
    }

    /// <summary>
    /// Typing a host is enough, and the dialog says beside each field where its value came from.
    /// </summary>
    [Fact]
    public void TypingAHostIsEnoughAndTheRestSaysWhereItCameFrom()
    {
        (bool enabled, string user, string credential) = OnStaThread(() =>
        {
            SessionDialog dialog = new(SessionEditor.Creating(Fleet(), "prod", "cache"));

            dialog.Measure(new Size(600, 900));

            Named<TextBox>(dialog, "Host")!.Text = "cache.prod.example";

            return (Named<Button>(dialog, "Save")!.IsEnabled,
                    Named<TextBlock>(dialog, "UserExplains")!.Text,
                    Named<TextBlock>(dialog, "CredentialExplains")!.Text);
        });

        Assert.True(enabled);
        Assert.Equal("inherited from prod", user);
        Assert.Equal("not set", credential);
    }

    /// <summary>
    /// An inherited field's box is empty rather than pre-filled.
    ///
    /// <para>A box carrying the inherited value would become an override the moment anybody pressed
    /// save, and a folder's one default would quietly become a copy on every session under it.</para>
    /// </summary>
    [Fact]
    public void AnInheritedFieldsBoxIsEmpty()
    {
        (string user, string own) = OnStaThread(() =>
        {
            SessionDialog web = new(SessionEditor.Editing(Fleet(), "prod/web"));
            SessionDialog db = new(SessionEditor.Editing(Fleet(), "prod/db"));

            web.Measure(new Size(600, 900));
            db.Measure(new Size(600, 900));

            return (Named<TextBox>(web, "User")!.Text, Named<TextBox>(db, "User")!.Text);
        });

        Assert.Equal(string.Empty, user);

        // And a field the session does set shows what it set, because that is its own value.
        Assert.Equal("postgres", own);
    }

    // ---- plumbing ----

    private static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T found && found.Name == name)
        {
            return found;
        }

        foreach (object? child in Children(root))
        {
            if (child is DependencyObject node && Named<T>(node, name) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Every text box outside the disclosure, which is what the dialog asks for outright.</summary>
    private static List<TextBox> Showing<T>(DependencyObject root, Expander behind)
    {
        List<TextBox> found = [];

        Walk(root);

        return found;

        void Walk(DependencyObject node)
        {
            if (ReferenceEquals(node, behind))
            {
                return;
            }

            if (node is TextBox box)
            {
                found.Add(box);
            }

            foreach (object? child in Children(node))
            {
                if (child is DependencyObject inner)
                {
                    Walk(inner);
                }
            }
        }
    }

    private static IEnumerable<object?> Children(DependencyObject node) =>
        node switch
        {
            Panel panel => panel.Children.Cast<object?>(),
            ContentControl content => [content.Content],
            Decorator decorator => [decorator.Child],
            _ => [],
        };

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failed = null;

        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failed = error;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA thread never finished");

        if (failed is not null)
        {
            throw new InvalidOperationException("the work on the STA thread failed", failed);
        }

        return result;
    }
}
