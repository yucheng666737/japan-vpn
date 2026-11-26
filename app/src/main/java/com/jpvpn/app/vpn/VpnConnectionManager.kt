package com.jpvpn.app.vpn

import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import com.jpvpn.app.data.VpnGateRepository
import com.jpvpn.app.model.VpnServer
import de.blinkt.openvpn.VpnProfile
import de.blinkt.openvpn.core.ConfigParser
import de.blinkt.openvpn.core.ConnectionStatus
import de.blinkt.openvpn.core.OpenVPNService
import de.blinkt.openvpn.core.ProfileManager
import de.blinkt.openvpn.core.VPNLaunchHelper
import de.blinkt.openvpn.core.VpnStatus
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.StringReader
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

enum class ConnectionPhase {
    DISCONNECTED,
    FETCHING,
    PROBING,
    CONNECTING,
    SWITCHING,
    CONNECTED
}

data class VpnUiState(
    val phase: ConnectionPhase = ConnectionPhase.DISCONNECTED,
    val statusTitle: String = "未連線",
    val statusDetail: String = "點擊電源鍵自動連線最佳日本節點",
    val servers: List<VpnServer> = emptyList(),
    val selectedServer: VpnServer? = null,
    val activeServer: VpnServer? = null,
    val totalJapanCount: Int = 0,
    val excluded219Count: Int = 0,
    val failedIps: Set<String> = emptySet(),
    val autoFailoverEnabled: Boolean = true,
    val autoRefreshEnabled: Boolean = true,
    val healthCheckEnabled: Boolean = true,
    val downloadBps: Long = 0L,
    val uploadBps: Long = 0L,
    val totalDownloadBytes: Long = 0L,
    val totalUploadBytes: Long = 0L,
    val connectedSeconds: Long = 0L,
    val isRefreshing: Boolean = false,
    val lastFetchedAtMillis: Long = 0L,
    val attemptProgress: String = "",
    val logs: List<String> = emptyList()
)

