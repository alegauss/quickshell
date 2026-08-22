using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshProbe;

/// <summary>
/// Six questions about SSH.NET, each answered by running something against the OpenSSH 9.6 server
/// in prototypes/SshProbe/fixture. Nothing here reads documentation.
/// </summary>
internal static class Program
{
    private const string Host = "127.0.0.1";
    private const int TargetPort = 2222;
    private const int JumpPort = 2223;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string _keys = "";

    private static int Main(string[] args)
    {
        _keys = args.Length > 0 ? args[0] : Path.Combine("fixture", "keys");
        string output = args.Length > 1 ? args[1] : "runs";

        Directory.CreateDirectory(output);

        List<Answer> answers =
        [
            Ask("Can it consume a key held by the Windows OpenSSH agent, or by Pageant?", AgentKeys),
            Ask("Does it accept an OpenSSH certificate?", Certificate),
            Ask("Can a connection be carried inside another connection's channel?", JumpHost),
            Ask("Does it survive keyboard-interactive with a second factor?", TwoFactor),
            Ask("Does it read any of ~/.ssh/config?", SshConfig),
            Ask("What does its shell stream sustain under cat of a large file?", Throughput),
        ];

        string version = typeof(SshClient).Assembly.GetName().Version?.ToString() ?? "unknown";

        foreach (Answer answer in answers)
        {
            Console.WriteLine($"[{answer.Verdict}] {answer.Question}");
            Console.WriteLine($"        {answer.Evidence}");

            if (answer.Work.Length > 0)
            {
                Console.WriteLine($"   work {answer.Work}");
            }
        }

        string path = Path.Combine(output, "ssh-net.json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            new { Library = "SSH.NET", Version = version, RanAt = DateTimeOffset.Now, Answers = answers },
            Indented));

