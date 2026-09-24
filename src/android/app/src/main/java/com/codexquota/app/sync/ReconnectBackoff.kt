package com.codexquota.app.sync

import java.time.Duration
import kotlin.random.Random

/**
 * The delay before each reconnect attempt.
 *
 * The schedule is the approved one — 1s, 2s, 5s, 10s, 30s, then 60s forever — with ±20% jitter so
 * a Bridge that restarts does not have every phone in the house reconnect on the same tick.
 *
 * It is a plain class with an injected random source so the schedule can be asserted exactly rather
 * than statistically.
 */
class ReconnectBackoff(
    private val schedule: List<Duration> = DEFAULT_SCHEDULE,
    private val jitterFraction: Double = DEFAULT_JITTER,
    private val random: Random = Random.Default,
) {
    init {
        require(schedule.isNotEmpty()) { "The backoff schedule must not be empty." }
        require(jitterFraction in 0.0..1.0) { "Jitter must be a fraction between 0 and 1." }
    }

    /** How many attempts have been made since the last reset. */
    var attempt: Int = 0
        private set

    /**
     * The delay to wait before the next attempt, and advances the counter.
     *
     * Jitter is applied symmetrically around the scheduled delay, so the mean is the schedule and no
     * attempt is ever immediate.
     */
    fun nextDelay(): Duration {
        val base = schedule[minOf(attempt, schedule.size - 1)]
        attempt++

        if (jitterFraction == 0.0) {
            return base
        }

        val spread = base.toMillis() * jitterFraction
        val offset = (random.nextDouble() * 2.0 - 1.0) * spread

        return Duration.ofMillis((base.toMillis() + offset).toLong().coerceAtLeast(1L))
    }

    /**
     * Forgets the failures.
     *
     * Called once a connection is stable: a phone that reconnected successfully and drops again an
     * hour later should retry in a second, not in a minute.
     */
    fun reset() {
        attempt = 0
    }

    companion object {
        /** The approved schedule. The last entry is the ceiling. */
        val DEFAULT_SCHEDULE: List<Duration> = listOf(
            Duration.ofSeconds(1),
            Duration.ofSeconds(2),
            Duration.ofSeconds(5),
            Duration.ofSeconds(10),
            Duration.ofSeconds(30),
            Duration.ofSeconds(60),
        )

        /** ±20%. */
        const val DEFAULT_JITTER = 0.2
    }
}
