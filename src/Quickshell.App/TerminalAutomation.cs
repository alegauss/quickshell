using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using Quickshell.Terminal;

namespace Quickshell.App;

/// <summary>
/// What a screen reader sees when it looks at the terminal.
///
/// <para><b>Built rather than inherited, because there is nothing to inherit.</b> The pane is a
/// child window a swapchain paints into: it has no controls, no text elements and no automation
/// tree of its own. Assistive technology looking at it finds a rectangle. So this publishes the
/// buffer as a text pattern, and without it the client cannot be used without sight — not poorly,
/// but at all.</para>
///
/// <para><b>Announcements are throttled, and that decides whether any of it works.</b> A reader
/// handed one notification per row during a <c>cat</c> is still working through the first second of
/// output a minute later. <see cref="TextChanges"/> is where that is decided; this only asks it.</para>
///
/// <para><b>It is reached through WPF's tree and never through the child window.</b> The pane hosts
/// an HWND, and a hosted window answers <c>WM_GETOBJECT</c> down a path that produces no provider
/// for a peer of this kind and then hands the nothing to UIA, which throws inside the message pump
/// and takes the process with it — see <see cref="TerminalPane"/>, which is where that message stops
/// now. This peer is published the ordinary way, as the peer of an element in a window's tree, and
/// that is the path assistive technology actually walks. QS148.</para>
/// </summary>
public sealed class TerminalAutomationPeer : FrameworkElementAutomationPeer, ITextProvider
{
    private readonly TextChanges _changes;
    private readonly TerminalDocument _document;

    /// <summary>Publishes a buffer through an element a reader can find.</summary>
    /// <param name="owner">The element in the tree; the pane it is looking at.</param>
    /// <param name="buffer">The terminal's own buffer, read and never copied.</param>
    /// <param name="changes">How often to announce; the usual throttle when null.</param>
    public TerminalAutomationPeer(FrameworkElement owner, TerminalBuffer buffer,
                                  TextChanges? changes = null)
        : base(owner)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _document = new TerminalDocument(buffer);
        _changes = changes ?? new TextChanges();
    }

    /// <summary>The whole of it: scrollback and screen, as one document.</summary>
    public ITextRangeProvider DocumentRange =>
        new TerminalTextRange(_document, 0, _document.Length, ProviderFromPeer(this));

    /// <summary>
    /// One selection at a time, which is what a terminal has.
    /// </summary>
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    /// <summary>
    /// Where the caret is, as an empty range — which is how a reader follows a prompt as somebody
    /// types, and the reason <see cref="TerminalDocument.Caret"/> exists.
    /// </summary>
    public ITextRangeProvider[] GetSelection() =>
        [new TerminalTextRange(_document, _document.Caret, _document.Caret, ProviderFromPeer(this))];

    /// <inheritdoc/>
    public ITextRangeProvider[] GetVisibleRanges() => [DocumentRange];

    /// <inheritdoc/>
    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    /// <summary>
    /// A range at a point.
    ///
    /// <para>The document range, because this class has no map from a screen point to an offset —
    /// the pane is a texture. Answering with the whole document is a reader finding text it can
    /// then navigate, where answering with nothing is a reader finding a rectangle.</para>
    /// </summary>
    public ITextRangeProvider RangeFromPoint(Point screenLocation) => DocumentRange;

    /// <summary>
    /// Something changed. Answers whether an event was raised, so a caller can see the throttle
    /// working rather than take it on trust.
    /// </summary>
    /// <param name="now">The clock, passed in so this is testable without sleeping.</param>
    public bool Changed(TimeSpan now)
    {
        if (!_changes.Changed(now))
        {
            return false;
        }

        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);

        return true;
    }

    /// <summary>
    /// The cursor moved. A caret event is what lets a reader follow a shell prompt, and it is not
    /// throttled with the text: a user typing wants to hear where they are, immediately.
    /// </summary>
    public void CaretMoved() =>
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);

    /// <summary>Announces a change that was held back, when its quiet period has passed.</summary>
    public bool Settle(TimeSpan now)
    {
        if (!_changes.Due(now))
        {
            return false;
        }

        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);

        return true;
    }

    /// <inheritdoc/>
    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Text ? this : base.GetPattern(patternInterface);

    /// <inheritdoc/>
    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Document;

    /// <inheritdoc/>
    protected override string GetClassNameCore() => "Terminal";

    /// <summary>
    /// What a reader announces when it reaches this element.
    ///
    /// <para>Not the output: a name is read on arrival and the text is read on request, and a name
    /// that was the whole scrollback would be a reader saying a session's history every time focus
    /// moved.</para>
    /// </summary>
    protected override string GetNameCore() => "Terminal output";
}
