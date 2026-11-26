package com.jpvpn.app.vpn

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.widget.Toast
import com.jpvpn.app.JapanVpnApp

class VpnNotificationActionReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent?) {
        val app = context.applicationContext as? JapanVpnApp ?: return
        when (intent?.action) {
            ACTION_SWITCH_NEXT -> {
                Toast.makeText(context, "正在切換下一個日本節點...", Toast.LENGTH_SHORT).show()
                app.vpnManager.switchToNextServer()
            }
            ACTION_DISCONNECT -> {
                Toast.makeText(context, "已中斷日本 VPN 連線", Toast.LENGTH_SHORT).show()
                app.vpnManager.disconnect()
            }
            ACTION_SYSTEM_REVOKED -> {
                app.vpnManager.onSystemVpnRevoked()
            }
        }
    }

    companion object {
        const val ACTION_SWITCH_NEXT = "com.jpvpn.app.ACTION_SWITCH_NEXT"
        const val ACTION_DISCONNECT = "com.jpvpn.app.ACTION_DISCONNECT"
        const val ACTION_SYSTEM_REVOKED = "com.jpvpn.app.ACTION_SYSTEM_REVOKED"
    }
}
