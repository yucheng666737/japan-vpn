package com.jpvpn.app

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import com.jpvpn.app.model.VpnServer
import com.jpvpn.app.ui.BatteryOptimizationDialog
import com.jpvpn.app.ui.ConnectionLogDialog
import com.jpvpn.app.ui.MainScreen
import com.jpvpn.app.ui.ServerListScreen
import com.jpvpn.app.ui.theme.JapanVpnTheme
import com.jpvpn.app.vpn.ConnectionPhase
import com.jpvpn.app.vpn.VpnConnectionManager

class MainActivity : ComponentActivity() {

    private lateinit var vpnManager: VpnConnectionManager
    private var pendingConnectAction: (() -> Unit)? = null
    private var isBatteryUnrestrictedState by mutableStateOf(true)

    private val vpnPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        if (result.resultCode == Activity.RESULT_OK) {
            pendingConnectAction?.invoke()
        } else {
            Toast.makeText(this, "需要允許 VPN 權限才能連線日本伺服器", Toast.LENGTH_SHORT).show()
        }
        pendingConnectAction = null
    }

    private val notificationPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { _ ->
        // VPN works regardless of notification permission, though notifications are needed for quick-switch bar
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        vpnManager = (application as JapanVpnApp).vpnManager
        requestNotificationPermissionIfNeeded()

        isBatteryUnrestrictedState = isIgnoringBatteryOptimizations()
        val onboardingPrefs = getSharedPreferences(PREFS_ONBOARDING, Context.MODE_PRIVATE)
        val shouldShowFirstBatteryPrompt =
            !onboardingPrefs.getBoolean(KEY_BATTERY_PROMPT_SHOWN, false) && !isBatteryUnrestrictedState

        setContent {
            val systemDark = isSystemInDarkTheme()
            var isDarkTheme by rememberSaveable { mutableStateOf(systemDark) }
            var showServerList by rememberSaveable { mutableStateOf(false) }
            var showLogs by remember { mutableStateOf(false) }
            var showBatteryDialog by rememberSaveable { mutableStateOf(shouldShowFirstBatteryPrompt) }

            val uiState by vpnManager.uiState.collectAsState()

            JapanVpnTheme(darkTheme = isDarkTheme) {
                if (showServerList) {
                    ServerListScreen(
                        state = uiState,
                        onBack = { showServerList = false },
                        onRefresh = { vpnManager.refreshServers(silent = false) },
                        onSelectAndConnect = { server: VpnServer ->
                            showServerList = false
                            ensureVpnPermissionThen {
                                vpnManager.connectToServer(server)
                            }
                        }
                    )
                } else {
                    MainScreen(
                        state = uiState,
                        isDarkTheme = isDarkTheme,
                        isBatteryUnrestricted = isBatteryUnrestrictedState,
                        onToggleTheme = { isDarkTheme = !isDarkTheme },
                        onPowerClick = {
                            if (uiState.phase == ConnectionPhase.DISCONNECTED) {
                                ensureVpnPermissionThen {
                                    vpnManager.connectAuto()
                                }
                            } else {
                                vpnManager.disconnect()
                            }
                        },
                        onOpenServerList = { showServerList = true },
                        onRefreshServers = { vpnManager.refreshServers(silent = false) },
                        onToggleAutoFailover = { enabled -> vpnManager.setAutoFailover(enabled) },
                        onToggleAutoRefresh = { enabled -> vpnManager.setAutoRefresh(enabled) },
                        onToggleHealthCheck = { enabled -> vpnManager.setHealthCheckEnabled(enabled) },
                        onRequestBatteryOptimization = { showBatteryDialog = true },
                        onSwitchNextServer = {
                            ensureVpnPermissionThen {
                                vpnManager.switchToNextServer()
                            }
                        },
                        onOpenLogs = { showLogs = true }
                    )
                }

                if (showLogs) {
                    ConnectionLogDialog(
                        logs = uiState.logs,
                        onDismiss = { showLogs = false }
                    )
                }

                if (showBatteryDialog) {
                    BatteryOptimizationDialog(
                        onRequestIgnoreBattery = {
                            markBatteryPromptShown()
                            showBatteryDialog = false
                            openIgnoreBatteryOptimizationRequest()
                        },
                        onOpenAppSettings = {
                            markBatteryPromptShown()
                            showBatteryDialog = false
                            openAppDetailsSettings()
                        },
                        onDismiss = {
                            markBatteryPromptShown()
                            showBatteryDialog = false
                        }
                    )
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        isBatteryUnrestrictedState = isIgnoringBatteryOptimizations()
        if (::vpnManager.isInitialized) {
            vpnManager.refreshIfStale()
        }
    }

    private fun markBatteryPromptShown() {
        getSharedPreferences(PREFS_ONBOARDING, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(KEY_BATTERY_PROMPT_SHOWN, true)
            .apply()
    }

    private fun isIgnoringBatteryOptimizations(): Boolean {
        return try {
            val pm = getSystemService(Context.POWER_SERVICE) as? PowerManager ?: return true
            pm.isIgnoringBatteryOptimizations(packageName)
        } catch (_: Exception) {
            true
        }
    }

    @SuppressLint("BatteryLife")
    private fun openIgnoreBatteryOptimizationRequest() {
        try {
            val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                data = Uri.parse("package:$packageName")
            }
            startActivity(intent)
        } catch (_: Exception) {
            try {
                startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
            } catch (_: Exception) {
                openAppDetailsSettings()
            }
        }
    }

    private fun openAppDetailsSettings() {
        try {
            val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:$packageName")
            }
            startActivity(intent)
        } catch (_: Exception) {
            Toast.makeText(this, "無法開啟系統設定頁，請手動至設定 → 應用程式 → Japan VPN 調整", Toast.LENGTH_LONG).show()
        }
    }

    private fun ensureVpnPermissionThen(action: () -> Unit) {
        val prepareIntent: Intent? = VpnService.prepare(this)
        if (prepareIntent != null) {
            pendingConnectAction = action
            vpnPermissionLauncher.launch(prepareIntent)
        } else {
            action()
        }
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(
                    this,
                    Manifest.permission.POST_NOTIFICATIONS
                ) != PackageManager.PERMISSION_GRANTED
            ) {
                notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }

    companion object {
        private const val PREFS_ONBOARDING = "app_onboarding"
        private const val KEY_BATTERY_PROMPT_SHOWN = "battery_prompt_shown"
    }
}
