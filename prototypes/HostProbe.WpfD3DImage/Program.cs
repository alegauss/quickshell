using System.Windows;
using HostProbe.Core;

namespace HostProbe.WpfD3DImage;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "runs";

        Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ProbeWindow window = new();
        window.Show();

        new Thread(() => ProbeDriver.Run(window, output)) { IsBackground = true, Name = "probe-driver" }.Start();

        application.Run();
    }
}
