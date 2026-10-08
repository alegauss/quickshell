using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>One session's forwards, as the forwards view lists them: whose they are, and the set.</summary>
/// <param name="Host">The session they belong to, as the tab names it.</param>
/// <param name="Forwards">Its forwards on the connection there is now.</param>
public readonly record struct SessionForwardsView(string Host, SessionForwards Forwards);

/// <summary>
/// Every forward this client holds, across every session (QS70).
///
/// <para><b>Forwards have no window of their own</b>, no output and no obvious presence, which is
/// exactly why they are shown here: a user who has forgotten one is running has an open route into
/// somebody's network and does not know it, and the alternative to this view is <c>netstat</c>.</para>
///
/// <para><b>What each one is carrying now, beside what it has carried.</b> A forward listening with
/// nothing connected and one carrying eight look the same in a list without the count, and those are
/// the two states a user is trying to tell apart. Read again every second while the view is open.</para>
///
/// <para>Each row stops and starts on its own, and copies the address a tool would be pointed at.
/// What did not start is listed with its reason, because a failure from an hour ago is invisible
/// everywhere else by now.</para>
/// </summary>
public sealed class ForwardsWindow : Window
{
    private readonly Func<IReadOnlyList<SessionForwardsView>> _read;
    private readonly StackPanel _rows = new() { Margin = new Thickness(12) };
    private readonly DispatcherTimer _ticking = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _shown = string.Empty;

    /// <summary>Builds the view over whatever <paramref name="read"/> says is open, each time it is asked.</summary>
    public ForwardsWindow(Func<IReadOnlyList<SessionForwardsView>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        _read = read;

        Title = "Forwards";
        AutomationProperties.SetAutomationId(this, "Forwards");

        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Refresh();

        _ticking.Tick += (_, _) => Refresh();
        _ticking.Start();

        Closed += (_, _) => _ticking.Stop();
    }

    /// <summary>The rows' text as shown, one per forward, for a test to read.</summary>
    public IReadOnlyList<string> Lines =>
        [.. _rows.Children.OfType<FrameworkElement>().Select(row => AutomationProperties.GetName(row))];

    /// <summary>Reads the forwards again, and redraws only where something changed.</summary>
    public void Refresh()
    {
        IReadOnlyList<SessionForwardsView> sessions = _read();

        List<(string Line, Action<StackPanel> Buttons)> rows = [];

        foreach (SessionForwardsView session in sessions)
        {
            foreach (ForwardActivity running in session.Forwards.Activity())
            {
                string carrying = running.Carrying is { } now
                    ? string.Create(CultureInfo.InvariantCulture, $"{now} carrying now, {running.Connections} so far")
                    : string.Create(CultureInfo.InvariantCulture, $"{running.Connections} so far");

                rows.Add(($"{session.Host}  {running.Spec}  on {running.Address}  {carrying}", buttons =>
                {
                    buttons.Children.Add(Button("Copy address", () => Clipboard.SetText(running.Address)));
                    buttons.Children.Add(Button("Stop", () => _ = StopAsync(session.Forwards, running.Spec)));
                }));
            }

            foreach (FailedForward failed in session.Forwards.Failed)
            {
                rows.Add(($"{session.Host}  {failed.Spec}  did not start: {failed.Reason}", buttons =>
                    buttons.Children.Add(Button("Start", () => _ = StartAsync(session.Forwards, failed.Spec)))));
            }
        }

        string shown = string.Join('\n', rows.Select(row => row.Line));

        if (shown == _shown && _rows.Children.Count > 0)
        {
            return;
        }

        _shown = shown;
        _rows.Children.Clear();

        if (rows.Count == 0)
        {
            TextBlock none = new() { Text = "No forwards are running. A saved session's forwards start with it." };

            AutomationProperties.SetAutomationId(none, "NoForwards");
            AutomationProperties.SetName(none, none.Text);
            _rows.Children.Add(none);

            return;
        }

        foreach ((string line, Action<StackPanel> buttons) in rows)
        {
            DockPanel row = new() { Margin = new Thickness(0, 0, 0, 6) };
            StackPanel acts = new() { Orientation = Orientation.Horizontal };

            buttons(acts);
            DockPanel.SetDock(acts, Dock.Right);

            row.Children.Add(acts);
            row.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });

            AutomationProperties.SetName(row, line);
            _rows.Children.Add(row);
        }
    }

    /// <summary>Told when a row stopped or started a forward, so the window's title can count again.</summary>
    public Action? Changed { get; init; }

    private async Task StopAsync(SessionForwards forwards, ForwardSpec spec)
    {
        await forwards.StopAsync(spec);
        Refresh();
        Changed?.Invoke();
    }

    private async Task StartAsync(SessionForwards forwards, ForwardSpec spec)
    {
        await forwards.StartAsync(spec);
        Refresh();
        Changed?.Invoke();
    }

    private static Button Button(string label, Action act)
    {
        Button button = new() { Content = label, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };

        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => act();

        return button;
    }
}
