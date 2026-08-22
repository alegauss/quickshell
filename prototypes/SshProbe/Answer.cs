namespace SshProbe;

/// <summary>
/// One of the six questions, answered. <see cref="Evidence"/> is what the run actually did or
/// saw - never a sentence from the library's documentation, which is the one thing QS5 forbids.
/// </summary>
public sealed class Answer
{
    public string Question { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Work { get; set; } = "";
}
