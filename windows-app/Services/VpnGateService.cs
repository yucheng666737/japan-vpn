using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using JapanVPN.Models;

namespace JapanVPN.Services;

public sealed class VpnGateService
{
    private const string Endpoint = "https://www.vpngate.net/api/iphone/";
    private readonly string _cacheDir;
    private readonly string _cacheCsvPath;
    private readonly HttpClient _httpClient;

    public VpnGateService()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JapanVPN");
        Directory.CreateDirectory(_cacheDir);
        _cacheCsvPath = Path.Combine(_cacheDir, "vpngate_cache.csv");

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(18)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) JapanVPN/1.0");
    }

    public async Task<ServerFetchResult> FetchJapanServersAsync(bool allowCacheFallback = true, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _httpClient.GetAsync(Endpoint, ct);
            resp.EnsureSuccessStatusCode();
            var csv = await resp.Content.ReadAsStringAsync(ct);
            var parsed = ParseJapanCsv(csv, fromCache: false);
            if (parsed.Servers.Count > 0)
            {
                await File.WriteAllTextAsync(_cacheCsvPath, csv, Encoding.UTF8, ct);
                return parsed;
            }
            throw new InvalidOperationException("VPN Gate 回傳空清單");
        }
        catch when (allowCacheFallback)
        {
            var cached = LoadCachedResult();
            if (cached is { Servers.Count: > 0 })
            {
                return cached;
            }
            throw;
        }
    }

    public ServerFetchResult? LoadCachedResult()
    {
        try
        {
            if (!File.Exists(_cacheCsvPath)) return null;
            var csv = File.ReadAllText(_cacheCsvPath, Encoding.UTF8);
            var ts = new DateTimeOffset(File.GetLastWriteTimeUtc(_cacheCsvPath)).ToUnixTimeMilliseconds();
            return ParseJapanCsv(csv, fromCache: true, overrideTimestamp: ts);
        }
        catch
        {
            return null;
        }
    }

    public ServerFetchResult ParseJapanCsv(string csvContent, bool fromCache, long? overrideTimestamp = null)
    {
        var list = new List<VpnServer>();
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalJapanCount = 0;
        int excluded219Count = 0;

        using var reader = new StringReader(csvContent);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('*') || line.StartsWith('#')) continue;

            var cols = line.Split(',', 15);
            if (cols.Length < 15) continue;

            var countryLong = cols[5].Trim();
            var countryShort = cols[6].Trim();
            bool isJapan = countryShort.Equals("JP", StringComparison.OrdinalIgnoreCase) ||
                           countryLong.Equals("Japan", StringComparison.OrdinalIgnoreCase);
            if (!isJapan) continue;

            totalJapanCount++;

            var hostName = cols[0].Trim();
            var ip = cols[1].Trim();

            if (IsBlocked219Ip(ip))
            {
                excluded219Count++;
                continue;
            }

            var ovpnBase64 = cols[14].Trim();
            if (ovpnBase64.Length == 0) continue;

            string ovpnConfig;
            try
            {
                var bytes = Convert.FromBase64String(ovpnBase64);
                ovpnConfig = Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                continue;
            }

            if (!ovpnConfig.Contains("remote ", StringComparison.Ordinal) ||
                !ovpnConfig.Contains("<ca>", StringComparison.Ordinal))
            {
                continue;
            }

            var (remoteHost, port, protocol) = ExtractRemoteInfo(ovpnConfig, ip);
            if (IsBlocked219Ip(remoteHost))
            {
                excluded219Count++;
                continue;
            }

            if (!seenIps.Add(ip)) continue;

            long.TryParse(cols[2].Trim(), out long score);
            if (!int.TryParse(cols[3].Trim(), out int ping)) ping = 999;
            long.TryParse(cols[4].Trim(), out long speedBps);
            int.TryParse(cols[7].Trim(), out int sessions);
            long.TryParse(cols[8].Trim(), out long uptimeMs);
            var operatorName = cols[12].Trim();

            list.Add(new VpnServer(
                HostName: hostName,
                Ip: ip,
                RemoteHost: remoteHost,
                Port: port,
                Protocol: protocol,
                Score: score,
                Ping: ping,
                SpeedBps: speedBps,
                Sessions: sessions,
                UptimeMs: uptimeMs,
                Operator: operatorName,
                OvpnConfig: ovpnConfig));
        }

        var sorted = list.OrderByDescending(server =>
        {
            double speedFactor = Math.Min(server.SpeedBps / 1_000_000.0, 200.0) * 12.0;
            double pingPenalty = Math.Clamp(server.Ping, 1, 500) * 3.0;
            double scoreFactor = Math.Min(server.Score / 100_000.0, 50.0) * 6.0;
            double tcpBonus = server.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase) ? 15.0 : 0.0;
            return speedFactor + scoreFactor + tcpBonus - pingPenalty;
        }).ToList();

        return new ServerFetchResult(
            Servers: sorted,
            TotalJapanCount: totalJapanCount,
            Excluded219Count: excluded219Count,
            FetchedAtMillis: overrideTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FromCache: fromCache);
    }

    public async Task<bool> ProbeTcpReachableAsync(string host, int port, int timeoutMs = 2500, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (string Host, int Port, string Protocol) ExtractRemoteInfo(string ovpnConfig, string fallbackIp)
    {
        string host = fallbackIp;
        int port = 443;
        string protocol = "tcp";

        using var reader = new StringReader(ovpnConfig);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith("proto ", StringComparison.OrdinalIgnoreCase))
            {
                var p = line["proto ".Length..].Trim().ToLowerInvariant();
                if (p.StartsWith("udp")) protocol = "udp";
                else if (p.StartsWith("tcp")) protocol = "tcp";
            }
            else if (line.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Regex.Split(line, @"\s+");
                if (parts.Length >= 2) host = parts[1].Trim();
                if (parts.Length >= 3 && int.TryParse(parts[2].Trim(), out int parsedPort)) port = parsedPort;
            }
        }

        return (host, port, protocol);
    }

    public static bool IsBlocked219Ip(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && ip.Trim().StartsWith("219.", StringComparison.Ordinal);
}
