package net.onebell.mcs.call

import android.Manifest
import android.app.role.RoleManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build

object CallAccess {
    const val LOCAL_NETWORK = "android.permission.ACCESS_LOCAL_NETWORK"
    fun hasRole(context: Context): Boolean =
        context.getSystemService(RoleManager::class.java).isRoleHeld(RoleManager.ROLE_CALL_SCREENING)
    fun hasContacts(context: Context) =
        context.checkSelfPermission(Manifest.permission.READ_CONTACTS) == PackageManager.PERMISSION_GRANTED
    fun hasNetworkPermission(context: Context) = Build.VERSION.SDK_INT < 37 ||
        context.checkSelfPermission(LOCAL_NETWORK) == PackageManager.PERMISSION_GRANTED
}
