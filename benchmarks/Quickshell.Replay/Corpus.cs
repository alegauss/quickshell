using System.IO.Compression;
using System.Security.Cryptography;

namespace Quickshell.Replay;

/// <summary>
/// The captured streams, decompressed once into memory. Nothing here interprets a byte: a corpus
/// entry is what a terminal would have received, and the harness's job is to hand it over at the
/// speed the consumer can take it and not to be interesting itself.
/// </summary>
public sealed class Corpus
{
    private Corpus(string name, byte[] bytes)
    {
        Name = name;
        Bytes = bytes;
    }

    public string Name { get; }

    // CA1819 says a property should not return an array. Here it must: the harness feeds spans of
    // it in a hot loop, and a copy per call is exactly the cost this rig exists to measure.
#pragma warning disable CA1819
    public byte[] Bytes { get; }
#pragma warning restore CA1819

    public double Megabytes => Bytes.Length / 1024.0 / 1024.0;

    public string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes))[..16].ToLowerInvariant();

    public static IReadOnlyList<Corpus> Load(string directory)
    {
        List<Corpus> streams = [];

        foreach (string path in Directory.EnumerateFiles(directory, "*.raw.gz").Order(StringComparer.Ordinal))
        {
            using FileStream file = File.OpenRead(path);
            using GZipStream unzip = new(file, CompressionMode.Decompress);
            using MemoryStream memory = new();
            unzip.CopyTo(memory);

            streams.Add(new Corpus(Path.GetFileName(path).Replace(".raw.gz", "", StringComparison.Ordinal), memory.ToArray()));
        }

        return streams;
    }

    /// <summary>The directory holding the corpus, found from wherever the harness was started.</summary>
    public static string Find()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quickshell.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new DirectoryNotFoundException("the repository root is not above this executable")
            : Path.Combine(directory.FullName, "benchmarks", "corpus", "streams");
    }
}