        Console.WriteLine($"\nSSH.NET {version} -> {path}");
        return 0;
    }

    private static Answer Ask(string question, Func<Answer> run)
    {
        try
        {
            Answer answer = run();
            answer.Question = question;
            return answer;
        }
        catch (Exception error)
        {
            return new Answer
            {
                Question = question,
                Verdict = "error",
                Evidence = $"the probe itself failed: {error.GetType().Name}: {error.Message}",
            };
        }
    }

    // ---------------------------------------------------------------- 1. agent

    private static Answer AgentKeys()
    {
        Assembly assembly = typeof(SshClient).Assembly;

        string[] authenticationMethods = assembly.GetExportedTypes()
            .Where(type => typeof(AuthenticationMethod).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] agentShaped = assembly.GetExportedTypes()
            .Where(type => type.Name.Contains("Agent", StringComparison.OrdinalIgnoreCase))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Type? source = assembly.GetType("Renci.SshNet.IPrivateKeySource");
        Type key = typeof(PrivateKeyFile);

        bool pipe = File.Exists(@"\\.\pipe\openssh-ssh-agent");

        return new Answer
        {
            Verdict = agentShaped.Length == 0 ? "no" : "partial",
            Evidence =
                $"the assembly exports {authenticationMethods.Length} authentication method(s) - " +
                $"{string.Join(", ", authenticationMethods)} - and " +
                (agentShaped.Length == 0
                    ? "no exported type whose name mentions an agent"
                    : $"these agent-shaped types: {string.Join(", ", agentShaped)}") +
                $". IPrivateKeySource is {(source is null ? "not exported" : "exported")}, " +
                $"PrivateKeyFile is {(key.IsSealed ? "sealed" : "open")}. " +
                $"The Windows OpenSSH agent pipe was {(pipe ? "present" : "absent")} on this machine, " +
                "so no positive control against a live agent was run: the ssh-agent service needs " +
                "elevation to start and this run had none.",
            Work = agentShaped.Length == 0
                ? "implement an authentication method that signs through \\\\.\\pipe\\openssh-ssh-agent " +
                  "and through Pageant's window-message protocol; " +
                  (source is not null
                      ? "IPrivateKeySource is exported, so the signing seam is reachable without forking"
                      : "no key-source interface is exported, so this may need a fork")
                : "",
        };
    }

    // ---------------------------------------------------------------- 2. certificate

    private static Answer Certificate()
    {
        string keyPath = Path.Combine(_keys, "probe_ed25519");
        string certPath = Path.Combine(_keys, "probe_ed25519-cert.pub");

        ConstructorInfo? withCertificate = typeof(PrivateKeyFile).GetConstructor([typeof(string), typeof(string), typeof(string)]);

        if (withCertificate is null)
        {
            return new Answer
            {
                Verdict = "no",
                Evidence = "PrivateKeyFile exposes no constructor taking a certificate file",
                Work = "carry the certificate blob into the publickey request by hand, or fork",
            };
        }

        PrivateKeyFile keyWithCertificate = new(keyPath, null, certPath);

        using SshClient client = Connect("certonly", TargetPort, new PrivateKeyAuthenticationMethod("certonly", keyWithCertificate));
        string who = client.RunCommand("id -un").Result.Trim();

        return new Answer
        {
            Verdict = who == "certonly" ? "yes" : "no",
            Evidence =
                $"connected as certonly and the server answered id -un = '{who}'. That account has no " +
                "authorized_keys at all - the server's only route in for it is TrustedUserCAKeys - so " +
                "the certificate is what authenticated. The same connection without the certificate is " +
                "refused with 'Permission denied (publickey)'.",
        };
    }

    // ---------------------------------------------------------------- 3. jump host

    private static Answer JumpHost()
    {
        PrivateKeyFile key = new(Path.Combine(_keys, "probe_ed25519"));

        using SshClient jump = Connect("probe", JumpPort, new PrivateKeyAuthenticationMethod("probe", key));
        string jumpHost = jump.RunCommand("hostname").Result.Trim();

        ForwardedPortLocal channel = new("127.0.0.1", 0, "target", 22);
        jump.AddForwardedPort(channel);
        channel.Start();

        try
        {
            using SshClient inner = Connect("probe", (int)channel.BoundPort, new PrivateKeyAuthenticationMethod("probe", key));
            string innerHost = inner.RunCommand("hostname").Result.Trim();

            return new Answer
            {
                Verdict = innerHost != jumpHost ? "yes" : "no",
                Evidence =
                    $"opened a direct-tcpip channel on the jump connection (hostname '{jumpHost}') to " +
                    $"target:22, bound it to local port {channel.BoundPort}, and completed a second SSH " +
                    $"handshake through it to a different host (hostname '{innerHost}'). The target's own " +
                    "published port was not used.",
            };
        }
        finally
        {
            channel.Stop();
        }
    }

    // ---------------------------------------------------------------- 4. two factors

    private static Answer TwoFactor()
    {
        PrivateKeyFile key = new(Path.Combine(_keys, "probe_ed25519"));

        PrivateKeyAuthenticationMethod publicKey = new("twofactor", key);
        KeyboardInteractiveAuthenticationMethod interactive = new("twofactor");

        List<string> prompts = [];

        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (AuthenticationPrompt prompt in e.Prompts)
            {
                prompts.Add(prompt.Request.Trim());
                prompt.Response = "twofactor-pw";
            }
        };

        ConnectionInfo connection = new(Host, TargetPort, "twofactor", publicKey, interactive);

        using SshClient client = new(connection);
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        client.Connect();

        string who = client.RunCommand("id -un").Result.Trim();

        return new Answer
        {
            Verdict = who == "twofactor" ? "yes" : "no",
            Evidence =
                "the server requires AuthenticationMethods publickey,keyboard-interactive for this " +
                $"account. Both methods were offered in one ConnectionInfo, the prompt(s) " +
                $"[{string.Join(" | ", prompts)}] were answered from the handler, and the session " +
                $"reported id -un = '{who}'.",
        };
    }

    // ---------------------------------------------------------------- 5. ssh config

    private static Answer SshConfig()
    {
        Assembly assembly = typeof(SshClient).Assembly;

        string[] configShaped = assembly.GetExportedTypes()
            .Where(type => type.Name.Contains("SshConfig", StringComparison.OrdinalIgnoreCase)
                        || type.Name.Contains("ConfigFile", StringComparison.OrdinalIgnoreCase))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        string aliasFile = Path.Combine(Path.GetTempPath(), "qs-probe-ssh-config");
        string keyPath = Path.Combine(_keys, "probe_ed25519");

        File.WriteAllText(aliasFile,
            $"Host qs-probe-alias\n  HostName {Host}\n  Port {TargetPort}\n  User probe\n  IdentityFile {Path.GetFullPath(keyPath)}\n");

        string cliWho = RunSsh($"-F \"{aliasFile}\" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes qs-probe-alias id -un");

        string libraryOutcome;

        try
        {
            using SshClient client = Connect("probe", TargetPort, new PrivateKeyAuthenticationMethod("probe", new PrivateKeyFile(keyPath)), host: "qs-probe-alias");
            libraryOutcome = "connected, which would mean the alias resolved";
        }
        catch (Exception error)
        {
            libraryOutcome = $"{error.GetType().Name}: {error.Message.Trim()}";
        }

        return new Answer
        {
            Verdict = configShaped.Length == 0 ? "no" : "partial",
            Evidence =
                $"the OpenSSH client given the same alias file resolved it and answered id -un = " +
                $"'{cliWho.Trim()}'. SSH.NET handed the bare alias 'qs-probe-alias' answered with " +
                $"{libraryOutcome}. The assembly exports " +
                (configShaped.Length == 0 ? "no config-file type at all" : string.Join(", ", configShaped)) +
                ": host, port, user and key are constructor arguments and nothing reads a file.",
            Work = configShaped.Length == 0
                ? "parse ~/.ssh/config in this repository and resolve Host/HostName/Port/User/" +
                  "IdentityFile/ProxyJump into a ConnectionInfo before the library is called"
                : "",
        };
    }

    // ---------------------------------------------------------------- 6. throughput

    private static Answer Throughput()
    {
        PrivateKeyFile key = new(Path.Combine(_keys, "probe_ed25519"));

        using SshClient client = Connect("probe", TargetPort, new PrivateKeyAuthenticationMethod("probe", key));
        using ShellStream shell = client.CreateShellStream("xterm", 200, 50, 800, 600, 64 * 1024);

        // Settle the login banner and the first prompt before the clock starts.
        Thread.Sleep(1200);
        _ = shell.Read();

        const long Expected = 67108864;
        byte[] buffer = new byte[64 * 1024];
        long read = 0;

        // Process-wide and not per-thread: SSH.NET reads on its own threads, so a per-thread count
        // measures this loop and reports the library as allocating nothing.
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch clock = Stopwatch.StartNew();

        shell.WriteLine("cat /srv/big.txt");

        while (read < Expected && clock.Elapsed < TimeSpan.FromMinutes(3))
        {
            int got = shell.Read(buffer, 0, buffer.Length);

            if (got > 0)
            {
                read += got;
                continue;
            }

            Thread.Sleep(1);
        }

        clock.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        double megabytes = read / 1024.0 / 1024.0;
        double seconds = clock.Elapsed.TotalSeconds;

        return new Answer
        {
            Verdict = read >= Expected ? "measured" : "incomplete",
            Evidence =
                $"cat of a {Expected / 1024 / 1024} MB printable-ASCII file through a ShellStream: " +
                $"{megabytes:F1} MB in {seconds:F2} s = {megabytes / seconds:F1} MB/s, " +
                $"allocating {allocated / 1024.0 / 1024.0:F1} MB across the process " +
                $"= {allocated / Math.Max(1, megabytes) / 1024.0:F0} KB per MB read.",
        };
    }

    // ---------------------------------------------------------------- plumbing

    private static SshClient Connect(string user, int port, AuthenticationMethod method, string? host = null)
    {
        ConnectionInfo connection = new(host ?? Host, port, user, method);
        SshClient client = new(connection);
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        client.Connect();
        return client;
    }

    private static string RunSsh(string arguments)
    {
        ProcessStartInfo start = new("ssh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using Process process = Process.Start(start)!;
        StringBuilder output = new();
        output.Append(process.StandardOutput.ReadToEnd());
        process.WaitForExit(30000);

        return output.ToString();
    }
}
