package net.onebell.mcs.network

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import net.onebell.mcs.data.NetworkSnapshot

fun networkSnapshot(context: Context): NetworkSnapshot = try {
    val manager = context.getSystemService(ConnectivityManager::class.java)
    val network = manager.activeNetwork
    val capabilities = manager.getNetworkCapabilities(network)
    val transports = listOf(NetworkCapabilities.TRANSPORT_WIFI to "Wi-Fi",
        NetworkCapabilities.TRANSPORT_CELLULAR to "모바일 데이터", NetworkCapabilities.TRANSPORT_VPN to "VPN",
        NetworkCapabilities.TRANSPORT_ETHERNET to "Ethernet")
        .filter { capabilities?.hasTransport(it.first) == true }.map { it.second }
    NetworkSnapshot(transports.joinToString(" / ").ifBlank { "연결 없음/미확인" })
} catch (_: Exception) { NetworkSnapshot() }
