using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JapanVPN.Services;

public sealed class OpenVpnRunner : IAsyncDisposable
{
    private readonly string _configPath;
    private Process? _process;
    private TcpClient? _mgmtClient;
    private StreamWriter? _mgmtWriter;
    private CancellationTokenSource? _runnerCts;
    private int _mgmtPort;
    private long _lastInBytes;
    private long _lastOutBytes;
    private bool _stoppedExplicitly;
    private bool _reportedTerminal;

    public event Action? Connected;
    public event Action<string>? Reconnecting;
    public event Action<string>? Failed;
    public event Action<string>? LogLine;
    public event Action<long, long, long, long>? ByteCountUpdated;

    public OpenVpnRunner()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JapanVPN");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "current.ovpn");
    }

    public static string PatchConfig(string rawConfig)
    {
        var sb = new StringBuilder();
        bool inXmlBlock = false;

        using var reader = new StringReader(rawConfig);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith('<') && !trimmed.StartsWith("</", StringComparison.Ordinal))
            {
                inXmlBlock = true;
            }
            else if (trimmed.StartsWith("</", StringComparison.Ordinal))
            {
                inXmlBlock = false;
            }

            if (!inXmlBlock && ShouldStripDirective(trimmed))
            {
                continue;
            }

            sb.AppendLine(rawLine);
        }

        sb.AppendLine();
        sb.AppendLine("# JapanVPN patched directives");
        sb.AppendLine("verb 3");
        sb.AppendLine("resolv-retry 10");
        sb.AppendLine("connect-retry 2 8");
        sb.AppendLine("connect-retry-max 1");
        sb.AppendLine("connect-timeout 10");
        sb.AppendLine("data-ciphers AES-128-CBC:AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305");
        sb.AppendLine("data-ciphers-fallback AES-128-CBC");
        sb.AppendLine("tls-cert-profile insecure");

        return sb.ToString();
    }

    private static bool ShouldStripDirective(string trimmedLine)
    {
        if (trimmedLine.Length == 0 || trimmedLine.StartsWith('#') || trimmedLine.StartsWith(';'))
        {
            return false;
        }

        string[] prefixes =
        [
            "resolv-retry",
            "connect-retry",
            "connect-retry-max",
            "connect-timeout",
            "data-ciphers",
            "data-ciphers-fallback",
            "tls-cert-profile",
            "management",
            "verb",
            "explicit-exit-notify"
        ];

        foreach (var p in prefixes)
        {
            if (trimmedLine.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.StartsWith(p + "\t", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task StartAsync(string openVpnExePath, string rawOvpnConfig, CancellationToken ct)
    {
        await StopAsync();

        _stoppedExplicitly = false;
        _reportedTerminal = false;
        _lastInBytes = 0;
        _lastOutBytes = 0;

        var patched = PatchConfig(rawOvpnConfig);
        await File.WriteAllTextAsync(_configPath, patched, Encoding.UTF8, ct);

        _mgmtPort = AllocateFreeTcpPort();
        _runnerCts = new CancellationTokenSource();

        var psi = new ProcessStartInfo
        {
            FileName = openVpnExePath,
            Arguments = $"--config \"{_configPath}\" --management 127.0.0.1 {_mgmtPort}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) HandleOutputLine(e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) HandleOutputLine(e.Data);
        };
        _process.Exited += (_, _) =>
        {
            if (!_stoppedExplicitly && !_reportedTerminal)
            {
                _reportedTerminal = true;
                var code = -1;
                try { code = _process?.ExitCode ?? -1; } catch { }
                Failed?.Invoke($"OpenVPN 程序已結束 (code={code})");
            }
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException("無法啟動 openvpn.exe");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _ = Task.Run(() => RunManagementLoopAsync(_mgmtPort, _runnerCts.Token));
    }

    private void HandleOutputLine(string line)
    {
        if (line.Contains("Initialization Sequence Completed", StringComparison.OrdinalIgnoreCase))
        {
            Connected?.Invoke();
            return;
        }

        if (line.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase))
        {
            if (!_stoppedExplicitly && !_reportedTerminal)
            {
                _reportedTerminal = true;
                Failed?.Invoke("驗證失敗 (AUTH_FAILED)");
            }
            return;
        }

        if (line.Contains("Exiting due to fatal error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Fatal error", StringComparison.OrdinalIgnoreCase))
        {
            LogLine?.Invoke(line);
            if (!_stoppedExplicitly && !_reportedTerminal)
            {
                _reportedTerminal = true;
                Failed?.Invoke("發生連線錯誤");
            }
            return;
        }

        if (line.Contains("SIGUSR1", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Connection reset, restarting", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Inactivity timeout", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Restart pause", StringComparison.OrdinalIgnoreCase))
        {
            Reconnecting?.Invoke("通道不穩重連中");
        }
    }

    private async Task RunManagementLoopAsync(int port, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 25 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, ct);
                _mgmtClient = client;

                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                _mgmtWriter = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

                await _mgmtWriter.WriteLineAsync("bytecount 2");

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;

                    if (line.StartsWith(">BYTECOUNT:", StringComparison.Ordinal))
                    {
                        var payload = line[">BYTECOUNT:".Length..];
                        var parts = payload.Split(',');
                        if (parts.Length == 2 &&
                            long.TryParse(parts[0], out long inBytes) &&
                            long.TryParse(parts[1], out long outBytes))
                        {
                            long diffIn = Math.Max(0, inBytes - _lastInBytes);
                            long diffOut = Math.Max(0, outBytes - _lastOutBytes);
                            _lastInBytes = inBytes;
                            _lastOutBytes = outBytes;
                            ByteCountUpdated?.Invoke(diffIn / 2, diffOut / 2, inBytes, outBytes);
                        }
                    }
                }
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task StopAsync()
    {
        _stoppedExplicitly = true;

        try
        {
            _runnerCts?.Cancel();
        }
        catch { }

        try
        {
            if (_mgmtWriter is not null)
            {
                await _mgmtWriter.WriteLineAsync("signal SIGTERM");
            }
        }
        catch { }

        var proc = _process;
        _process = null;

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    using var waitCts = new CancellationTokenSource(2000);
                    try
                    {
                        await proc.WaitForExitAsync(waitCts.Token);
                    }
                    catch
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }

        try { _mgmtWriter?.Dispose(); } catch { }
        try { _mgmtClient?.Dispose(); } catch { }
        try { _runnerCts?.Dispose(); } catch { }
        _mgmtWriter = null;
        _mgmtClient = null;
        _runnerCts = null;
    }

    public void StopSynchronously()
    {
        _stoppedExplicitly = true;
        try { _runnerCts?.Cancel(); } catch { }
        try { _mgmtWriter?.WriteLine("signal SIGTERM"); } catch { }

        var proc = _process;
        _process = null;
        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited && !proc.WaitForExit(1500))
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }

        try { _mgmtWriter?.Dispose(); } catch { }
        try { _mgmtClient?.Dispose(); } catch { }
        try { _runnerCts?.Dispose(); } catch { }
        _mgmtWriter = null;
        _mgmtClient = null;
        _runnerCts = null;
    }

    private static int AllocateFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
