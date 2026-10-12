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

    // ---- Reconnect, which until QS229 only the file could set ----

    /// <summary>
    /// Reconnecting is a field of the dialog like the rest: shown with the folder it came from, set
    /// here as a yes or a no, and refused as anything else (QS229).
    /// </summary>
    [Fact]
    public void ReconnectIsSetInTheDialogAndShowsWhereItCameFrom()
    {
        SessionTree fleet = Fleet().With("prod", Fleet().Find("prod")! with
        {
            Settings = Fleet().Find("prod")!.Settings with { Reconnect = true },
        });

        SessionEditor editor = SessionEditor.Editing(fleet, "prod/web");
        EditableField reconnect = editor.Field(nameof(SessionSettings.Reconnect));

        Assert.Equal("true", reconnect.Effective);
        Assert.Equal("inherited from prod", reconnect.Explains);

        reconnect.Override("maybe");

        Assert.Contains(editor.Complaints, said => said.Contains("Reconnect after a drop", StringComparison.Ordinal));

        reconnect.Override("no");

        SessionTree saved = editor.Save();

        Assert.False(saved.Find("prod/web")!.Settings.Reconnect);
        Assert.Equal(new Source<bool>(false, "prod/web"), saved.Session("prod/web")!.Reconnect);
    }

    /// <summary>
    /// A Reconnect somebody wrote into the file survives the dialog being opened and saved, which it
    /// did not while the dialog had no field for it: a save wrote back only the fields it showed.
    /// </summary>
    [Fact]
    public void AReconnectWrittenByHandSurvivesADialogSave()
    {
        SessionTree fleet = Fleet().With("prod/web", Fleet().Find("prod/web")! with
        {
            Settings = new SessionSettings { Reconnect = true },
        });

        SessionTree saved = SessionEditor.Editing(fleet, "prod/web").Save();

        Assert.True(saved.Find("prod/web")!.Settings.Reconnect);
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

    /// <summary>
    /// A new session lands under the name it was saved with, not the one the dialog opened on, and
    /// one named like a session already in its folder is refused rather than replacing it (QS121).
    /// </summary>
    [Fact]
    public void ANewSessionIsSavedUnderItsOwnNameAndNeverOverAnother()
    {
        SessionEditor editor = SessionEditor.Creating(Fleet(), "prod", "New session");

        editor.Name = string.Empty;
        editor.Host = "cache.prod.example";

        SessionTree saved = editor.Save();

        Assert.Equal("prod/cache.prod.example", editor.SavedAt);
        Assert.Equal("cache.prod.example", saved.Session("prod/cache.prod.example")!.Host);
        Assert.Null(saved.Find("prod/New session"));

        SessionEditor clash = SessionEditor.Creating(saved, "prod", "New session");

        clash.Name = "cache.prod.example";
        clash.Host = "elsewhere.example";

        Assert.False(clash.CanSave);
        Assert.Contains(clash.Complaints, said => said.Contains("already something called", StringComparison.Ordinal));
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
        editor.Field(nameof(SessionSettings.Scrollback)).Override("50000");
        editor.Field(nameof(SessionSettings.TerminalType)).Override("xterm-256color");

        ResolvedSession web = editor.Save().Session("prod/web")!;

        Assert.Equal("danger", web.Scheme!.Value.Value);
        Assert.Equal("prod/web", web.Scheme!.Value.From);
        Assert.Equal(50000, web.Scrollback!.Value.Value);
        Assert.Equal("xterm-256color", web.TerminalType!.Value.Value);
    }

    /// <summary>
    /// QS245: the font size is not in the dialog, because a window has one font (QS135), and a size a
    /// hand edit put in the file survives a save through the dialog all the same.
    /// </summary>
    [Fact]
    public void TheFontSizeIsNotOfferedAndAHandEditedOneSurvivesASave()
    {
        SessionTree tree = Fleet().With("prod/web", new SessionNode
        {
            Name = "web",
            Host = "web.prod.example",
            Settings = new SessionSettings { FontSize = 15.5 },
        });

        SessionEditor editor = SessionEditor.Editing(tree, "prod/web");

        Assert.DoesNotContain(editor.Fields, field => field.Name == nameof(SessionSettings.FontSize));

        editor.Field(nameof(SessionSettings.Scrollback)).Override("100");

        Assert.Equal(15.5, editor.Save().Session("prod/web")!.FontSize!.Value.Value);
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
    /// What a new-session dialog shows before anything is opened (QS270): where it goes, what it is
    /// called, who it signs in as and how, and every other field in a group that is shut.
    ///
    /// <para>Run against a real window on a real STA thread, because asking the model what it would
    /// show would be asking the object that decides. A dialog that opened with a group showing would be
    /// the twelve-field dialog with an extra click in it.</para>
    /// </summary>
    [Fact]
    public void ANewSessionDialogAsksWhereWhoAndHowAndFoldsTheRest()
    {
        (string[] boxes, bool[] expanded, bool enabled, string save) = Sta.Run<(string[], bool[], bool, string)>(() =>
        {
            SessionDialog dialog = new(SessionEditor.Creating(SessionTree.Empty(), string.Empty, "cache"));

            dialog.Measure(new Size(700, 1200));

            List<Expander> groups = [.. All<Expander>(dialog)];

            return ([.. Showing(dialog).Select(box => box.Name)], [.. groups.Select(group => group.IsExpanded)],
                    Named<Button>(dialog, "Save")!.IsEnabled, (string)Named<Button>(dialog, "Save")!.Content);
        });

        Assert.Equal(["Host", "Port", "Name", "User"], boxes);
        Assert.Equal(4, expanded.Length);
        Assert.All(expanded, Assert.False);
        Assert.Equal("Save and connect", save);

        // And it will not save yet, because the one thing it asked for is empty.
        Assert.False(enabled);
    }

    /// <summary>
    /// The sign-in choice is a choice: a segment shows the one field it needs, and saving writes the
    /// same Key and Credential a hand edit would.
    /// </summary>
    [Fact]
    public void ChoosingAKeyFileShowsItsPathAndSavesItAsTheKey()
    {
        (string[] before, string[] after, string? key, string? credential) = Sta.Run<(string[], string[], string?, string?)>(() =>
        {
            SessionEditor editor = SessionEditor.Creating(SessionTree.Empty(), string.Empty, "cache");
            SessionDialog dialog = new(editor);

            dialog.Measure(new Size(700, 1200));
            Named<TextBox>(dialog, "Host")!.Text = "cache.example";

            string[] first = [.. Showing(dialog).Select(box => box.Name)];

            Named<RadioButton>(dialog, "SignInKeyFile")!.IsChecked = true;
            Named<TextBox>(dialog, "Key")!.Text = "~/.ssh/id_cache";

            string[] then = [.. Showing(dialog).Select(box => box.Name)];

            Named<Button>(dialog, "Save")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            SessionNode saved = dialog.Saved!.Find(dialog.Editor.SavedAt)!;

            return (first, then, saved.Settings.Key, saved.Settings.Credential);
        });

        Assert.DoesNotContain("Key", before);
        Assert.Contains("Key", after);
        Assert.Equal("~/.ssh/id_cache", key);
        Assert.Null(credential);
    }

    /// <summary>
    /// QS270's falsification: a session saved from this dialog is the session the same fields make
    /// written by hand, field for field.
    /// </summary>
    [Fact]
    public void ASessionSavedFromTheDialogIsTheOneWrittenByHand()
    {
        string dialogs = Sta.Run(() =>
        {
            SessionDialog dialog = new(SessionEditor.Creating(SessionTree.Empty(), string.Empty, "x"));

            Named<TextBox>(dialog, "Host")!.Text = "db1.example.com";
            Named<TextBox>(dialog, "Name")!.Text = "db-primary";
            Named<TextBox>(dialog, "Port")!.Text = "2222";
            Named<TextBox>(dialog, "User")!.Text = "deploy";
            Named<RadioButton>(dialog, "SignInPassword")!.IsChecked = true;
            Named<TextBox>(dialog, "Credential")!.Text = "db-primary";
            Named<TextBox>(dialog, "JumpHost")!.Text = "bastion.example.com";
            Named<TextBox>(dialog, "Reconnect")!.Text = "true";
            Named<Button>(dialog, "Save")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            return Written(dialog.Saved!);
        });

        string hands = Written(SessionTree.Of(new SessionNode
        {
            Name = string.Empty,
            Children =
            [
                new SessionNode
                {
                    Name = "db-primary",
                    Host = "db1.example.com",
                    Settings = new SessionSettings
                    {
                        Port = 2222, User = "deploy", Credential = "db-primary",
                        JumpHost = "bastion.example.com", Reconnect = true,
                    },
                },
            ],
        }));

        Assert.Equal(hands, dialogs);
    }

    [Fact]
    public void TheSignInChoiceIsReadFromWhatTheFieldsSay()
    {
        Assert.Equal(SignInMethod.KeyFile, SessionEditor.Editing(Fleet(), "prod/web").SignIn);
        Assert.Equal(SignInMethod.KeyOrAgent, SessionEditor.Creating(SessionTree.Empty(), string.Empty, "x").SignIn);

        SessionEditor saved = SessionEditor.Creating(SessionTree.Empty(), string.Empty, "x");

        saved.Field(nameof(SessionSettings.Credential)).Override("db");

        Assert.Equal(SignInMethod.Password, SessionEditor.Editing(Saved(saved, "db.example"), "x").SignIn);
    }

    [Fact]
    public void AKeyFileWithNoPathAndAKeyAFolderHandsDownAreBothSaid()
    {
        SessionEditor bare = SessionEditor.Creating(SessionTree.Empty(), string.Empty, "x");

        bare.Host = "a.example";
        bare.SignIn = SignInMethod.KeyFile;

        Assert.Contains(bare.Complaints, said => said.Contains("key file's path", StringComparison.Ordinal));

        SessionEditor underProd = SessionEditor.Creating(Fleet(), "prod", "cache");

        underProd.Host = "cache.prod.example";
        underProd.SignIn = SignInMethod.KeyOrAgent;

        Assert.Contains(underProd.Complaints, said => said.Contains("prod hands down the key file", StringComparison.Ordinal));
    }

    [Fact]
    public void ChoosingAWayGivesTheOtherFieldBack()
    {
        SessionEditor editor = SessionEditor.Creating(SessionTree.Empty(), string.Empty, "x");

        editor.SignIn = SignInMethod.KeyFile;
        editor.Field(nameof(SessionSettings.Key)).Override("~/.ssh/k");
        editor.SignIn = SignInMethod.Password;

        Assert.False(editor.Field(nameof(SessionSettings.Key)).IsOverridden);
    }

    [Fact]
    public void AGroupsSummarySaysWhatItHoldsAndWhereItCameFrom()
    {
        SessionEditor web = SessionEditor.Editing(Fleet(), "prod/web");

        Assert.Equal("bastion.example, from prod", web.Summary(SessionGroupName.JumpHost));
        Assert.Equal("None", web.Summary(SessionGroupName.Tunnels));
        Assert.Equal("Nothing typed", web.Summary(SessionGroupName.AfterLogin));
        Assert.Equal("quickshell, no reconnect", web.Summary(SessionGroupName.Terminal));
    }

    /// <summary>A new session moved to another folder keeps what was typed and inherits from there.</summary>
    [Fact]
    public void ANewSessionInAnotherFolderInheritsFromIt()
    {
        SessionEditor top = SessionEditor.Creating(Fleet(), string.Empty, "cache");

        top.Host = "cache.example";
        top.Field(nameof(SessionSettings.Port)).Override("2200");

        SessionEditor moved = top.InFolder("prod");

        Assert.Equal(["", "prod"], top.Folders);
        Assert.Equal("prod", moved.Folder);
        Assert.Equal("cache.example", moved.Host);
        Assert.Equal("2200", moved.Field(nameof(SessionSettings.Port)).Own);
        Assert.Equal("inherited from prod", moved.Field(nameof(SessionSettings.User)).Explains);
        Assert.Throws<InvalidOperationException>(() => SessionEditor.Editing(Fleet(), "prod/web").InFolder(""));
    }

    /// <summary>Test connection runs on the session as it stands, unsaved, and says what it was told.</summary>
    [Fact]
    public void TestConnectionRunsOnTheUnsavedSession()
    {
        (string? host, string said, bool saved) = Sta.Run(() =>
        {
            string? asked = null;
            SessionDialog dialog = new(SessionEditor.Creating(SessionTree.Empty(), string.Empty, "x"))
            {
                Tests = (session, _) =>
                {
                    asked = $"{session.User?.Value}@{session.Host}";

                    return Task.FromResult("Signed in.");
                },
            };

            Named<TextBox>(dialog, "Host")!.Text = "a.example";
            Named<TextBox>(dialog, "User")!.Text = "me";

            dialog.TestAsync().GetAwaiter().GetResult();

            return (asked, dialog.TestSaid, dialog.Saved is not null);
        });

        Assert.Equal("me@a.example", host);
        Assert.Equal("Signed in.", said);
        Assert.False(saved, "testing saved the session");
    }

    private static SessionTree Saved(SessionEditor editor, string host)
    {
        editor.Host = host;

        return editor.Save();
    }

    private static string Written(SessionTree tree)
    {
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"quickshell-written-{Guid.NewGuid():N}.json");

        try
        {
            tree.WriteTo(file);

            return System.IO.File.ReadAllText(file);
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }

    /// <summary>
    /// Typing a host is enough, and the dialog says beside each field where its value came from.
    /// </summary>
    [Fact]
    public void TypingAHostIsEnoughAndTheRestSaysWhereItCameFrom()
    {
        (bool enabled, string user, string credential) = Sta.Run(() =>
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
        (string user, string own) = Sta.Run(() =>
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

    /// <summary>Every element of a type, in tree order.</summary>
    private static List<T> All<T>(DependencyObject root)
    {
        List<T> found = [];

        Walk(root);

        return found;

        void Walk(DependencyObject node)
        {
            if (node is T hit)
            {
                found.Add(hit);
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

    /// <summary>
    /// Every text box on screen before any group is opened, which is what the dialog asks for
    /// outright: outside every group, and under nothing collapsed.
    /// </summary>
    private static List<TextBox> Showing(DependencyObject root)
    {
        List<TextBox> found = [];

        // From what the window holds: a window that was never shown reads as collapsed itself.
        foreach (object? child in Children(root))
        {
            if (child is DependencyObject inner)
            {
                Walk(inner);
            }
        }

        return found;

        void Walk(DependencyObject node)
        {
            if (node is Expander || node is UIElement { Visibility: not Visibility.Visible })
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
}
