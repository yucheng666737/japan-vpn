using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.NetworkInformation;
using JapanVPN.Models;

namespace JapanVPN.Services;

public enum ConnectionPhase
{
    Disconnected,
    Fetching,
    Probing,
    Connecting,
    Switching,
    Connected
}

public sealed record VpnUiState
{
    public ConnectionPhase Phase { get; init; } = ConnectionPhase.Disconnected;
    public string StatusTitle { get; init; } = "未連線";
    public string StatusDetail { get; init; } = "點擊電源鍵自動連線最佳日本節點";
    public IReadOnlyList<VpnServer> Servers { get; init; } = [];
    public VpnServer? SelectedServer { get; init; }
    public VpnServer? ActiveServer { get; init; }
    public int TotalJapanCount { get; init; }
    public int Excluded219Count { get; init; }
    public IReadOnlySet<string> FailedIps { get; init; } = new HashSet<string>();
    public bool AutoFailoverEnabled { get; init; } = true;
    public bool AutoRefreshEnabled { get; init; } = true;
    public bool HealthCheckEnabled { get; init; } = true;
    public long DownloadBps { get; init; }
    public long UploadBps { get; init; }
    public long ConnectedSeconds { get; init; }
    public bool IsRefreshing { get; init; }
    public long LastFetchedAtMillis { get; init; }
    public string AttemptProgress { get; init; } = string.Empty;
    public IReadOnlyList<string> Logs { get; init; } = [];
    public string? OpenVpnPath { get; init; }
}

public sealed class VpnConnectionManager
{
    private const int TcpProbeTimeoutMs = 2500;
    private const int ConnectTimeoutMs = 15_000;
    private const int DropGraceMs = 5_000;
    private const int HealthWarmupMs = 12_000;
    private const int HealthIntervalMs = 10_000;
    private const int HealthRecheckDelayMs = 4_000;
    private const int HealthProbeTimeoutMs = 4_000;
    private const long FailedCooldownMs = 10 * 60 * 1000L;
    private const long ServerListStaleMs = 3 * 60 * 1000L;
    private const int AutoRefreshIntervalMs = 3 * 60 * 1000;
    private const int MaxLogLines = 120;

    private static readonly string[] HealthProbeUrls =
    [
        "https://connectivitycheck.gstatic.com/generate_204",
        "https://cp.cloudflare.com/generate_204"
    ];

    private readonly VpnGateService _vpnGateService = new();
    private readonly OpenVpnRunner _runner = new();
    private readonly HttpClient _healthClient;
    private readonly ConcurrentDictionary<string, long> _failedTimestamps = new();
    private readonly object _stateLock = new();

    private VpnUiState _state = new();
    private long _activeSessionId;
    private bool _userWantsConnected;
    private long _connectedSinceMillis;

    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _timerCts;
    private CancellationTokenSource? _graceCts;
    private CancellationTokenSource? _healthCts;
    private CancellationTokenSource? _autoRefreshCts;

    private TaskCompletionSource<AttemptOutcome>? _currentAttemptTcs;
    private long _currentAttemptSessionId;

    public event Action<VpnUiState>? StateChanged;

    private sealed record AttemptOutcome(bool IsConnected, string? Reason, bool IsCancelled)
    {
        public static AttemptOutcome Connected() => new(true, null, false);
        public static AttemptOutcome Failed(string reason) => new(false, reason, false);
        public static AttemptOutcome Cancelled() => new(false, null, true);
    }

    public VpnConnectionManager()
    {
        _healthClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMilliseconds(HealthProbeTimeoutMs)
        };

        _runner.Connected += OnRunnerConnected;
        _runner.Reconnecting += OnRunnerReconnecting;
        _runner.Failed += OnRunnerFailed;
        _runner.LogLine += line => AppendLog($"[OpenVPN] {line}");
        _runner.ByteCountUpdated += OnRunnerByteCountUpdated;

