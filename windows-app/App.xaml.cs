using JapanVPN.Services;

namespace JapanVPN;

public partial class App : System.Windows.Application
{
    public VpnConnectionManager VpnManager { get; private set; } = null!;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        VpnManager = new VpnConnectionManager();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        VpnManager?.ShutdownSynchronously();
        base.OnExit(e);
    }
}
