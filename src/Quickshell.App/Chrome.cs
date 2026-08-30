namespace Quickshell.App;

/// <summary>Something the window can show around the terminal.</summary>
public enum ChromeElement
{
    /// <summary>The window's own bar. Always there; it is how a window is moved and closed.</summary>
    TitleBar,

    /// <summary>The tabs. Hidden while there is one, because one tab is not a choice.</summary>
    TabStrip,

    /// <summary>The terminal. The reason the window exists.</summary>
    Terminal,

    /// <summary>Buttons across the top. Not shown, ever, unless a user asks.</summary>
    Toolbar,

    /// <summary>A bar along the bottom. Not shown unless a user asks.</summary>
    StatusBar,

    /// <summary>A panel down the side. Not shown unless a user opens one.</summary>
    Sidebar,
}

/// <summary>
/// What the window shows, and the budget every later element spends.
///
/// <para><b>This is the first argument this project makes.</b> The incumbent opens onto a toolbar, a
/// sidebar, a status bar and an advertisement. quickshell opens onto a terminal. That is not a
/// preference about decoration — it is the claim the whole client rests on, and it is lost one
/// well-meaning addition at a time rather than all at once.</para>
///
/// <para>So it is a model with a test against it rather than a habit. Anything added later has to
/// change <see cref="Default"/>, which means changing a test that says what a default installation
/// looks like, which means somebody deciding to.</para>
/// </summary>
public sealed record Chrome
{
    /// <summary>What a default installation shows: a title bar, a terminal, and nothing else.</summary>
    public static Chrome Default { get; } = new();

    /// <summary>Whether a toolbar is showing. False, and there is no default that says otherwise.</summary>
    public bool Toolbar { get; init; }

    /// <summary>Whether a status bar is showing.</summary>
    public bool StatusBar { get; init; }

    /// <summary>Whether a sidebar is open.</summary>
    public bool Sidebar { get; init; }

    /// <summary>
    /// What is on screen with this many tabs open, in the order it appears from the top.
    ///
    /// <para>The tab strip is the one element that decides for itself: with one tab there is nothing
    /// to choose between, so a strip would be a row of pixels showing the user what they already
    /// know. It appears when a second tab does.</para>
    /// </summary>
    public IReadOnlyList<ChromeElement> Showing(int tabs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tabs, 1);

        List<ChromeElement> showing = [ChromeElement.TitleBar];

        if (tabs > 1)
        {
            showing.Add(ChromeElement.TabStrip);
        }

        if (Toolbar)
        {
            showing.Add(ChromeElement.Toolbar);
        }

        if (Sidebar)
        {
            showing.Add(ChromeElement.Sidebar);
        }

        showing.Add(ChromeElement.Terminal);

        if (StatusBar)
        {
            showing.Add(ChromeElement.StatusBar);
        }

        return showing;
    }
}
