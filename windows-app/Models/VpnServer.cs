namespace JapanVPN.Models;

public sealed record VpnServer(
    string HostName,
    string Ip,
    string RemoteHost,
    int Port,
    string Protocol,
    long Score,
    int Ping,
    long SpeedBps,
    int Sessions,
    long UptimeMs,
    string Operator,
    string OvpnConfig)
{
    public double SpeedMbps => SpeedBps / 1_000_000.0;

    public int SignalBars
    {
        get
        {
            int points = 0;
            points += Ping switch
            {
                >= 1 and <= 25 => 3,
                >= 26 and <= 60 => 2,
                >= 61 and <= 120 => 1,
                _ => 0
            };
            points += SpeedMbps switch
            {
                >= 30.0 => 2,
                >= 10.0 => 1,
                _ => 0
            };
            return Math.Clamp(points, 1, 5);
        }
    }

    public string FormattedSpeed() => SpeedMbps switch
    {
        >= 100.0 => $"{SpeedMbps:0} Mbps",
        >= 1.0 => $"{SpeedMbps:0.0} Mbps",
        _ when SpeedBps >= 1_000 => $"{SpeedBps / 1_000.0:0} Kbps",
        _ => "N/A"
    };

    public string FormattedPing() => Ping > 0 ? $"{Ping}ms" : "--";
}

public sealed record ServerFetchResult(
    IReadOnlyList<VpnServer> Servers,
    int TotalJapanCount,
    int Excluded219Count,
    long FetchedAtMillis,
    bool FromCache = false);
