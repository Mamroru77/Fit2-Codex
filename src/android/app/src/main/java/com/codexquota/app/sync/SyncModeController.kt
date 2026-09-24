package com.codexquota.app.sync

import com.codexquota.app.data.settings.AlertSettings
import com.codexquota.app.data.settings.current
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * The only owner of mode transitions.
 *
 * The invariant it exists to hold is mutual exclusion: live foreground sync and the periodic worker
 * must never run at the same time. It is enforced by ordering rather than by hoping, and the order
 * differs by direction on purpose:
 *
 * - entering a mode that does not use the worker **cancels the worker before starting the service**,
 *   so there is no instant where both could run;
 * - entering a mode that does not use the service **stops the service before enqueueing the worker**,
 *   for the same reason.
 *
 * Transitions are serialised with a mutex because preferences can change while a transition is in
 * flight, and two concurrent transitions could interleave into exactly the state the invariant
 * forbids.
 */
class SyncModeController(
    private val settings: AlertSettings,
    private val scheduler: SyncScheduler,
    private val scope: CoroutineScope,
    private val mutex: Mutex = Mutex(),
) {
    private val _mode = MutableStateFlow(SyncMode.Off)

    /** The mode currently applied. */
    val mode: StateFlow<SyncMode> = _mode.asStateFlow()

    /** The last mode this controller actually applied, or `null` if it has never run. */
    private var applied: SyncMode? = null

    /** Applies the mode that matches the current preferences, and keeps doing so as they change. */
    fun start() {
        settings.preferences
            .map { SyncMode.forPreferences(it) }
            .distinctUntilChanged()
            .onEach { applyMode(it) }
            .launchIn(scope)
    }

    /** Applies a mode directly. */
    suspend fun applyMode(target: SyncMode) = mutex.withLock {
        if (applied == target) {
            // Nothing to change. Re-applying the same mode must not enqueue a second worker.
            return@withLock
        }

        when (target) {
            SyncMode.Off -> {
                // Both off: leave nothing running at all.
                scheduler.stopLive()
                scheduler.cancelPeriodic()
            }

            SyncMode.Background -> {
                // The service first: the worker must never overlap a live connection.
                scheduler.stopLive()
                scheduler.enqueuePeriodic()
            }

            SyncMode.Live -> {
                // The worker first, for the same reason in the other direction.
                scheduler.cancelPeriodic()
                scheduler.startLive()
            }
        }

        applied = target
        _mode.value = target
    }

    /** Applies whatever the preferences currently say. */
    suspend fun applyCurrent() = applyMode(SyncMode.forPreferences(settings.current()))

    /**
     * Starts live sync because the user asked for it.
     *
     * This is the one transition that ignores the "already applied" shortcut, because its whole
     * purpose is to recover from a service that was stopped by the system or by the user.
     */
    suspend fun resumeLive() = mutex.withLock {
        scheduler.cancelPeriodic()
        scheduler.startLive()

        applied = SyncMode.Live
        _mode.value = SyncMode.Live
    }

    /** Whether the app currently believes live sync is running. */
    fun isLiveRunning(): Boolean = scheduler.isLiveRunning()
}
