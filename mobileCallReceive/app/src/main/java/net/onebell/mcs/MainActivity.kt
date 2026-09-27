package net.onebell.mcs

import android.Manifest
import android.app.role.RoleManager
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.material3.*
import androidx.compose.foundation.layout.*
import androidx.compose.ui.Modifier
import net.onebell.mcs.call.CallAccess
import net.onebell.mcs.ui.settings.*
import net.onebell.mcs.ui.theme.MCSTheme

class MainActivity : ComponentActivity() {
    private lateinit var viewModel: SettingsViewModel
    private val roleRequest = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        viewModel.refreshAccess()
        if (CallAccess.hasRole(this)) requestPermissionsForPending()
        else { viewModel.pendingAction = null; viewModel.message("Call Screening 역할 요청이 취소되었습니다.") }
    }
    private val permissionRequest = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        finishAction()
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        viewModel = ViewModelProvider(this)[SettingsViewModel::class.java]
        enableEdgeToEdge()
        setContent {
            val state by viewModel.state.collectAsStateWithLifecycle()
            var selectedTab by rememberSaveable { mutableIntStateOf(0) }
            MCSTheme {
                Scaffold { padding ->
                    Column(Modifier.padding(padding)) {
                        PrimaryTabRow(selectedTabIndex = selectedTab) {
                            Tab(selected = selectedTab == 0, onClick = { selectedTab = 0 },
                                text = { Text("전화 수신 알림 전달") })
                            Tab(selected = selectedTab == 1, onClick = { selectedTab = 1 },
                                text = { Text("로그") })
                        }
                        Box(Modifier.weight(1f)) {
                            if (selectedTab == 0) {
                                SettingsScreen(state, viewModel, ::beginAction) {
                                    startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
                                }
                            } else DeliveryLogScreen(state)
                        }
                    }
                }
            }
        }
    }
    override fun onResume() {
        super.onResume()
        if (::viewModel.isInitialized) viewModel.refreshAccess()
    }
    private fun beginAction(action: SettingsAction) {
        if (!viewModel.canRun() || viewModel.pendingAction != null) return
        viewModel.pendingAction = action
        if (action == SettingsAction.START && !CallAccess.hasRole(this)) {
            val manager = getSystemService(RoleManager::class.java)
            if (!manager.isRoleAvailable(RoleManager.ROLE_CALL_SCREENING)) {
                viewModel.pendingAction = null
                viewModel.message("이 기기는 Call Screening 역할을 지원하지 않습니다.")
                return
            }
            roleRequest.launch(manager.createRequestRoleIntent(RoleManager.ROLE_CALL_SCREENING))
        } else requestPermissionsForPending()
    }
    private fun requestPermissionsForPending() {
        val permissions = mutableListOf<String>()
        if (viewModel.pendingAction == SettingsAction.START && !CallAccess.hasContacts(this))
            permissions += Manifest.permission.READ_CONTACTS
        if (!CallAccess.hasNetworkPermission(this)) permissions += CallAccess.LOCAL_NETWORK
        if (permissions.isEmpty()) finishAction() else permissionRequest.launch(permissions.toTypedArray())
    }
    private fun finishAction() {
        val action = viewModel.pendingAction ?: return
        viewModel.pendingAction = null
        viewModel.execute(action)
    }
}
