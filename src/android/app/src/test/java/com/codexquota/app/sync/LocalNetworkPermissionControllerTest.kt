package com.codexquota.app.sync

import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * The LAN permission is a platform-version question, so the answer has to be right on both sides of
 * API 37 rather than only on the device the developer happens to own.
 */
class LocalNetworkPermissionControllerTest {

    @Test
    fun belowApi37NothingIsRequired() {
        // The permission does not exist yet, so reporting "required" would send the user to a prompt
        // that cannot grant anything.
        val controller = LocalNetworkPermissionController(sdkInt = 36, hasPermission = { false })

        assertEquals(LocalNetworkPermissionState.NotRequired, controller.state.value)
    }

    @Test
    fun atApi37WithoutAGrantThePermissionIsRequired() {
        val controller = LocalNetworkPermissionController(sdkInt = 37, hasPermission = { false })

        assertEquals(LocalNetworkPermissionState.Required, controller.state.value)
    }

    @Test
    fun atApi37WithAGrantItIsGranted() {
        val controller = LocalNetworkPermissionController(sdkInt = 37, hasPermission = { true })

        assertEquals(LocalNetworkPermissionState.Granted, controller.state.value)
    }

    @Test
    fun aboveApi37BehavesLikeApi37() {
        val required = LocalNetworkPermissionController(sdkInt = 40, hasPermission = { false })
        val granted = LocalNetworkPermissionController(sdkInt = 40, hasPermission = { true })

        assertEquals(LocalNetworkPermissionState.Required, required.state.value)
        assertEquals(LocalNetworkPermissionState.Granted, granted.state.value)
    }

    @Test
    fun refreshingPicksUpAGrantThatHappenedElsewhere() {
        // The user can grant from the system dialog or from Settings; either way the controller has
        // to be able to catch up without being recreated.
        var granted = false
        val controller = LocalNetworkPermissionController(sdkInt = 37, hasPermission = { granted })

        assertEquals(LocalNetworkPermissionState.Required, controller.state.value)

        granted = true
        controller.refresh()

        assertEquals(LocalNetworkPermissionState.Granted, controller.state.value)
    }

    @Test
    fun thePermissionStringMatchesThePlatformName() {
        // A typo here would silently request nothing and be reported as "denied" forever.
        assertEquals("android.permission.ACCESS_LOCAL_NETWORK", LocalNetworkPermissionController.PERMISSION)
    }
}
