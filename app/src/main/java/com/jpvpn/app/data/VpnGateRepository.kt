package com.jpvpn.app.data

import android.content.Context
import android.util.Base64
import com.jpvpn.app.model.ServerFetchResult
import com.jpvpn.app.model.VpnServer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.TimeUnit

class VpnGateRepository(context: Context) {

    private val prefs = context.getSharedPreferences("vpngate_cache", Context.MODE_PRIVATE)

    private val httpClient = OkHttpClient.Builder()
        .connectTimeout(12, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .followRedirects(true)
        .build()

    private val endpoints = listOf(
        "https://www.vpngate.net/api/iphone/"
    )

    suspend fun fetchJapanServers(allowCacheFallback: Boolean = true): ServerFetchResult =
        withContext(Dispatchers.IO) {
            var lastError: Exception? = null
            for (url in endpoints) {
                try {
                    val request = Request.Builder()
                        .url(url)
                        .header("User-Agent", "Mozilla/5.0 (Android; JapanVPN/1.0)")
                        .build()

                    httpClient.newCall(request).execute().use { response ->
                        if (!response.isSuccessful) {
                            throw IOException("HTTP ${response.code}")
                        }
                        val body = response.body?.string()
                            ?: throw IOException("Empty response body")
                        val parsed = parseJapanCsv(body, fromCache = false)
                        if (parsed.servers.isNotEmpty()) {
                            saveCache(body, parsed.fetchedAtMillis)
                            return@withContext parsed
                        }
                    }
                } catch (e: Exception) {
                    lastError = e
                }
            }

            if (allowCacheFallback) {
                val cached = loadCachedResult()
                if (cached != null && cached.servers.isNotEmpty()) {
                    return@withContext cached
                }
            }

            throw lastError ?: IOException("無法取得日本 VPN 節點清單")
        }

    fun loadCachedResult(): ServerFetchResult? {
        val raw = prefs.getString(KEY_CSV, null) ?: return null
        val timestamp = prefs.getLong(KEY_TIMESTAMP, 0L)
        return runCatching {
            parseJapanCsv(raw, fromCache = true, overrideTimestamp = timestamp)
        }.getOrNull()
    }

    private fun saveCache(csv: String, timestamp: Long) {
        prefs.edit()
            .putString(KEY_CSV, csv)
            .putLong(KEY_TIMESTAMP, timestamp)
            .apply()
    }

    private fun parseJapanCsv(
        csvContent: String,
        fromCache: Boolean,
        overrideTimestamp: Long? = null
    ): ServerFetchResult {
        val result = mutableListOf<VpnServer>()
        val seenIps = HashSet<String>()
        var totalJapanCount = 0
        var excluded219Count = 0

        val lines = csvContent.lineSequence()
        for (rawLine in lines) {
            val line = rawLine.trim()
            if (line.isEmpty() || line.startsWith("*") || line.startsWith("#")) continue

            val cols = line.split(",", limit = 15)
            if (cols.size < 15) continue

            val countryLong = cols[5].trim()
            val countryShort = cols[6].trim()
            val isJapan = countryShort.equals("JP", ignoreCase = true) ||
                countryLong.equals("Japan", ignoreCase = true)
            if (!isJapan) continue

            totalJapanCount++

            val hostName = cols[0].trim()
            val ip = cols[1].trim()

            // Strictly exclude any IP starting with 219.
            if (isBlocked219Ip(ip)) {
                excluded219Count++
                continue
            }

            val ovpnBase64 = cols[14].trim()
            if (ovpnBase64.isEmpty()) continue

            val ovpnConfig = try {
                String(Base64.decode(ovpnBase64, Base64.DEFAULT), Charsets.UTF_8)
            } catch (_: Exception) {
                continue
            }

            if (!ovpnConfig.contains("remote ") || !ovpnConfig.contains("<ca>")) continue

            val remoteInfo = extractRemoteInfo(ovpnConfig, fallbackIp = ip)
            // Double check the actual OpenVPN remote host as well!
            if (isBlocked219Ip(remoteInfo.host)) {
                excluded219Count++
                continue
            }

            if (!seenIps.add(ip)) continue

            val score = cols[2].trim().toLongOrNull() ?: 0L
            val ping = cols[3].trim().toIntOrNull() ?: 999
            val speedBps = cols[4].trim().toLongOrNull() ?: 0L
            val sessions = cols[7].trim().toIntOrNull() ?: 0
            val uptimeMs = cols[8].trim().toLongOrNull() ?: 0L
            val operator = cols[12].trim()

            result.add(
                VpnServer(
                    hostName = hostName,
                    ip = ip,
                    remoteHost = remoteInfo.host,
                    port = remoteInfo.port,
                    protocol = remoteInfo.protocol,
                    score = score,
                    ping = ping,
                    speedBps = speedBps,
                    sessions = sessions,
                    uptimeMs = uptimeMs,
                    operator = operator,
                    ovpnConfig = ovpnConfig
                )
            )
        }

        // Rank best Japan servers first: prioritize higher speed, lower ping, higher score
        val sorted = result.sortedWith(
            compareByDescending<VpnServer> { server ->
                val speedFactor = (server.speedBps / 1_000_000.0).coerceAtMost(200.0) * 12.0
                val pingPenalty = server.ping.coerceIn(1, 500) * 3.0
                val scoreFactor = (server.score / 100_000.0).coerceAtMost(50.0) * 6.0
                val tcpBonus = if (server.protocol.equals("tcp", ignoreCase = true)) 15.0 else 0.0
                speedFactor + scoreFactor + tcpBonus - pingPenalty
            }
        )

        return ServerFetchResult(
            servers = sorted,
            totalJapanCount = totalJapanCount,
            excluded219Count = excluded219Count,
            fetchedAtMillis = overrideTimestamp ?: System.currentTimeMillis(),
            fromCache = fromCache
        )
    }

    suspend fun probeTcpReachable(host: String, port: Int, timeoutMs: Int = 2500): Boolean =
        withContext(Dispatchers.IO) {
            try {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress(host, port), timeoutMs)
                    true
                }
            } catch (_: Exception) {
                false
            }
        }

    private data class RemoteInfo(val host: String, val port: Int, val protocol: String)

    private fun extractRemoteInfo(ovpnConfig: String, fallbackIp: String): RemoteInfo {
        var host = fallbackIp
        var port = 443
        var protocol = "tcp"
        for (rawLine in ovpnConfig.lineSequence()) {
            val line = rawLine.trim()
            if (line.isEmpty() || line.startsWith("#") || line.startsWith(";")) continue
            if (line.startsWith("proto ")) {
                val p = line.substringAfter("proto ").trim().lowercase()
                if (p.startsWith("udp")) protocol = "udp"
                else if (p.startsWith("tcp")) protocol = "tcp"
            } else if (line.startsWith("remote ")) {
                val parts = line.split(Regex("\\s+"))
                if (parts.size >= 2) host = parts[1].trim()
                if (parts.size >= 3) port = parts[2].trim().toIntOrNull() ?: port
            }
        }
        return RemoteInfo(host = host, port = port, protocol = protocol)
    }

    companion object {
        private const val KEY_CSV = "cached_csv"
        private const val KEY_TIMESTAMP = "cached_ts"

        fun isBlocked219Ip(ip: String): Boolean {
            return ip.trim().startsWith("219.")
        }
    }
}