class VpnConnectionManager(private val appContext: Context) :
    VpnStatus.StateListener,
    VpnStatus.ByteCountListener {

    private val repository = VpnGateRepository(appContext)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private val healthHttpClient = OkHttpClient.Builder()
        .connectTimeout(HEALTH_PROBE_TIMEOUT_MS, TimeUnit.MILLISECONDS)
        .readTimeout(HEALTH_PROBE_TIMEOUT_MS, TimeUnit.MILLISECONDS)
        .callTimeout(HEALTH_PROBE_TIMEOUT_MS + 1000L, TimeUnit.MILLISECONDS)
        .followRedirects(false)
        .retryOnConnectionFailure(false)
        .build()

    private val _uiState = MutableStateFlow(VpnUiState())
    val uiState: StateFlow<VpnUiState> = _uiState.asStateFlow()

    private val failedTimestamps = ConcurrentHashMap<String, Long>()

    @Volatile
    private var userWantsConnected = false

    @Volatile
    private var activeSessionId = 0L

    @Volatile
    private var currentAttemptId = 0L

    @Volatile
    private var attemptSawStartSignal = false

    @Volatile
    private var currentAttemptResult: CompletableDeferred<AttemptOutcome>? = null

    private var connectJob: Job? = null
    private var timerJob: Job? = null
    private var reconnectGraceJob: Job? = null
    private var healthCheckJob: Job? = null
    private var autoRefreshJob: Job? = null
    private var connectedSinceMillis = 0L

    private sealed class AttemptOutcome {
        data object Connected : AttemptOutcome()
        data class Failed(val reason: String) : AttemptOutcome()
        data object Cancelled : AttemptOutcome()
    }

    init {
        VpnStatus.addStateListener(this)
        VpnStatus.addByteCountListener(this)

        // Load cached Japan servers immediately so UI is populated right away
        repository.loadCachedResult()?.let { cached ->
            _uiState.update {
                it.copy(
                    servers = cached.servers,
                    selectedServer = cached.servers.firstOrNull(),
                    totalJapanCount = cached.totalJapanCount,
                    excluded219Count = cached.excluded219Count,
                    lastFetchedAtMillis = cached.fetchedAtMillis
                )
            }
        }
        refreshServers(silent = true)
        startPeriodicAutoRefresh()
    }

    fun setAutoFailover(enabled: Boolean) {
        _uiState.update { it.copy(autoFailoverEnabled = enabled) }
        appendLog(if (enabled) "已開啟：連線失敗/斷線時自動重連其他日本節點" else "已關閉：自動重連")
    }

    fun setHealthCheckEnabled(enabled: Boolean) {
        _uiState.update { it.copy(healthCheckEnabled = enabled) }
        if (enabled) {
            appendLog("已開啟：防假連線／斷流主動健康檢查")
            if (_uiState.value.phase == ConnectionPhase.CONNECTED && userWantsConnected) {
                startHealthCheck(activeSessionId)
            }
        } else {
            appendLog("已關閉：防假連線健康檢查")
            healthCheckJob?.cancel()
            healthCheckJob = null
        }
    }

    fun setAutoRefresh(enabled: Boolean) {
        _uiState.update { it.copy(autoRefreshEnabled = enabled) }
        if (enabled) {
            appendLog("已開啟：每 3 分鐘背景自動抓取最新日本節點")
            startPeriodicAutoRefresh()
        } else {
            appendLog("已關閉：背景定時自動抓取")
            autoRefreshJob?.cancel()
            autoRefreshJob = null
        }
    }

    fun refreshIfStale() {
        val state = _uiState.value
        if (!state.autoRefreshEnabled || state.isRefreshing || state.phase == ConnectionPhase.FETCHING) {
            return
        }
        val age = System.currentTimeMillis() - state.lastFetchedAtMillis
        if (state.servers.isEmpty() || age >= RESUME_REFRESH_STALE_MS) {
            refreshServers(silent = true)
        }
    }

    private fun startPeriodicAutoRefresh() {
        autoRefreshJob?.cancel()
        autoRefreshJob = scope.launch {
            while (isActive) {
                delay(AUTO_REFRESH_INTERVAL_MS)
                val state = _uiState.value
                if (state.autoRefreshEnabled && !state.isRefreshing && state.phase != ConnectionPhase.FETCHING) {
                    refreshServers(silent = true)
                }
            }
        }
    }

    fun selectServer(server: VpnServer) {
        if (VpnGateRepository.isBlocked219Ip(server.ip)) {
            appendLog("已阻擋 219 開頭節點：${server.ip}")
            return
        }
        _uiState.update { it.copy(selectedServer = server) }
    }

    fun refreshServers(silent: Boolean = false) {
        if (_uiState.value.isRefreshing) return
        scope.launch {
            _uiState.update { it.copy(isRefreshing = true) }
            if (!silent) {
                appendLog("正在從 VPN Gate 抓取最新日本節點清單...")
            }
            try {
                val fetched = repository.fetchJapanServers(allowCacheFallback = true)
                pruneExpiredFailures()
                val currentSelected = _uiState.value.selectedServer
                val newSelected = fetched.servers.firstOrNull { it.ip == currentSelected?.ip }
                    ?: fetched.servers.firstOrNull { !isRecentlyFailed(it.ip) }
                    ?: fetched.servers.firstOrNull()

                _uiState.update {
                    it.copy(
                        servers = fetched.servers,
                        selectedServer = newSelected,
                        totalJapanCount = fetched.totalJapanCount,
                        excluded219Count = fetched.excluded219Count,
                        lastFetchedAtMillis = fetched.fetchedAtMillis,
                        isRefreshing = false
                    )
                }
                appendLog(
                    "已自動更新日本節點：共 ${fetched.totalJapanCount} 個，已排除 ${fetched.excluded219Count} 個 219 開頭，可用 ${fetched.servers.size} 個"
                )
            } catch (e: Exception) {
                _uiState.update { it.copy(isRefreshing = false) }
                if (!silent) {
                    appendLog("抓取節點清單失敗：${e.message ?: "網路錯誤"}")
                }
            }
        }
    }

    fun connectAuto() {
        startConnectionPipeline(preferredServer = _uiState.value.selectedServer)
    }

    fun connectToServer(server: VpnServer) {
        if (VpnGateRepository.isBlocked219Ip(server.ip)) {
            appendLog("已略過 219 開頭節點：${server.ip}")
            return
        }
        failedTimestamps.remove(server.ip)
        syncFailedIps()
        _uiState.update { it.copy(selectedServer = server) }
        startConnectionPipeline(preferredServer = server)
    }

    fun switchToNextServer() {
        val currentIp = _uiState.value.activeServer?.ip ?: _uiState.value.selectedServer?.ip
        if (currentIp != null) {
            markFailed(currentIp)
            appendLog("手動切換節點，暫時略過 $currentIp")
        }
        startConnectionPipeline(preferredServer = null)
    }

    fun disconnect() {
        userWantsConnected = false
        activeSessionId++
        currentAttemptId++
        currentAttemptResult?.complete(AttemptOutcome.Cancelled)
        currentAttemptResult = null
        connectJob?.cancel()
        connectJob = null
        reconnectGraceJob?.cancel()
        reconnectGraceJob = null
        healthCheckJob?.cancel()
        healthCheckJob = null
        stopTimer()

        stopOpenVpnService()
        appendLog("使用者已手動斷開連線")

        _uiState.update {
            it.copy(
                phase = ConnectionPhase.DISCONNECTED,
                statusTitle = "未連線",
                statusDetail = "已安全斷開連線",
                activeServer = null,
                downloadBps = 0L,
                uploadBps = 0L,
                connectedSeconds = 0L,
                attemptProgress = ""
            )
        }
    }

    fun onSystemVpnRevoked() {
        userWantsConnected = false
        activeSessionId++
        currentAttemptId++
        currentAttemptResult?.complete(AttemptOutcome.Cancelled)
        currentAttemptResult = null
        connectJob?.cancel()
        connectJob = null
        reconnectGraceJob?.cancel()
        reconnectGraceJob = null
        healthCheckJob?.cancel()
        healthCheckJob = null
        stopTimer()

        appendLog("已由系統狀態欄中斷 VPN 連線（停止自動重連）")

        _uiState.update {
            it.copy(
                phase = ConnectionPhase.DISCONNECTED,
                statusTitle = "未連線",
                statusDetail = "已由系統狀態欄中斷連線",
                activeServer = null,
                downloadBps = 0L,
                uploadBps = 0L,
                connectedSeconds = 0L,
                attemptProgress = ""
            )
        }
    }

    private fun startConnectionPipeline(preferredServer: VpnServer?) {
        userWantsConnected = true
        val sessionId = ++activeSessionId
        currentAttemptId++
        currentAttemptResult?.complete(AttemptOutcome.Cancelled)
        currentAttemptResult = null
        connectJob?.cancel()
        reconnectGraceJob?.cancel()
        reconnectGraceJob = null
        healthCheckJob?.cancel()
        healthCheckJob = null
        stopTimer()

        connectJob = scope.launch {
            // Stop any existing tunnel cleanly first
            if (_uiState.value.phase == ConnectionPhase.CONNECTED ||
                _uiState.value.phase == ConnectionPhase.CONNECTING ||
                _uiState.value.phase == ConnectionPhase.SWITCHING
            ) {
                stopOpenVpnService()
                delay(450L)
            }

            // Ensure we have fresh Japan non-219 servers
            val cacheAge = System.currentTimeMillis() - _uiState.value.lastFetchedAtMillis
            if (_uiState.value.servers.isEmpty() || cacheAge > SERVER_LIST_STALE_MS) {
                _uiState.update {
                    it.copy(
                        phase = ConnectionPhase.FETCHING,
                        statusTitle = "正在抓取日本 VPN...",
                        statusDetail = "自動篩選日本節點並排除 219 開頭 IP",
                        attemptProgress = ""
                    )
                }
                try {
                    val fetched = repository.fetchJapanServers(allowCacheFallback = true)
                    _uiState.update {
                        it.copy(
                            servers = fetched.servers,
                            totalJapanCount = fetched.totalJapanCount,
                            excluded219Count = fetched.excluded219Count,
                            lastFetchedAtMillis = fetched.fetchedAtMillis
                        )
                    }
                    appendLog("取得 ${fetched.servers.size} 個日本節點（已排除 ${fetched.excluded219Count} 個 219 開頭）")
                } catch (e: Exception) {
                    if (_uiState.value.servers.isEmpty()) {
                        appendLog("無法取得日本 VPN 清單：${e.message}")
                        userWantsConnected = false
                        _uiState.update {
                            it.copy(
                                phase = ConnectionPhase.DISCONNECTED,
                                statusTitle = "抓取節點失敗",
                                statusDetail = "請檢查網路連線後重試",
                                attemptProgress = ""
                            )
                        }
                        return@launch
                    }
                }
            }

            var refreshedDuringLoop = false
            var attemptIndex = 0
            var nextPreferred = preferredServer

            while (isActive && userWantsConnected) {
                pruneExpiredFailures()
                val allServers = _uiState.value.servers.filter { !VpnGateRepository.isBlocked219Ip(it.ip) }
                if (allServers.isEmpty()) {
                    userWantsConnected = false
                    _uiState.update {
                        it.copy(
                            phase = ConnectionPhase.DISCONNECTED,
                            statusTitle = "無可用日本節點",
                            statusDetail = "請稍後重新整理節點清單"
                        )
                    }
                    return@launch
                }

                // Pick next candidate server
                val candidate = if (nextPreferred != null && !VpnGateRepository.isBlocked219Ip(nextPreferred.ip)) {
                    val picked = nextPreferred
                    nextPreferred = null
                    picked
                } else {
                    allServers.firstOrNull { !isRecentlyFailed(it.ip) }
                }

                if (candidate == null) {
                    // All cached servers failed in this round
                    if (!refreshedDuringLoop) {
                        refreshedDuringLoop = true
                        appendLog("目前清單中的節點皆無法連線，重新抓取最新日本節點...")
                        _uiState.update {
                            it.copy(
                                phase = ConnectionPhase.FETCHING,
                                statusTitle = "重新抓取日本節點...",
                                statusDetail = "尋找新的可用節點（自動排除 219 開頭）"
                            )
                        }
                        try {
                            val fresh = repository.fetchJapanServers(allowCacheFallback = false)
                            failedTimestamps.clear()
                            syncFailedIps()
                            _uiState.update {
                                it.copy(
                                    servers = fresh.servers,
                                    totalJapanCount = fresh.totalJapanCount,
                                    excluded219Count = fresh.excluded219Count,
                                    lastFetchedAtMillis = fresh.fetchedAtMillis
                                )
                            }
                            continue
                        } catch (e: Exception) {
                            appendLog("重新抓取失敗：${e.message}")
                        }
                    }

                    userWantsConnected = false
                    _uiState.update {
                        it.copy(
                            phase = ConnectionPhase.DISCONNECTED,
                            statusTitle = "暫無可連通的日本節點",
                            statusDetail = "已嘗試全部節點，請稍後再試",
                            activeServer = null,
                            attemptProgress = ""
                        )
                    }
                    return@launch
                }

                attemptIndex++
                val totalCandidates = allServers.size
                val isSwitch = attemptIndex > 1

                _uiState.update {
                    it.copy(
                        selectedServer = candidate,
                        activeServer = candidate,
                        attemptProgress = "節點 $attemptIndex / $totalCandidates"
                    )
                }

                // Fast TCP pre-check for TCP nodes so dead servers are skipped in <2.5s!
                if (candidate.protocol.equals("tcp", ignoreCase = true)) {
                    _uiState.update {
                        it.copy(
                            phase = if (isSwitch) ConnectionPhase.SWITCHING else ConnectionPhase.PROBING,
                            statusTitle = if (isSwitch) "自動切換節點中..." else "檢測節點連通性...",
                            statusDetail = "${candidate.ip}:${candidate.port} (${candidate.formattedPing()})"
                        )
                    }
                    val reachable = repository.probeTcpReachable(
                        candidate.remoteHost,
                        candidate.port,
                        TCP_PROBE_TIMEOUT_MS
                    )
                    if (!userWantsConnected || !isActive) return@launch

                    if (!reachable) {
                        appendLog("節點 ${candidate.ip}:${candidate.port} TCP 無法連通，自動切換下一個")
                        markFailed(candidate.ip)
                        if (!_uiState.value.autoFailoverEnabled) {
                            userWantsConnected = false
                            _uiState.update {
                                it.copy(
                                    phase = ConnectionPhase.DISCONNECTED,
                                    statusTitle = "連線失敗",
                                    statusDetail = "節點 ${candidate.ip} 無法連通",
                                    activeServer = null
                                )
                            }
                            return@launch
                        }
                        continue
                    }
                }

                // Launch OpenVPN tunnel for this candidate
                _uiState.update {
                    it.copy(
                        phase = if (isSwitch) ConnectionPhase.SWITCHING else ConnectionPhase.CONNECTING,
                        statusTitle = if (isSwitch) "自動重連日本節點..." else "正在連線日本 VPN...",
                        statusDetail = "${candidate.ip} • ${candidate.protocol.uppercase()}:${candidate.port}"
                    )
                }
                appendLog("嘗試連線日本節點：${candidate.ip} (${candidate.protocol.uppercase()}:${candidate.port}, ${candidate.formattedSpeed()})")

                val outcome = attemptOpenVpnConnect(candidate)
                if (!userWantsConnected || !isActive) return@launch

                when (outcome) {
                    is AttemptOutcome.Connected -> {
                        if (sessionId != activeSessionId) return@launch
                        appendLog("成功連線日本節點：${candidate.ip}！")
                        connectedSinceMillis = System.currentTimeMillis()
                        startTimer()
                        _uiState.update {
                            it.copy(
                                phase = ConnectionPhase.CONNECTED,
                                statusTitle = "已連線日本 VPN",
                                statusDetail = "${candidate.ip} • ${candidate.formattedPing()} • ${candidate.formattedSpeed()}",
                                activeServer = candidate,
                                selectedServer = candidate,
                                attemptProgress = ""
                            )
                        }
                        if (_uiState.value.healthCheckEnabled) {
                            startHealthCheck(sessionId)
                        }
                        return@launch
                    }

                    is AttemptOutcome.Cancelled -> {
                        return@launch
                    }

                    is AttemptOutcome.Failed -> {
                        appendLog("節點 ${candidate.ip} 失敗（${outcome.reason}）")
                        markFailed(candidate.ip)
                        stopOpenVpnService()
                        delay(500L)

                        if (!_uiState.value.autoFailoverEnabled) {
                            userWantsConnected = false
                            _uiState.update {
                                it.copy(
                                    phase = ConnectionPhase.DISCONNECTED,
                                    statusTitle = "連線失敗",
                                    statusDetail = "${candidate.ip}：${outcome.reason}",
                                    activeServer = null,
                                    attemptProgress = ""
                                )
                            }
                            return@launch
                        }

                        appendLog("自動切換至下一個可用日本節點...")
                    }
                }
            }
        }
    }

    private suspend fun attemptOpenVpnConnect(server: VpnServer): AttemptOutcome {
        val attemptId = ++currentAttemptId
        attemptSawStartSignal = false
        val deferred = CompletableDeferred<AttemptOutcome>()
        currentAttemptResult = deferred

        val profile = withContext(Dispatchers.IO) {
            buildVpnProfile(server)
        }
        if (profile == null) {
            return AttemptOutcome.Failed("設定檔解析失敗")
        }

        withContext(Dispatchers.IO) {
            ProfileManager.setTemporaryProfile(appContext, profile)
            VPNLaunchHelper.startOpenVpn(profile, appContext, "JapanVPN AutoConnect", true)
        }

        val result = withTimeoutOrNull(CONNECT_TIMEOUT_MS) {
            deferred.await()
        }

        if (currentAttemptId == attemptId) {
            currentAttemptResult = null
        }

        return result ?: AttemptOutcome.Failed("連線逾時 (${CONNECT_TIMEOUT_MS / 1000}秒)")
    }

    private fun buildVpnProfile(server: VpnServer): VpnProfile? {
        return try {
            val cp = ConfigParser()
            cp.parseConfig(StringReader(server.ovpnConfig))
            val profile = cp.convertProfile()
            profile.mName = "Japan - ${server.ip}"
            profile.mProfileCreator = appContext.packageName
            profile.mUsername = "vpn"
            profile.mPassword = "vpn"
            profile.mUsePull = true
            profile.mAllowLocalLAN = true

            // Limit internal OpenVPN retries so our auto-failover can switch servers quickly
            profile.mConnectRetryMax = "1"
            profile.mConnectRetry = "2"
            profile.mConnectRetryMaxTime = "8"

            // Ensure compatibility with SoftEther VPN Gate AES-128-CBC & older volunteer certs on OpenSSL 3
            val existingCiphers = profile.mDataCiphers.orEmpty()
            profile.mDataCiphers = when {
                existingCiphers.isBlank() -> "AES-128-CBC:AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305"
                !existingCiphers.contains("AES-128-CBC") -> "$existingCiphers:AES-128-CBC"
                else -> existingCiphers
            }
            if (profile.mCipher.isNullOrBlank()) {
                profile.mCipher = "AES-128-CBC"
            }
            profile.mTlSCertProfile = "insecure"
            profile
        } catch (e: Exception) {
            appendLog("解析設定檔錯誤 (${server.ip}): ${e.message}")
            null
        }
    }

    private fun stopOpenVpnService() {
        try {
            val stopIntent = Intent(appContext, OpenVPNService::class.java).apply {
                action = OpenVPNService.DISCONNECT_VPN
            }
            appContext.startService(stopIntent)
        } catch (_: Exception) {
            // Service might not be running yet
        }
    }

    override fun updateState(
        state: String?,
        logmessage: String?,
        localizedResId: Int,
        level: ConnectionStatus?,
        intent: Intent?
    ) {
        val safeState = state.orEmpty()
        val safeLevel = level ?: ConnectionStatus.UNKNOWN_LEVEL

        scope.launch {
            if (safeState == "USER_VPN_REVOKED") {
                onSystemVpnRevoked()
                return@launch
            }

            val pending = currentAttemptResult
            if (pending != null && !pending.isCompleted) {
                when (safeLevel) {
                    ConnectionStatus.LEVEL_START,
                    ConnectionStatus.LEVEL_CONNECTING_NO_SERVER_REPLY_YET,
                    ConnectionStatus.LEVEL_CONNECTING_SERVER_REPLIED -> {
                        attemptSawStartSignal = true
                        val detailPrefix = _uiState.value.activeServer?.ip ?: ""
                        val stage = when (safeState) {
                            "WAIT" -> "等待日本伺服器回應..."
                            "AUTH" -> "正在驗證加密通道..."
                            "GET_CONFIG" -> "取得日本 VPN 設定..."
                            "ASSIGN_IP" -> "分配日本 IP 位址..."
                            "ADD_ROUTES" -> "設定網路路由..."
                            else -> "建立加密連線中..."
                        }
                        _uiState.update {
                            it.copy(statusDetail = "$detailPrefix • $stage")
                        }
                    }

                    ConnectionStatus.LEVEL_CONNECTED -> {
                        pending.complete(AttemptOutcome.Connected)
                    }

                    ConnectionStatus.LEVEL_AUTH_FAILED -> {
                        pending.complete(AttemptOutcome.Failed("驗證失敗"))
                    }

                    ConnectionStatus.LEVEL_NONETWORK -> {
                        pending.complete(AttemptOutcome.Failed("目前無網路連線"))
                    }

                    ConnectionStatus.LEVEL_NOTCONNECTED -> {
                        if (attemptSawStartSignal || safeState == "EXITING" || safeState == "NOPROCESS") {
                            if (attemptSawStartSignal) {
                                pending.complete(AttemptOutcome.Failed("伺服器中斷 ($safeState)"))
                            }
                        }
                    }

                    else -> Unit
                }
                return@launch
            }

            // Monitor unexpected drops AFTER we are already connected
            if (_uiState.value.phase == ConnectionPhase.CONNECTED && userWantsConnected) {
                val sessionId = activeSessionId
                when {
                    safeLevel == ConnectionStatus.LEVEL_CONNECTED -> {
                        reconnectGraceJob?.cancel()
                        reconnectGraceJob = null
                    }

                    safeLevel == ConnectionStatus.LEVEL_NOTCONNECTED ||
                        safeLevel == ConnectionStatus.LEVEL_AUTH_FAILED -> {
                        handleUnexpectedDrop("連線中斷 ($safeState)", expectedSessionId = sessionId)
                    }

                    safeState == "RECONNECTING" ||
                        safeLevel == ConnectionStatus.LEVEL_CONNECTING_NO_SERVER_REPLY_YET -> {
                        if (reconnectGraceJob == null || reconnectGraceJob?.isActive != true) {
                            appendLog("偵測到日本節點不穩 ($safeState)，等待 ${DROP_GRACE_MS / 1000} 秒確認...")
                            reconnectGraceJob = scope.launch {
                                delay(DROP_GRACE_MS)
                                if (sessionId == activeSessionId &&
                                    _uiState.value.phase == ConnectionPhase.CONNECTED &&
                                    userWantsConnected
                                ) {
                                    handleUnexpectedDrop("節點逾時無法恢復", expectedSessionId = sessionId)
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private fun startHealthCheck(sessionId: Long) {
        healthCheckJob?.cancel()
        healthCheckJob = scope.launch {
            // Warmup delay so Android VPN routes & DNS settle cleanly after connecting
            delay(HEALTH_WARMUP_MS)
            var consecutiveFailures = 0

            while (isActive &&
                userWantsConnected &&
                sessionId == activeSessionId &&
                _uiState.value.phase == ConnectionPhase.CONNECTED
            ) {
                if (!_uiState.value.healthCheckEnabled) {
                    consecutiveFailures = 0
                    delay(HEALTH_INTERVAL_MS)
                    continue
                }

                // Do not blame the VPN node if the phone itself has no Wi-Fi/cellular network
                if (!hasActiveDeviceNetwork()) {
                    consecutiveFailures = 0
                    delay(HEALTH_INTERVAL_MS)
                    continue
                }

                val ok = checkInternetReachable()
                // Re-check session validity after network call (manual preemption guard!)
                if (!isActive ||
                    !userWantsConnected ||
                    sessionId != activeSessionId ||
                    _uiState.value.phase != ConnectionPhase.CONNECTED
                ) {
                    return@launch
                }

                if (ok) {
                    consecutiveFailures = 0
                    delay(HEALTH_INTERVAL_MS)
                } else {
                    if (!hasActiveDeviceNetwork()) {
                        consecutiveFailures = 0
                        delay(HEALTH_INTERVAL_MS)
                        continue
                    }
                    consecutiveFailures++
                    if (consecutiveFailures == 1) {
                        appendLog("連通健康檢查第 1 次無回應，${HEALTH_RECHECK_DELAY_MS / 1000} 秒後覆檢...")
                        delay(HEALTH_RECHECK_DELAY_MS)
                    } else {
                        appendLog("偵測到節點假連線／斷流（連續 $consecutiveFailures 次無法連網），自動切換下一個日本節點！")
                        handleUnexpectedDrop("偵測到假連線／斷流", expectedSessionId = sessionId)
                        return@launch
                    }
                }
            }
        }
    }

    private suspend fun checkInternetReachable(): Boolean = withContext(Dispatchers.IO) {
        for (url in HEALTH_PROBE_URLS) {
            try {
                val request = Request.Builder()
                    .url(url)
                    .header("Connection", "close")
                    .header("Cache-Control", "no-cache")
                    .get()
                    .build()
                healthHttpClient.newCall(request).execute().use { response ->
                    if (response.code in 200..399) {
                        return@withContext true
                    }
                }
            } catch (_: Exception) {
                // Try next probe endpoint
            }
        }
        false
    }

    private fun hasActiveDeviceNetwork(): Boolean {
        return try {
            val cm = appContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
                ?: return true
            val activeNetwork = cm.activeNetwork ?: return false
            val caps = cm.getNetworkCapabilities(activeNetwork) ?: return false
            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        } catch (_: Exception) {
            true
        }
    }

    private fun handleUnexpectedDrop(reason: String, expectedSessionId: Long) {
        // Guard against duplicate triggers between OpenVPN state listener and active health check
        if (expectedSessionId != activeSessionId ||
            _uiState.value.phase != ConnectionPhase.CONNECTED ||
            !userWantsConnected
        ) {
            return
        }

        reconnectGraceJob?.cancel()
        reconnectGraceJob = null
        healthCheckJob?.cancel()
        healthCheckJob = null
        stopTimer()

        val droppedServer = _uiState.value.activeServer
        if (droppedServer != null) {
            markFailed(droppedServer.ip)
        }

        if (_uiState.value.autoFailoverEnabled && userWantsConnected) {
            appendLog("節點 ${droppedServer?.ip ?: ""} 異常（$reason），自動重新連線其他日本節點！")
            startConnectionPipeline(preferredServer = null)
        } else {
            userWantsConnected = false
            activeSessionId++
            _uiState.update {
                it.copy(
                    phase = ConnectionPhase.DISCONNECTED,
                    statusTitle = "連線已中斷",
                    statusDetail = reason,
                    activeServer = null,
                    downloadBps = 0L,
                    uploadBps = 0L,
                    connectedSeconds = 0L
                )
            }
        }
    }

    override fun setConnectedVPN(uuid: String?) {
        // Not needed; tracked by VpnServer state
    }

    override fun updateByteCount(inBytes: Long, outBytes: Long, diffIn: Long, diffOut: Long) {
        // OpenVPN updates bytecount every 2 seconds by default
        val downPerSec = (diffIn / 2L).coerceAtLeast(0L)
        val upPerSec = (diffOut / 2L).coerceAtLeast(0L)
        scope.launch {
            if (_uiState.value.phase == ConnectionPhase.CONNECTED) {
                _uiState.update {
                    it.copy(
                        downloadBps = downPerSec,
                        uploadBps = upPerSec,
                        totalDownloadBytes = inBytes,
                        totalUploadBytes = outBytes
                    )
                }
            }
        }
    }

    private fun startTimer() {
        timerJob?.cancel()
        timerJob = scope.launch {
            while (isActive && _uiState.value.phase == ConnectionPhase.CONNECTED) {
                val elapsed = ((System.currentTimeMillis() - connectedSinceMillis) / 1000L).coerceAtLeast(0L)
                _uiState.update { it.copy(connectedSeconds = elapsed) }
                delay(1000L)
            }
        }
    }

    private fun stopTimer() {
        timerJob?.cancel()
        timerJob = null
    }

    private fun markFailed(ip: String) {
        failedTimestamps[ip] = System.currentTimeMillis()
        syncFailedIps()
    }

    private fun isRecentlyFailed(ip: String): Boolean {
        val ts = failedTimestamps[ip] ?: return false
        return (System.currentTimeMillis() - ts) < FAILED_COOLDOWN_MS
    }

    private fun pruneExpiredFailures() {
        val now = System.currentTimeMillis()
        var changed = false
        val iterator = failedTimestamps.entries.iterator()
        while (iterator.hasNext()) {
            if (now - iterator.next().value >= FAILED_COOLDOWN_MS) {
                iterator.remove()
                changed = true
            }
        }
        if (changed) syncFailedIps()
    }

    private fun syncFailedIps() {
        _uiState.update { it.copy(failedIps = failedTimestamps.keys.toSet()) }
    }

    private fun appendLog(message: String) {
        val time = SimpleDateFormat("HH:mm:ss", Locale.getDefault()).format(Date())
        val entry = "[$time] $message"
        _uiState.update { state ->
            val updated = (state.logs + entry).takeLast(MAX_LOG_LINES)
            state.copy(logs = updated)
        }
    }

    companion object {
        private const val TCP_PROBE_TIMEOUT_MS = 2500
        private const val CONNECT_TIMEOUT_MS = 15_000L
        private const val DROP_GRACE_MS = 5_000L
        private const val HEALTH_WARMUP_MS = 12_000L
        private const val HEALTH_INTERVAL_MS = 10_000L
        private const val HEALTH_RECHECK_DELAY_MS = 4_000L
        private const val HEALTH_PROBE_TIMEOUT_MS = 4_000L
        private const val FAILED_COOLDOWN_MS = 10 * 60 * 1000L
        private const val SERVER_LIST_STALE_MS = 3 * 60 * 1000L
        private const val AUTO_REFRESH_INTERVAL_MS = 3 * 60 * 1000L
        private const val RESUME_REFRESH_STALE_MS = 2 * 60 * 1000L
        private const val MAX_LOG_LINES = 120

        private val HEALTH_PROBE_URLS = listOf(
            "https://connectivitycheck.gstatic.com/generate_204",
            "https://cp.cloudflare.com/generate_204"
        )
    }
}
