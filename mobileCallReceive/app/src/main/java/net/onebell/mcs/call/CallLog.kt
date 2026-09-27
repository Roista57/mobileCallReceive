package net.onebell.mcs.call

import android.util.Log
import net.onebell.mcs.BuildConfig

object CallLog {
    fun event(tag: String, detail: String = "") { Log.i("MCS", "$tag $detail") }
    fun number(number: String) { event("CALL_NUMBER", if (BuildConfig.DEBUG) number else "[REDACTED]") }
}
