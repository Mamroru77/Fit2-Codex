package com.codexquota.app.sync

import com.codexquota.app.alerts.AlertProcessingRepository
import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.domain.QuotaSnapshot
import com.codexquota.app.domain.alerts.AlertEvaluation
import java.time.Instant

/**
 * What happens to a snapshot that arrived over the live connection.
 *
 * The live service and the periodic worker both end up here, so a quota change is treated the same
 * way whichever mode produced it. The order is the point:
 *
 * 1. **the cache is written** — so the numbers on screen are the ones just received;
 * 2. **the alert rules are evaluated** — against the snapshot that was just stored;
 * 3. **the status notification is updated** — last, so it can never show a value the cache does not
 *    have.
 *
 * A snapshot that has already been processed is ignored: the Bridge re-sends its current state to a
 * client that reconnects, and evaluating it again would be evaluating the same moment twice.
 */
class LiveSyncCoordinator(
    private val repository: QuotaRepository,
    private val alerts: AlertProcessingRepository,
    private val clock: () -> Instant = Instant::now,
) {
    /** The last sequence this coordinator acted on, or `null` if it has not acted yet. */
    private var lastSequence: Long? = null

    /**
     * Applies one trusted snapshot.
     *
     * @param sequence the Bridge's sequence number, when the update came over the live connection.
     * @param snapshot the complete snapshot.
     * @return the evaluation, or `null` when the snapshot was a duplicate.
     */
    suspend fun onTrustedSnapshot(
        snapshot: QuotaSnapshot,
        sequence: Long? = null,
    ): AlertEvaluation? {
        if (sequence != null && sequence == lastSequence) {
            return null
        }

        lastSequence = sequence

        repository.applyRealtime(snapshot)

        val evaluation = alerts.process(snapshot, fresh = true)

        alerts.updateStatus(snapshot, clock(), stale = false)

        return evaluation
    }

    /**
     * Records that the connection ended.
     *
     * The cached values stay; only their label changes. The alert state is deliberately left alone so
     * that reconnecting does not replay a notification.
     */
    suspend fun onDisconnected() {
        lastSequence = null

        val cached = repository.current()

        alerts.updateStatus(cached.snapshot, cached.receivedAt, stale = true)
    }
}
