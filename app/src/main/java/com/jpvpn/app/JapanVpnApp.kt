package com.jpvpn.app

import android.app.Application
import com.jpvpn.app.vpn.VpnConnectionManager
import de.blinkt.openvpn.core.OpenVPNService
import de.blinkt.openvpn.core.VpnStatus

class JapanVpnApp : Application() {

    lateinit var vpnManager: VpnConnectionManager
        private set

    override fun onCreate() {
        super.onCreate()
        VpnStatus.initLogCache(cacheDir)
        OpenVPNService.setNotificationActivityClass(MainActivity::class.java)
        vpnManager = VpnConnectionManager(applicationContext)
    }
}
