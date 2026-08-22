using HostProbe.Core;
using Microsoft.UI.Xaml;

namespace HostProbe.WinUI;

public partial class App : Application
{
    private ProbeHost? _host;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string output = Environment.GetCommandLineArgs() is [_, string first, ..] ? first : "runs";

        _host = new ProbeHost();
        _host.Show();

        new Thread(() => ProbeDriver.Run(_host, output)) { IsBackground = true, Name = "probe-driver" }.Start();
    }
}
