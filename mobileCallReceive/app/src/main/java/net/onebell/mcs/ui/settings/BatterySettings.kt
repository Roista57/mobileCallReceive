package net.onebell.mcs.ui.settings

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.core.net.toUri
import kotlinx.coroutines.CancellationException

internal enum class BatteryDestination { REQUEST_EXEMPTION, OPTIMIZATION_LIST, APP_DETAILS, SAMSUNG_EXCEPTIONS }
internal data class BatteryPromptDecision(val request: Boolean, val recordingFailed: Boolean = false)

internal suspend fun batteryPromptDecision(claim: suspend () -> Boolean, ignored: () -> Boolean?): BatteryPromptDecision {
    val first = try { claim() }
    catch (e: CancellationException) { throw e }
    catch (_: Exception) { return BatteryPromptDecision(false, recordingFailed = true) }
    return BatteryPromptDecision(first && ignored() != true)
}

internal fun batteryDestinations(samsung: Boolean, ignored: Boolean?): List<BatteryDestination> =
    if (samsung) listOf(BatteryDestination.SAMSUNG_EXCEPTIONS, BatteryDestination.APP_DETAILS)
    else buildList {
        if (ignored != true) add(BatteryDestination.REQUEST_EXEMPTION)
        add(BatteryDestination.OPTIMIZATION_LIST)
        add(BatteryDestination.APP_DETAILS)
    }

/** Try the next destination only when launch fails, never when the user declines or returns unchanged. */
internal fun <T> launchFirstAvailable(destinations: List<T>, launch: (T) -> Unit): T? {
    for (destination in destinations) {
        try { launch(destination); return destination }
        catch (_: ActivityNotFoundException) { }
        catch (_: SecurityException) { }
    }
    return null
}

internal object BatterySettings {
    val isSamsung get() = Build.MANUFACTURER.equals("samsung", ignoreCase = true)
    fun optimizationIgnored(context: Context): Boolean? = try {
        context.getSystemService(PowerManager::class.java)?.isIgnoringBatteryOptimizations(context.packageName)
    } catch (_: Exception) { null }

    fun intent(context: Context, destination: BatteryDestination): Intent = when (destination) {
        BatteryDestination.REQUEST_EXEMPTION -> Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
            "package:${context.packageName}".toUri())
        BatteryDestination.OPTIMIZATION_LIST -> Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
        BatteryDestination.APP_DETAILS -> Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
            "package:${context.packageName}".toUri())
        BatteryDestination.SAMSUNG_EXCEPTIONS -> Intent("com.samsung.android.sm.ACTION_OPEN_CHECKABLE_LISTACTIVITY")
            .setPackage("com.samsung.android.lool").putExtra("activity_type", 2)
    }
}
