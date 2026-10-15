using System.IO;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// QS314: several sidebar rows chosen at once, and a chosen folder standing for every session under
/// it, so an action that takes a set has one to take.
/// </summary>
public sealed class SessionSelectionTests : IDisposable
{
    private readonly string _here = Path.Combine(Path.GetTempPath(), "qs-select-" + Guid.NewGuid().ToString("N"));

    public SessionSelectionTests() => Directory.CreateDirectory(_here);

    public void Dispose() => Directory.Delete(_here, recursive: true);

    /// <summary>
    /// Two sessions and a folder, one of the two inside it: each row reports itself selected, and the
    /// set holds every session in the folder, at any depth, and the other session, each exactly once.
    /// </summary>
    [Fact]
    public void TwoSessionsAndAFolderAreOneSetWithEverySessionOnce()
    {
        (bool[] selected, string[] set) = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.SelectSessionRows(["Work/db-primary", "pi", "Work"]);

            ListBox list = Find<ListBox>(window.Sidebar, "SessionsList");

            // Through the item peers the list's own peer hands a screen reader and a case.
            SelectorAutomationPeer listed = (SelectorAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(list);

            bool[] reported = [.. list.Items.OfType<ListBoxItem>()
                                     .Where(item => item.Tag is SessionRow { Path: "Work/db-primary" or "pi" or "Work" })
                                     .Select(item => ((ISelectionItemProvider)new ListBoxItemAutomationPeer(item, listed)).IsSelected)];

            return (reported, window.SelectedSessions().Select(session => session.Path).ToArray());
        });

        Assert.Equal([true, true, true], selected);
        Assert.Equal(["Work/build", "Work/db-primary", "Work/Nested/deep", "pi"], set);
    }

    /// <summary>One row selected is a set of one, as a plain click leaves it.</summary>
    [Fact]
    public void OneRowIsASetOfOne()
    {
        string[] set = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.SelectSessionRows(["Work/db-primary", "pi"]);
            window.Sidebar.Select("pi");

            return window.SelectedSessions().Select(session => session.Path).ToArray();
        });

        Assert.Equal(["pi"], set);
    }

    /// <summary>A store refreshed while several rows are chosen keeps every one of them chosen.</summary>
    [Fact]
    public void ARefreshKeepsTheWholeSelection()
    {
        string[] kept = Sta.Run(() =>
        {
            MainWindow window = Window();

            window.SelectSessionRows(["Work/db-primary", "pi"]);
            window.RefreshSessions();

            return window.Sidebar.SelectedRows.Select(row => row.Path).ToArray();
        });

        Assert.Equal(["Work/db-primary", "pi"], kept);
    }

    private MainWindow Window()
    {
        string store = Path.Combine(_here, "sessions.json");

        File.WriteAllText(store, """
            { "name": "", "children": [
                { "name": "Work", "children": [
                    { "name": "build", "host": "build.example.invalid" },
                    { "name": "db-primary", "host": "db1.example.invalid" },
                    { "name": "Nested", "children": [ { "name": "deep", "host": "deep.example.invalid" } ] }
                ] },
                { "name": "Home", "children": [ { "name": "nas", "host": "nas.example.invalid" } ] },
                { "name": "pi", "host": "pi.example.invalid" }
            ] }
            """);

        MainWindow window = new() { SessionsFile = store };

        window.RefreshSessions();

        // Folders start folded (QS304); Work is opened so a session inside it can be chosen by its row.
        window.ToggleFolder("Work");

        return window;
    }

    private static T Find<T>(System.Windows.DependencyObject root, string id) where T : System.Windows.DependencyObject =>
        Under(root).OfType<T>().First(each => System.Windows.Automation.AutomationProperties.GetAutomationId(each) == id);

    private static IEnumerable<System.Windows.DependencyObject> Under(System.Windows.DependencyObject root)
    {
        yield return root;

        foreach (System.Windows.DependencyObject child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            foreach (System.Windows.DependencyObject below in Under(child))
            {
                yield return below;
            }
        }
    }
}