        var ovpnPath = OpenVpnLocator.FindOpenVpnPath();
        var cached = _vpnGateService.LoadCachedResult();
        UpdateState(s => s with
        {
            OpenVpnPath = ovpnPath,
            Servers = cached?.Servers ?? [],
            SelectedServer = cached?.Servers.FirstOrDefault(),
            TotalJapanCount = cached?.TotalJapanCount ?? 0,
            Excluded219Count = cached?.Excluded219Count ?? 0,
            LastFetchedAtMillis = cached?.FetchedAtMillis ?? 0
        });

        _ = RefreshServersAsync(silent: true);
        StartPeriodicAutoRefresh();
    }

    public VpnUiState CurrentState
    {
        get
        {
            lock (_stateLock) return _state;
        }
    }

    public void RecheckOpenVpnPath()
    {
        var path = OpenVpnLocator.FindOpenVpnPath();
        UpdateState(s => s with { OpenVpnPath = path });
        if (path is not null)
        {
            AppendLog($"已偵測到 OpenVPN 核心：{path}");
        }
    }

    public void SetAutoFailover(bool enabled)
    {
        UpdateState(s => s with { AutoFailoverEnabled = enabled });
        AppendLog(enabled ? "已開啟：連線失敗/斷線時自動重連其他日本節點" : "已關閉：自動重連");
    }

    public void SetAutoRefresh(bool enabled)
    {
        UpdateState(s => s with { AutoRefreshEnabled = enabled });
        if (enabled)
        {
            AppendLog("已開啟：每 3 分鐘背景自動抓取最新日本節點");
            StartPeriodicAutoRefresh();
        }
        else
        {
            AppendLog("已關閉：背景定時自動抓取");
            _autoRefreshCts?.Cancel();
            _autoRefreshCts = null;
        }
    }

    public void SetHealthCheckEnabled(bool enabled)
    {
        UpdateState(s => s with { HealthCheckEnabled = enabled });
        if (enabled)
        {
            AppendLog("已開啟：防假連線／斷流主動健康檢查");
            if (CurrentState.Phase == ConnectionPhase.Connected && _userWantsConnected)
            {
                StartHealthCheck(_activeSessionId);
            }
        }
        else
        {
            AppendLog("已關閉：防假連線健康檢查");
            _healthCts?.Cancel();
            _healthCts = null;
        }
    }

    public void ClearLogs()
    {
        UpdateState(s => s with { Logs = [] });
    }

    public async Task RefreshServersAsync(bool silent = false)
    {
        if (CurrentState.IsRefreshing) return;

        UpdateState(s => s with { IsRefreshing = true });
        if (!silent)
        {
            AppendLog("正在從 VPN Gate 抓取最新日本節點清單...");
        }

        try
        {
            var fetched = await _vpnGateService.FetchJapanServersAsync(allowCacheFallback: true);
            PruneExpiredFailures();

            UpdateState(s =>
            {
                var nextSelected = fetched.Servers.FirstOrDefault(x => x.Ip == s.SelectedServer?.Ip)
                    ?? fetched.Servers.FirstOrDefault(x => !IsRecentlyFailed(x.Ip))
                    ?? fetched.Servers.FirstOrDefault();

                return s with
                {
                    Servers = fetched.Servers,
                    SelectedServer = nextSelected,
                    TotalJapanCount = fetched.TotalJapanCount,
                    Excluded219Count = fetched.Excluded219Count,
                    LastFetchedAtMillis = fetched.FetchedAtMillis,
                    IsRefreshing = false
                };
            });

            AppendLog(
                $"已自動更新日本節點：共 {fetched.TotalJapanCount} 個，已排除 {fetched.Excluded219Count} 個 219 開頭，可用 {fetched.Servers.Count} 個");
        }
        catch (Exception ex)
        {
            UpdateState(s => s with { IsRefreshing = false });
            if (!silent)
            {
                AppendLog($"抓取節點清單失敗：{ex.Message}");
            }
        }
    }

    public void ConnectAuto()
    {
        StartConnectionPipeline(CurrentState.SelectedServer);
    }

    public void ConnectToServer(VpnServer server)
    {
        if (VpnGateService.IsBlocked219Ip(server.Ip))
        {
            AppendLog($"已略過 219 開頭節點：{server.Ip}");
            return;
        }
        _failedTimestamps.TryRemove(server.Ip, out _);
        SyncFailedIps();
        UpdateState(s => s with { SelectedServer = server });
        StartConnectionPipeline(server);
    }

    public void SwitchToNextServer()
    {
        var currentIp = CurrentState.ActiveServer?.Ip ?? CurrentState.SelectedServer?.Ip;
        if (!string.IsNullOrEmpty(currentIp))
        {
            MarkFailed(currentIp);
            AppendLog($"手動切換節點，暫時略過 {currentIp}");
        }
        StartConnectionPipeline(null);
    }

    public async Task DisconnectAsync()
    {
        _userWantsConnected = false;
        Interlocked.Increment(ref _activeSessionId);
        _currentAttemptTcs?.TrySetResult(AttemptOutcome.Cancelled());
        _currentAttemptTcs = null;

        CancelInFlightOperations();
        await _runner.StopAsync();
        AppendLog("使用者已手動斷開連線");

        UpdateState(s => s with
        {
            Phase = ConnectionPhase.Disconnected,
            StatusTitle = "未連線",
            StatusDetail = "已安全斷開連線",
            ActiveServer = null,
            DownloadBps = 0,
            UploadBps = 0,
            ConnectedSeconds = 0,
            AttemptProgress = string.Empty
        });
    }

    public void ShutdownSynchronously()
    {
        _userWantsConnected = false;
        Interlocked.Increment(ref _activeSessionId);
        _currentAttemptTcs?.TrySetResult(AttemptOutcome.Cancelled());
        CancelInFlightOperations();
        _autoRefreshCts?.Cancel();
        _runner.StopSynchronously();
    }

    private void CancelInFlightOperations()
    {
        try { _connectCts?.Cancel(); } catch { }
        try { _timerCts?.Cancel(); } catch { }
        try { _graceCts?.Cancel(); } catch { }
        try { _healthCts?.Cancel(); } catch { }
        _connectCts = null;
        _timerCts = null;
        _graceCts = null;
        _healthCts = null;
    }

    private void StartPeriodicAutoRefresh()
    {
        _autoRefreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autoRefreshCts = cts;

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(AutoRefreshIntervalMs, cts.Token);
                    var st = CurrentState;
                    if (st.AutoRefreshEnabled && !st.IsRefreshing && st.Phase != ConnectionPhase.Fetching)
                    {
                        await RefreshServersAsync(silent: true);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { }
            }
        });
    }

    private void StartConnectionPipeline(VpnServer? preferredServer)
    {
        var openVpnPath = OpenVpnLocator.FindOpenVpnPath();
        UpdateState(s => s with { OpenVpnPath = openVpnPath });
        if (string.IsNullOrEmpty(openVpnPath))
        {
            AppendLog("尚未安裝 OpenVPN 核心，請先點擊「一鍵安裝 OpenVPN」");
            UpdateState(s => s with
            {
                Phase = ConnectionPhase.Disconnected,
                StatusTitle = "缺少 OpenVPN 核心",
                StatusDetail = "請先點擊上方「一鍵安裝 OpenVPN」完成安裝",
                ActiveServer = null,
                AttemptProgress = string.Empty
            });
            return;
        }

        _userWantsConnected = true;
        long sessionId = Interlocked.Increment(ref _activeSessionId);
        _currentAttemptTcs?.TrySetResult(AttemptOutcome.Cancelled());
        _currentAttemptTcs = null;

        CancelInFlightOperations();
        var cts = new CancellationTokenSource();
        _connectCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await _runner.StopAsync();
                if (cts.IsCancellationRequested || sessionId != Interlocked.Read(ref _activeSessionId)) return;

                var cacheAge = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - CurrentState.LastFetchedAtMillis;
                if (CurrentState.Servers.Count == 0 || cacheAge > ServerListStaleMs)
                {
                    UpdateState(s => s with
                    {
                        Phase = ConnectionPhase.Fetching,
                        StatusTitle = "正在抓取日本 VPN...",
                        StatusDetail = "自動篩選日本節點並排除 219 開頭 IP",
                        AttemptProgress = string.Empty
                    });

                    await RefreshServersAsync(silent: false);
                    if (cts.IsCancellationRequested || sessionId != Interlocked.Read(ref _activeSessionId)) return;

                    if (CurrentState.Servers.Count == 0)
                    {
                        _userWantsConnected = false;
                        UpdateState(s => s with
                        {
                            Phase = ConnectionPhase.Disconnected,
                            StatusTitle = "抓取節點失敗",
                            StatusDetail = "請檢查網路連線後重試",
                            AttemptProgress = string.Empty
                        });
                        return;
                    }
                }

                bool refreshedDuringLoop = false;
                int attemptIndex = 0;
                var nextPreferred = preferredServer;

                while (!cts.IsCancellationRequested &&
                       _userWantsConnected &&
                       sessionId == Interlocked.Read(ref _activeSessionId))
                {
                    PruneExpiredFailures();
                    var allServers = CurrentState.Servers.Where(s => !VpnGateService.IsBlocked219Ip(s.Ip)).ToList();
                    if (allServers.Count == 0)
                    {
                        _userWantsConnected = false;
                        UpdateState(s => s with
                        {
                            Phase = ConnectionPhase.Disconnected,
                            StatusTitle = "無可用日本節點",
                            StatusDetail = "請稍後重新整理節點清單"
                        });
                        return;
                    }

                    VpnServer? candidate = null;
                    if (nextPreferred is not null && !VpnGateService.IsBlocked219Ip(nextPreferred.Ip))
                    {
                        candidate = nextPreferred;
                        nextPreferred = null;
                    }
                    else
                    {
                        candidate = allServers.FirstOrDefault(s => !IsRecentlyFailed(s.Ip));
                    }

                    if (candidate is null)
                    {
                        if (!refreshedDuringLoop)
                        {
                            refreshedDuringLoop = true;
                            AppendLog("目前清單中的節點皆無法連線，重新抓取最新日本節點...");
                            UpdateState(s => s with
                            {
                                Phase = ConnectionPhase.Fetching,
                                StatusTitle = "重新抓取日本節點...",
                                StatusDetail = "尋找新的可用節點（自動排除 219 開頭）"
                            });

                            try
                            {
                                var fresh = await _vpnGateService.FetchJapanServersAsync(allowCacheFallback: false, cts.Token);
                                _failedTimestamps.Clear();
                                SyncFailedIps();
                                UpdateState(s => s with
                                {
                                    Servers = fresh.Servers,
                                    TotalJapanCount = fresh.TotalJapanCount,
                                    Excluded219Count = fresh.Excluded219Count,
                                    LastFetchedAtMillis = fresh.FetchedAtMillis
                                });
                                continue;
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"重新抓取失敗：{ex.Message}");
                            }
                        }

                        _userWantsConnected = false;
                        UpdateState(s => s with
                        {
                            Phase = ConnectionPhase.Disconnected,
                            StatusTitle = "暫無可連通的日本節點",
                            StatusDetail = "已嘗試全部節點，請稍後再試",
                            ActiveServer = null,
                            AttemptProgress = string.Empty
                        });
                        return;
                    }

                    attemptIndex++;
                    bool isSwitch = attemptIndex > 1;
                    string progress = $"節點 {attemptIndex} / {allServers.Count}";

                    UpdateState(s => s with
                    {
                        SelectedServer = candidate,
                        ActiveServer = candidate,
                        AttemptProgress = progress
                    });

                    if (candidate.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateState(s => s with
                        {
                            Phase = isSwitch ? ConnectionPhase.Switching : ConnectionPhase.Probing,
                            StatusTitle = isSwitch ? "自動切換節點中..." : "檢測節點連通性...",
                            StatusDetail = $"{candidate.Ip}:{candidate.Port} ({candidate.FormattedPing()})"
                        });

                        bool reachable = await _vpnGateService.ProbeTcpReachableAsync(
                            candidate.RemoteHost, candidate.Port, TcpProbeTimeoutMs, cts.Token);
                        if (cts.IsCancellationRequested || sessionId != Interlocked.Read(ref _activeSessionId)) return;

                        if (!reachable)
                        {
                            AppendLog($"節點 {candidate.Ip}:{candidate.Port} TCP 無法連通，自動切換下一個");
                            MarkFailed(candidate.Ip);
                            if (!CurrentState.AutoFailoverEnabled)
                            {
                                _userWantsConnected = false;
                                UpdateState(s => s with
                                {
                                    Phase = ConnectionPhase.Disconnected,
                                    StatusTitle = "連線失敗",
                                    StatusDetail = $"節點 {candidate.Ip} 無法連通",
                                    ActiveServer = null,
                                    AttemptProgress = string.Empty
                                });
                                return;
                            }
                            continue;
                        }
                    }

                    UpdateState(s => s with
                    {
                        Phase = isSwitch ? ConnectionPhase.Switching : ConnectionPhase.Connecting,
                        StatusTitle = isSwitch ? "自動重連日本節點..." : "正在連線日本 VPN...",
                        StatusDetail = $"{candidate.Ip} • {candidate.Protocol.ToUpperInvariant()}:{candidate.Port}"
                    });

                    AppendLog($"嘗試連線日本節點：{candidate.Ip} ({candidate.Protocol.ToUpperInvariant()}:{candidate.Port}, {candidate.FormattedSpeed()})");

                    var outcome = await AttemptOpenVpnConnectAsync(openVpnPath, candidate, sessionId, cts.Token);
                    if (cts.IsCancellationRequested || sessionId != Interlocked.Read(ref _activeSessionId)) return;

                    if (outcome.IsCancelled) return;

                    if (outcome.IsConnected)
                    {
                        AppendLog($"成功連線日本節點：{candidate.Ip}！");
                        _connectedSinceMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        StartTimer(sessionId);
                        UpdateState(s => s with
                        {
                            Phase = ConnectionPhase.Connected,
                            StatusTitle = "已連線日本 VPN",
                            StatusDetail = $"{candidate.Ip} • {candidate.FormattedPing()} • {candidate.FormattedSpeed()}",
                            ActiveServer = candidate,
                            SelectedServer = candidate,
                            AttemptProgress = string.Empty
                        });

                        if (CurrentState.HealthCheckEnabled)
                        {
                            StartHealthCheck(sessionId);
                        }
                        return;
                    }

                    AppendLog($"節點 {candidate.Ip} 失敗（{outcome.Reason}）");
                    MarkFailed(candidate.Ip);
                    await _runner.StopAsync();
                    await Task.Delay(450, cts.Token);

                    if (!CurrentState.AutoFailoverEnabled)
                    {
                        _userWantsConnected = false;
                        UpdateState(s => s with
                        {
                            Phase = ConnectionPhase.Disconnected,
                            StatusTitle = "連線失敗",
                            StatusDetail = $"{candidate.Ip}：{outcome.Reason}",
                            ActiveServer = null,
                            AttemptProgress = string.Empty
                        });
                        return;
                    }

                    AppendLog("自動切換至下一個可用日本節點...");
                }
            }
            catch (OperationCanceledException)
            {
                // Preempted cleanly
            }
        });
    }

    private async Task<AttemptOutcome> AttemptOpenVpnConnectAsync(
        string openVpnExePath,
        VpnServer server,
        long sessionId,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AttemptOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _currentAttemptSessionId = sessionId;
        _currentAttemptTcs = tcs;

        try
        {
            await _runner.StartAsync(openVpnExePath, server.OvpnConfig, ct);
        }
        catch (Exception ex)
        {
            _currentAttemptTcs = null;
            return AttemptOutcome.Failed(ex.Message);
        }

        using var reg = ct.Register(() => tcs.TrySetResult(AttemptOutcome.Cancelled()));
        var timeoutTask = Task.Delay(ConnectTimeoutMs, ct);
        var completed = await Task.WhenAny(tcs.Task, timeoutTask);

        if (_currentAttemptSessionId == sessionId)
        {
            _currentAttemptTcs = null;
        }

        if (completed == tcs.Task)
        {
            return await tcs.Task;
        }

        return ct.IsCancellationRequested
            ? AttemptOutcome.Cancelled()
            : AttemptOutcome.Failed($"連線逾時 ({ConnectTimeoutMs / 1000}秒)");
    }

    private void OnRunnerConnected()
    {
        var tcs = _currentAttemptTcs;
        if (tcs is not null && !tcs.Task.IsCompleted)
        {
            tcs.TrySetResult(AttemptOutcome.Connected());
            return;
        }

        _graceCts?.Cancel();
        _graceCts = null;
    }

    private void OnRunnerReconnecting(string reason)
    {
        var tcs = _currentAttemptTcs;
        if (tcs is not null && !tcs.Task.IsCompleted) return;

        long sessionId = Interlocked.Read(ref _activeSessionId);
        if (CurrentState.Phase == ConnectionPhase.Connected && _userWantsConnected)
        {
            if (_graceCts is { IsCancellationRequested: false }) return;
            var cts = new CancellationTokenSource();
            _graceCts = cts;
            AppendLog($"偵測到日本節點不穩 ({reason})，等待 {DropGraceMs / 1000} 秒確認...");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DropGraceMs, cts.Token);
                    if (sessionId == Interlocked.Read(ref _activeSessionId) &&
                        CurrentState.Phase == ConnectionPhase.Connected &&
                        _userWantsConnected)
                    {
                        HandleUnexpectedDrop("節點逾時無法恢復", sessionId);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    private void OnRunnerFailed(string reason)
    {
        var tcs = _currentAttemptTcs;
        if (tcs is not null && !tcs.Task.IsCompleted)
        {
            tcs.TrySetResult(AttemptOutcome.Failed(reason));
            return;
        }

        long sessionId = Interlocked.Read(ref _activeSessionId);
        if (CurrentState.Phase == ConnectionPhase.Connected && _userWantsConnected)
        {
            HandleUnexpectedDrop(reason, sessionId);
        }
    }

    private void OnRunnerByteCountUpdated(long downBps, long upBps, long _, long __)
    {
        if (CurrentState.Phase == ConnectionPhase.Connected)
        {
            UpdateState(s => s with { DownloadBps = downBps, UploadBps = upBps });
        }
    }

    private void StartHealthCheck(long sessionId)
    {
        _healthCts?.Cancel();
        var cts = new CancellationTokenSource();
        _healthCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(HealthWarmupMs, cts.Token);
                int consecutiveFailures = 0;

                while (!cts.IsCancellationRequested &&
                       _userWantsConnected &&
                       sessionId == Interlocked.Read(ref _activeSessionId) &&
                       CurrentState.Phase == ConnectionPhase.Connected)
                {
                    if (!CurrentState.HealthCheckEnabled || !NetworkInterface.GetIsNetworkAvailable())
                    {
                        consecutiveFailures = 0;
                        await Task.Delay(HealthIntervalMs, cts.Token);
                        continue;
                    }

                    bool ok = await CheckInternetReachableAsync(cts.Token);
                    if (cts.IsCancellationRequested ||
                        !_userWantsConnected ||
                        sessionId != Interlocked.Read(ref _activeSessionId) ||
                        CurrentState.Phase != ConnectionPhase.Connected)
                    {
                        return;
                    }

                    if (ok)
                    {
                        consecutiveFailures = 0;
                        await Task.Delay(HealthIntervalMs, cts.Token);
                    }
                    else
                    {
                        if (!NetworkInterface.GetIsNetworkAvailable())
                        {
                            consecutiveFailures = 0;
                            await Task.Delay(HealthIntervalMs, cts.Token);
                            continue;
                        }

                        consecutiveFailures++;
                        if (consecutiveFailures == 1)
                        {
                            AppendLog($"連通健康檢查第 1 次無回應，{HealthRecheckDelayMs / 1000} 秒後覆檢...");
                            await Task.Delay(HealthRecheckDelayMs, cts.Token);
                        }
                        else
                        {
                            AppendLog($"偵測到節點假連線／斷流（連續 {consecutiveFailures} 次無法連網），自動切換下一個日本節點！");
                            HandleUnexpectedDrop("偵測到假連線／斷流", sessionId);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task<bool> CheckInternetReachableAsync(CancellationToken ct)
    {
        foreach (var url in HealthProbeUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.ConnectionClose = true;
                using var resp = await _healthClient.SendAsync(req, ct);
                if ((int)resp.StatusCode is >= 200 and <= 399)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try next probe URL
            }
        }
        return false;
    }

    private void HandleUnexpectedDrop(string reason, long expectedSessionId)
    {
        if (expectedSessionId != Interlocked.Read(ref _activeSessionId) ||
            CurrentState.Phase != ConnectionPhase.Connected ||
            !_userWantsConnected)
        {
            return;
        }

        CancelInFlightOperations();

        var dropped = CurrentState.ActiveServer;
        if (dropped is not null)
        {
            MarkFailed(dropped.Ip);
        }

        if (CurrentState.AutoFailoverEnabled && _userWantsConnected)
        {
            AppendLog($"節點 {dropped?.Ip ?? ""} 異常（{reason}），自動重新連線其他日本節點！");
            StartConnectionPipeline(null);
        }
        else
        {
            _userWantsConnected = false;
            Interlocked.Increment(ref _activeSessionId);
            UpdateState(s => s with
            {
                Phase = ConnectionPhase.Disconnected,
                StatusTitle = "連線已中斷",
                StatusDetail = reason,
                ActiveServer = null,
                DownloadBps = 0,
                UploadBps = 0,
                ConnectedSeconds = 0
            });
        }
    }

    private void StartTimer(long sessionId)
    {
        _timerCts?.Cancel();
        var cts = new CancellationTokenSource();
        _timerCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested &&
                       sessionId == Interlocked.Read(ref _activeSessionId) &&
                       CurrentState.Phase == ConnectionPhase.Connected)
                {
                    long elapsed = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _connectedSinceMillis) / 1000);
                    UpdateState(s => s with { ConnectedSeconds = elapsed });
                    await Task.Delay(1000, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void MarkFailed(string ip)
    {
        _failedTimestamps[ip] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SyncFailedIps();
    }

    private bool IsRecentlyFailed(string ip)
    {
        if (!_failedTimestamps.TryGetValue(ip, out long ts)) return false;
        return (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts) < FailedCooldownMs;
    }

    private void PruneExpiredFailures()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool changed = false;
        foreach (var kvp in _failedTimestamps)
        {
            if (now - kvp.Value >= FailedCooldownMs)
            {
                if (_failedTimestamps.TryRemove(kvp.Key, out _)) changed = true;
            }
        }
        if (changed) SyncFailedIps();
    }

    private void SyncFailedIps()
    {
        UpdateState(s => s with { FailedIps = new HashSet<string>(_failedTimestamps.Keys, StringComparer.OrdinalIgnoreCase) });
    }

    private void AppendLog(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        UpdateState(s =>
        {
            var next = s.Logs.Append(entry).TakeLast(MaxLogLines).ToList();
            return s with { Logs = next };
        });
    }

    private void UpdateState(Func<VpnUiState, VpnUiState> updater)
    {
        VpnUiState snapshot;
        lock (_stateLock)
        {
            _state = updater(_state);
            snapshot = _state;
        }
        StateChanged?.Invoke(snapshot);
    }
}
