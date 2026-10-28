using System.Net.NetworkInformation;
using System.Windows.Threading;
using Windows.Networking.Connectivity;
using Connectivity = Windows.Networking.Connectivity.NetworkInformation;

namespace DynamicIsland;

sealed class NetworkService
{
    public enum Link { None, Wifi, Wired, Mobile }

    public readonly record struct State(Link Link, string Name, bool Internet, string Vpn)
    {
        public string[] Tunnels => Vpn.Split(TunnelSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    const char TunnelSeparator = '\n';
    const int PropVirtualInterface = 53;
    static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);
    static readonly string[] VpnHints = ["VPN", "TAP-", "OpenVPN"];
    static readonly State Unread = new(Link.None, "", false, "");

    readonly Dispatcher _ui;
    readonly DelayedAction _settle;
    State _state;
    bool _reading;

    public NetworkService(Dispatcher ui)
    {
        _ui = ui;
        _settle = new DelayedAction(() => _ = RefreshAsync());
    }

    public event Action<State, State>? Changed;

    public async Task StartAsync()
    {
        _state = await Task.Run(ReadState) ?? Unread;
        Connectivity.NetworkStatusChanged += _ => ScheduleRefresh();
        NetworkChange.NetworkAddressChanged += (_, _) => ScheduleRefresh();
    }

    void ScheduleRefresh() => _ui.InvokeAsync(() => _settle.Start(SettleTime));

    async Task RefreshAsync()
    {
        if (_reading)
        {
            _settle.Start(SettleTime);
            return;
        }

        _reading = true;
        State? read;
        try { read = await Task.Run(ReadState); }
        finally { _reading = false; }

        if (read is not { } now || now == _state) return;
        State was = _state;
        _state = now;
        Changed?.Invoke(was, now);
    }

    static State? ReadState()
    {
        Link link = Link.None;
        string name = "";
        bool internet = false;
        try
        {
            ConnectionProfile? profile = Connectivity.GetInternetConnectionProfile();
            if (profile != null)
            {
                link = profile.IsWlanConnectionProfile ? Link.Wifi : profile.IsWwanConnectionProfile ? Link.Mobile : Link.Wired;
                name = profile.ProfileName ?? "";
                internet = profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            return new State(link, name, internet, ReadTunnels());
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    static string ReadTunnels() =>
        string.Join(TunnelSeparator, NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && IsTunnel(nic))
            .Select(nic => nic.Name)
            .Order(StringComparer.Ordinal));

    static bool IsTunnel(NetworkInterface nic) =>
        nic.NetworkInterfaceType == NetworkInterfaceType.Ppp
        || (int)nic.NetworkInterfaceType == PropVirtualInterface
        || VpnHints.Any(hint => nic.Description.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
