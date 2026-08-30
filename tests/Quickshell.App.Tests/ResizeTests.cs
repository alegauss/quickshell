using System.Diagnostics;
using System.Text;
using Quickshell.Terminal;
using Quickshell.Transport;
using Xunit;

namespace Quickshell.App.Tests;

/// <summary>
/// Three parties hold a copy of the size, and only the window knows it changed.
/// </summary>
public sealed class ResizeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // ---- The falsification ----

    /// <summary>
    /// The design's own falsification: <em>falsified when a remote full-screen program is still
    /// drawing at the old width a second after the drag ended</em>.
    ///
    /// <para>Asked of a real shell on a real pseudo-console: the window is dragged through a range of
    /// sizes, and once it settles the program is asked what console it thinks it has. Its answer has
    /// to be the size the drag ended on.</para>
    /// </summary>
    [Fact]
    public async Task AProgramIsDrawingAtTheNewWidthOnceTheDragEnds()
    {
        await using ConPtyChannel channel = await ConPtyChannel.StartAsync(
            "cmd.exe /q", 80, 25, null, TestContext.Current.CancellationToken);

        Emulator emulator = new(80, 25);
        await using SessionPipeline pipeline = SessionPipeline.Start(channel, emulator);

        await Settle(pipeline);

        // A drag: many sizes in quick succession, ending on one in particular.
        foreach (int columns in new[] { 96, 104, 112, 120, 132 })
        {
            pipeline.Resize(columns, 40);
        }

        await Until(() => pipeline.Resizes > 0);
        await Task.Delay(SessionPipeline.ResizeQuiet * 4, TestContext.Current.CancellationToken);

        // Every party now has to agree on the size the drag ended on.
        Assert.Equal((132, 40), channel.Size);
        Assert.Equal(132, emulator.Buffer.Columns);
        Assert.Equal(40, emulator.Buffer.Rows);

        // And the program itself, which is the only one of the three that matters to a user.
        await pipeline.TypeAsync(Typed("mode con"), TestContext.Current.CancellationToken);

        string said = await Read(pipeline, emulator);

        Assert.Contains("132", said, StringComparison.Ordinal);
        Assert.Contains("40", said, StringComparison.Ordinal);
    }

    // ---- The order ----

    /// <summary>
    /// The model reflows before the far end hears anything. The moment the far end knows, the program
    /// starts drawing at the new width, and a model still holding the old one would render that as
    /// damage.
    /// </summary>
    [Fact]
    public async Task TheModelTakesTheSizeBeforeTheFarEndIsTold()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        pipeline.Resize(100, 30);

        await Until(() => emulator.Buffer.Columns == 100);

        Assert.Equal(0, far.Resizes);

        await Until(() => far.Resizes > 0);

        Assert.Equal((100, 30), far.Size);
    }

    /// <summary>
    /// A resize takes its turn among the bytes, so it never reflows text the host had not finished
    /// sending.
    /// </summary>
    [Fact]
    public async Task AResizeTakesItsTurnAmongTheBytes()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        far.Produce(Encoding.UTF8.GetBytes("before the resize\r\n"));
        pipeline.Resize(40, 24);
        far.Produce(Encoding.UTF8.GetBytes("after the resize\r\n"));
        far.Finish();

        Assert.Same(
            pipeline.Completed,
            await Task.WhenAny(
                pipeline.Completed, Task.Delay(Patience, TestContext.Current.CancellationToken)));
        await pipeline.Completed;

        Assert.Equal(40, emulator.Buffer.Columns);
        Assert.Contains("before the resize", Screen(emulator), StringComparison.Ordinal);
        Assert.Contains("after the resize", Screen(emulator), StringComparison.Ordinal);
    }

    // ---- Debounced, never dropped ----

    /// <summary>
    /// A drag fires continuously. Undebounced it would issue one window-change request per pixel of
    /// travel, and a remote editor would redraw for every one.
    /// </summary>
    [Fact]
    public async Task ADragBecomesFarFewerNotificationsThanItHasSizes()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        for (int columns = 40; columns < 240; columns++)
        {
            pipeline.Resize(columns, 24);
        }

        await Until(() => emulator.Buffer.Columns == 239);
        await Until(() => pipeline.Resizes > 0);
        await Task.Delay(SessionPipeline.ResizeQuiet * 4, TestContext.Current.CancellationToken);

        Assert.True(
            far.Resizes < 20,
            $"two hundred sizes became {far.Resizes} notifications, which is not debouncing");
    }

    /// <summary>
    /// And the last one always arrives. A resize that ended with no notification leaves the program
    /// permanently wrong about its own width, which is worse than a hundred notifications.
    /// </summary>
    [Fact]
    public async Task TheSizeTheDragEndedOnAlwaysArrives()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        for (int round = 0; round < 5; round++)
        {
            for (int columns = 40; columns < 120; columns++)
            {
                pipeline.Resize(columns, 24 + round);
            }

            await Task.Delay(SessionPipeline.ResizeQuiet * 3, TestContext.Current.CancellationToken);
        }

        await Until(() => far.Size == (119, 28));

        Assert.Equal((119, 28), far.Size);
    }

    // ---- What is never sent ----

    /// <summary>A size of zero is clamped and never sent, because some programs divide by it.</summary>
    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(0, 0)]
    [InlineData(-5, -5)]
    public async Task ASizeOfZeroIsClampedRatherThanSent(int columns, int rows)
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        pipeline.Resize(columns, rows);

        await Until(() => far.Resizes > 0);

        Assert.True(far.Size.Columns >= 1, $"a width of {far.Size.Columns} reached the far end");
        Assert.True(far.Size.Rows >= 1, $"a height of {far.Size.Rows} reached the far end");
        Assert.True(emulator.Buffer.Columns >= 1);
        Assert.True(emulator.Buffer.Rows >= 1);
    }

    /// <summary>
    /// The text survives the resize, because the model reflows rather than being rebuilt — QS23.
    /// </summary>
    [Fact]
    public async Task TextSurvivesTheResizeThatReflowsIt()
    {
        PtyStub far = new();
        Emulator emulator = new(80, 24);
        await using SessionPipeline pipeline = SessionPipeline.Start(far, emulator);

        far.Produce(Encoding.UTF8.GetBytes(new string('x', 70) + "-end\r\n"));

        await Until(() => emulator.Buffer.Generation > 0);

        pipeline.Resize(40, 24);

        await Until(() => emulator.Buffer.Columns == 40);

        Assert.Contains("-end", Screen(emulator), StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static byte[] Typed(string line) =>
        Encoding.UTF8.GetBytes(line + (char)0x0D + (char)0x0A);

    /// <summary>Waits for the far end to stop talking, which is what a prompt being up looks like.</summary>
    private static async Task Settle(SessionPipeline pipeline)
    {
        await Until(() => pipeline.Work.Chunks > 0);

        long before;

        do
        {
            before = pipeline.Work.Bytes;
            await Task.Delay(400, TestContext.Current.CancellationToken);
        }
        while (pipeline.Work.Bytes != before);
    }

    private static async Task<string> Read(SessionPipeline pipeline, Emulator emulator)
    {
        await Settle(pipeline);

        return Screen(emulator);
    }

    private static async Task Until(Func<bool> what)
    {
        Stopwatch clock = Stopwatch.StartNew();

        while (clock.Elapsed < Patience)
        {
            if (what())
            {
                return;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Fail("the pipeline did not get where this test needed it within the patience allowed");
    }

    private static string Screen(Emulator emulator)
    {
        StringBuilder text = new();

        for (int row = 0; row < emulator.Buffer.Rows; row++)
        {
            foreach (Cell cell in emulator.Buffer.Screen(row))
            {
                if (cell.Width != 0)
                {
                    text.Append(emulator.Buffer.TextOf(cell));
                }
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
