package com.codexquota.app.sync

import android.app.Activity
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** What the platform currently requires of this app before it may talk to the LAN. */
enum class LocalNetworkPermissionState {
    /** The platform has no such permission, so nothing is required. */
    NotRequired,

    /** The platform has the permission and the user has not granted it yet. */
    Required,

    /** The permission is granted. */
    Granted,
}

/**
 * The LAN-permission state machine.
 *
 * `ACCESS_LOCAL_NETWORK` exists from API 37. On anything older the permission does not exist and
 * asking for it would be meaningless, so the controller reports [LocalNetworkPermissionState.NotRequired]
 * rather than pretending there is something to grant. The platform version and the permission check
 * are injected so this logic is a plain unit test rather than something that can only be observed on
 * one API level.
 */
class LocalNetworkPermissionController(
    private val sdkInt: Int = Build.VERSION.SDK_INT,
    private val hasPermission: () -> Boolean = { true },
) {
    private val _state = MutableStateFlow(evaluate())

    /** The current requirement. */
    val state: StateFlow<LocalNetworkPermissionState> = _state.asStateFlow()

    /** Re-reads the platform state, after returning from a permission prompt or from Settings. */
    fun refresh() {
        _state.value = evaluate()
    }

    private fun evaluate(): LocalNetworkPermissionState = when {
        sdkInt < API_WITH_LOCAL_NETWORK -> LocalNetworkPermissionState.NotRequired
        hasPermission() -> LocalNetworkPermissionState.Granted
        else -> LocalNetworkPermissionState.Required
    }

    companion object {
        /** The first API level that defines `ACCESS_LOCAL_NETWORK`. */
        const val API_WITH_LOCAL_NETWORK = 37

        /** The permission name, so callers do not repeat a string literal. */
        const val PERMISSION = "android.permission.ACCESS_LOCAL_NETWORK"
    }
}

/**
 * The Android adapter.
 *
 * It supplies the real platform version and permission check, and owns the runtime request. It adds
 * no policy of its own: everything decision-shaped lives in [LocalNetworkPermissionController].
 */
class AndroidLocalNetworkPermissionController(
    private val context: Context,
) {
    private val core = LocalNetworkPermissionController(
        sdkInt = Build.VERSION.SDK_INT,
        hasPermission = {
            ContextCompat.checkSelfPermission(context, LocalNetworkPermissionController.PERMISSION) ==
                PackageManager.PERMISSION_GRANTED
        },
    )

    /** The current requirement. */
    val state: StateFlow<LocalNetworkPermissionState> get() = core.state

    /** Re-reads the platform state. */
    fun refresh() = core.refresh()

    /**
     * Requests the permission, when there is one to request.
     *
     * On API levels below 37 this does nothing, which is the correct answer rather than a silent
     * failure: the app is not missing a permission it could have.
     */
    fun request(activity: Activity, requestCode: Int) {
        if (Build.VERSION.SDK_INT < LocalNetworkPermissionController.API_WITH_LOCAL_NETWORK) {
            return
        }

        ActivityCompat.requestPermissions(
            activity,
            arrayOf(LocalNetworkPermissionController.PERMISSION),
            requestCode,
        )
    }

    /** The permission string, exposed so the manifest and the request cannot disagree. */
    companion object {
        /** The manifest permission this controller drives. */
        const val MANIFEST_PERMISSION = LocalNetworkPermissionController.PERMISSION
    }
}
