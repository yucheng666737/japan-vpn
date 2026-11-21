package com.jpvpn.app.model

data class VpnServer(
    val hostName: String,
    val ip: String,
    val remoteHost: String,
    val port: Int,
    val protocol: String,
    val score: Long,
    val ping: Int,
    val speedBps: Long,
    val sessions: Int,
    val uptimeMs: Long,
    val operator: String,
    val ovpnConfig: String
) {
    val speedMbps: Double
        get() = speedBps / 1_000_000.0

    val signalBars: Int
        get() {
            var points = 0
            points += when {
                ping in 1..25 -> 3
                ping in 26..60 -> 2
                ping in 61..120 -> 1
                else -> 0
            }
            points += when {
                speedMbps >= 30.0 -> 2
                speedMbps >= 10.0 -> 1
                else -> 0
            }
            return points.coerceIn(1, 5)
        }

    fun formattedSpeed(): String {
        return when {
            speedMbps >= 100.0 -> String.format("%.0f Mbps", speedMbps)
            speedMbps >= 1.0 -> String.format("%.1f Mbps", speedMbps)
            speedBps >= 1_000L -> String.format("%.0f Kbps", speedBps / 1_000.0)
            else -> "N/A"
        }
    }

    fun formattedPing(): String {
        return if (ping > 0) "${ping}ms" else "--"
    }
}

data class ServerFetchResult(
    val servers: List<VpnServer>,
    val totalJapanCount: Int,
    val excluded219Count: Int,
    val fetchedAtMillis: Long,
    val fromCache: Boolean = false
)
