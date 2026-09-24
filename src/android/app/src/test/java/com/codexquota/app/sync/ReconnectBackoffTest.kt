package com.codexquota.app.sync

import java.time.Duration
import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** The reconnect schedule is part of the approved design, so it is asserted exactly. */
class ReconnectBackoffTest {

    /** A random source that always returns the midpoint, so jitter is neutral and the schedule is exact. */
    private fun neutral() = ReconnectBackoff(
        jitterFraction = 0.0,
        random = Random(0),
    )

    @Test
    fun theScheduleIsOneTwoFiveTenThirtyThenSixty() {
        val backoff = neutral()

        val delays = (1..6).map { backoff.nextDelay() }

        assertEquals(
            listOf(1L, 2L, 5L, 10L, 30L, 60L),
            delays.map { it.toMillis() / 1000 },
        )
    }

    @Test
    fun theCeilingIsSixtySecondsForever() {
        val backoff = neutral()

        repeat(6) { backoff.nextDelay() }

        repeat(5) { assertEquals(Duration.ofSeconds(60), backoff.nextDelay()) }
    }

    @Test
    fun aStableConnectionResetsTheSchedule() {
        val backoff = neutral()

        backoff.nextDelay()
        backoff.nextDelay()
        backoff.nextDelay()
        assertEquals(3, backoff.attempt)

        backoff.reset()

        assertEquals(0, backoff.attempt)
        assertEquals(Duration.ofSeconds(1), backoff.nextDelay())
    }

    @Test
    fun jitterStaysWithinTwentyPercent() {
        val backoff = ReconnectBackoff(jitterFraction = 0.2, random = Random(1234))

        // Every attempt, including the capped ones, must stay inside ±20% of its scheduled delay.
        val expected = listOf(1000L, 2000L, 5000L, 10000L, 30000L, 60000L, 60000L, 60000L)

        expected.forEach { base ->
            val delay = backoff.nextDelay().toMillis()

            assertTrue(
                delay in (base * 0.8).toLong()..(base * 1.2).toLong(),
                "a delay of ${delay}ms is outside 20% of ${base}ms",
            )
        }
    }

    @Test
    fun anAttemptIsNeverImmediate() {
        // A zero delay would turn a failing reconnect into a busy loop, which the design forbids.
        val backoff = ReconnectBackoff(jitterFraction = 1.0, random = Random(7))

        repeat(20) {
            assertTrue(backoff.nextDelay().toMillis() >= 1L)
        }
    }
}
