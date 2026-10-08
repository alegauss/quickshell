namespace Quickshell.Gate;

/// <summary>
/// Where the parse and emulate arms run: the replay harness laid over the client being archived, so
/// what is timed is that client's bytes under that client's runtime settings (QS200).
///
/// <para><b>Built from the same source is not the same thing.</b> A harness built on its own takes
/// its own runtimeconfig and its own compilation, so a setting that exists only in the client's
/// project or in its publish command — tiered PGO off, another GC mode — would slow what ships while
/// the gate reported the figures held. So the client's folder is copied whole, the harness adds only
/// the files the client does not have (its own entry point, in practice), and the client's
/// runtimeconfig is written as the harness's. Every assembly both carry is the client's copy, which
/// is the copy a release zips.</para>
/// </summary>
public static class Bench
{
    /// <summary>The harness's own name, which its entry point and its runtimeconfig are named for.</summary>
    public const string Harness = "Quickshell.Replay";

    /// <summary>
    /// Lays a published harness over a copy of a published client and returns the harness's entry
    /// point there.
    /// </summary>
    /// <param name="client">The client's executable, in the folder a release archives.</param>
    /// <param name="harness">The folder the replay harness was published to.</param>
    /// <param name="into">Where to assemble the two; replaced, never added to.</param>
    /// <returns>The harness's executable inside <paramref name="into"/>.</returns>
    public static string Assemble(string client, string harness, string into)
    {
        ArgumentNullException.ThrowIfNull(client);

        string clientFolder = Path.GetDirectoryName(Path.GetFullPath(client))!;
        string settings = Path.Combine(clientFolder, $"{Path.GetFileNameWithoutExtension(client)}.runtimeconfig.json");

        // Without it the arms would run under the harness's own settings, which is the very gap this
        // closes - so it is a refusal and not a fallback.
        if (!File.Exists(settings))
        {
            throw new GateRefusal($"{client} has no runtimeconfig beside it, so its runtime settings cannot be "
                                  + "given to the replay arms");
        }

        if (!File.Exists(Path.Combine(harness, $"{Harness}.exe")))
        {
            throw new GateRefusal($"there is no published {Harness} in {harness} to lay over the client");
        }

        // Wiped rather than copied over: a file the last client had and this one does not would
        // otherwise be loaded as though it shipped.
        if (Directory.Exists(into))
        {
            Directory.Delete(into, recursive: true);
        }

        Copy(clientFolder, into, overwrite: true);
        Copy(harness, into, overwrite: false);

        File.Copy(settings, Path.Combine(into, $"{Harness}.runtimeconfig.json"), overwrite: true);

        return Path.Combine(into, $"{Harness}.exe");
    }

    private static void Copy(string from, string to, bool overwrite)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));

            if (!overwrite && File.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }
}
