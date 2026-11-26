using System.Diagnostics;
using System.IO;

namespace JapanVPN.Services;

public static class OpenVpnLocator
{
    private static readonly string[] CandidatePaths =
    [
        Path.Combine(AppContext.BaseDirectory, "openvpn", "openvpn.exe"),
        @"C:\Program Files\OpenVPN\bin\openvpn.exe",
        @"C:\Program Files (x86)\OpenVPN\bin\openvpn.exe",
        @"C:\Program Files\OpenVPN Connect\ovpnagent.exe" // not CLI openvpn.exe, kept separate below
    ];

    public static string? FindOpenVpnPath()
    {
        foreach (var path in CandidatePaths.Take(3))
        {
            if (File.Exists(path)) return path;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "openvpn.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH segments
            }
        }

        return null;
    }

    public static async Task<(bool Success, string Message)> InstallOpenVpnViaWingetAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install --id OpenVPNTechnologies.OpenVPN -e --accept-source-agreements --accept-package-arguments",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "無法啟動 winget 安裝程式");
            }

            var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errorTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await outputTask;
            var stderr = await errorTask;

            var found = FindOpenVpnPath();
            if (proc.ExitCode == 0 || found is not null)
            {
                return (true, "OpenVPN 安裝成功！");
            }

            return (false, $"winget 回傳代碼 {proc.ExitCode}：{(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
        }
        catch (Exception ex)
        {
            return (false, $"無法透過 winget 安裝：{ex.Message}");
        }
    }

    public static void OpenManualDownloadPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://openvpn.net/community-downloads/",
            UseShellExecute = true
        });
    }
}
