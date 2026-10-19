package net.onebell.mcs

import android.Manifest
import android.app.role.RoleManager
import android.content.Intent
import android.content.ActivityNotFoundException
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.net.toUri
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.collectLatest
import androidx.compose.material3.*
import androidx.compose.foundation.layout.*
import androidx.compose.ui.Modifier
import net.onebell.mcs.call.CallAccess
import net.onebell.mcs.ui.settings.*
import net.onebell.mcs.ui.theme.MCSTheme

class MainActivity : ComponentActivity() {
    private lateinit var viewModel: SettingsViewModel
    private val roleRequest = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        val request = viewModel.setupFlow.externalResult(SetupStep.ROLE) ?: return@registerForActivityResult
        viewModel.refreshAccess()
        if (CallAccess.hasRole(this)) viewModel.setupFlow.move(request, SetupStep.PERMISSIONS)
        else { viewModel.setupFlow.complete(request); viewModel.message("Call Screening 역할 요청이 취소되었습니다.") }
    }
    private val permissionRequest = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        viewModel.setupFlow.externalResult(SetupStep.PERMISSIONS)?.let(viewModel::permissionsFinished)
    }
    private val batteryRequest = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        val step = viewModel.setupFlow.current?.step ?: return@registerForActivityResult
        if (step == SetupStep.BATTERY || step == SetupStep.SAMSUNG) viewModel.batterySettingsReturned(step)
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        viewModel = ViewModelProvider(this)[SettingsViewModel::class.java]
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.RESUMED) {
                viewModel.setupRequests.collect { request ->
                    if (request != null && !request.waiting && !request.cancelled) advanceSetup(request)
                }
            }
        }
        enableEdgeToEdge()
        setContent {
            val state by viewModel.state.collectAsStateWithLifecycle()
            var selectedTab by rememberSaveable { mutableIntStateOf(0) }
            val snackbar = remember { SnackbarHostState() }
            LaunchedEffect(viewModel) {
                lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
                    viewModel.messages.collectLatest { snackbar.showSnackbar(it, duration = SnackbarDuration.Short) }
                }
            }
            MCSTheme {
                Scaffold(snackbarHost = { SnackbarHost(snackbar) }) { padding ->
                    Column(Modifier.padding(padding)) {
                        PrimaryTabRow(selectedTabIndex = selectedTab) {
                            Tab(selected = selectedTab == 0, onClick = { selectedTab = 0 },
                                text = { Text("전화 수신 알림 전달") })
                            Tab(selected = selectedTab == 1, onClick = { selectedTab = 1 },
                                text = { Text("로그") })
                        }
                        Box(Modifier.weight(1f)) {
                            if (selectedTab == 0) {
                                SettingsScreen(state, viewModel, viewModel::beginSetup, ::openAppSettings,
                                    { viewModel.openBatterySettings() }, { viewModel.openBatterySettings(samsung = true) })
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
    private fun advanceSetup(request: SetupRequest) {
        if (viewModel.setupFlow.current != request) return
        when (request.step) {
            SetupStep.ROLE -> requestRoleForPending(request)
            SetupStep.PERMISSIONS -> requestPermissionsForPending(request)
            SetupStep.BATTERY_CHECK -> viewModel.prepareBattery(request)
            SetupStep.BATTERY_CHECKING -> Unit
            SetupStep.BATTERY, SetupStep.SAMSUNG -> openBatterySettings(request)
            SetupStep.EXECUTE -> viewModel.finishSetup(request)
        }
    }
    private fun requestRoleForPending(request: SetupRequest) {
        if (request.action == SettingsAction.START && !CallAccess.hasRole(this)) {
            val manager = getSystemService(RoleManager::class.java)
            if (!manager.isRoleAvailable(RoleManager.ROLE_CALL_SCREENING)) {
                viewModel.setupFlow.complete(request)
                viewModel.message("이 기기는 Call Screening 역할을 지원하지 않습니다.")
                return
            }
            val waiting = viewModel.setupFlow.awaitExternal(request) ?: return
            try { roleRequest.launch(manager.createRequestRoleIntent(RoleManager.ROLE_CALL_SCREENING)) }
            catch (_: ActivityNotFoundException) { setupFailed(waiting) }
            catch (_: SecurityException) { setupFailed(waiting) }
        } else viewModel.setupFlow.move(request, SetupStep.PERMISSIONS)
    }
    private fun requestPermissionsForPending(request: SetupRequest) {
        val permissions = mutableListOf<String>()
        if (request.action == SettingsAction.START && !CallAccess.hasContacts(this))
            permissions += Manifest.permission.READ_CONTACTS
        if (!CallAccess.hasNetworkPermission(this)) permissions += CallAccess.LOCAL_NETWORK
        if (permissions.isEmpty()) viewModel.permissionsFinished(request)
        else {
            val waiting = viewModel.setupFlow.awaitExternal(request) ?: return
            try { permissionRequest.launch(permissions.toTypedArray()) }
            catch (_: ActivityNotFoundException) { setupFailed(waiting) }
            catch (_: SecurityException) { setupFailed(waiting) }
        }
    }
    private fun setupFailed(request: SetupRequest) {
        viewModel.setupFlow.complete(request)
        viewModel.message(getString(R.string.setup_screen_unavailable))
    }
    private fun openBatterySettings(request: SetupRequest) {
        val ignored = BatterySettings.optimizationIgnored(this)
        if (request.action == SettingsAction.START && ignored == true) {
            viewModel.setupFlow.move(request, SetupStep.EXECUTE)
            return
        }
        val waiting = viewModel.setupFlow.awaitExternal(request) ?: return
        val samsung = request.step == SetupStep.SAMSUNG
        val destination = launchFirstAvailable(batteryDestinations(samsung, ignored)) {
            batteryRequest.launch(BatterySettings.intent(this, it))
        }
        if (destination == null) {
            viewModel.setupWarning(listOfNotNull(getString(R.string.battery_settings_unavailable),
                if (samsung) getString(R.string.samsung_battery_manual_path) else null).joinToString("\n"))
            viewModel.batterySettingsReturned(waiting.step)
        } else if (samsung && destination != BatteryDestination.SAMSUNG_EXCEPTIONS) {
            viewModel.message(getString(R.string.samsung_battery_manual_path))
        }
    }
    private fun openAppSettings() {
        try { startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, "package:$packageName".toUri())) }
        catch (_: ActivityNotFoundException) { viewModel.message(getString(R.string.setup_screen_unavailable)) }
        catch (_: SecurityException) { viewModel.message(getString(R.string.setup_screen_unavailable)) }
    }
}
