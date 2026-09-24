package com.codexquota.app.domain.alerts

import java.time.Duration

/** Which quota window an alert is about. */
enum class QuotaWindowKind {
    ShortWindow,
    Weekly,
}

/** The thresholds for one window. */
data class WindowRules(
    val warningPercent: Double = DEFAULT_WARNING,
    val criticalPercent: Double = DEFAULT_CRITICAL,
) {
    init {
        // The spec's rules, enforced where the values live so no caller can construct an impossible
        // pair: 1..99, and critical strictly below warning.
        require(warningPercent in VALID_RANGE) {
            "warningPercent must be within $VALID_RANGE but was $warningPercent"
        }
        require(criticalPercent in VALID_RANGE) {
            "criticalPercent must be within $VALID_RANGE but was $criticalPercent"
        }
        require(criticalPercent < warningPercent) {
            "criticalPercent ($criticalPercent) must be below warningPercent ($warningPercent)"
        }
    }

    companion object {
        /** The approved defaults. */
        const val DEFAULT_WARNING = 20.0
        const val DEFAULT_CRITICAL = 10.0

        /** The approved valid range, in percent. */
        val VALID_RANGE = 1.0..99.0
    }
}

/**
 * The repeated-reminder rule.
 *
 * It applies to the critical state only, is off by default, and its interval is measured from the
 * persisted UTC time of the last critical notification so it survives process recreation.
 */
data class RepeatRule(
    val enabled: Boolean = false,
    val interval: Duration = DEFAULT_INTERVAL,
) {
    init {
        require(interval > Duration.ZERO) { "The repeat interval must be positive." }
    }

    companion object {
        /** The default interval the settings screen offers. */
        val DEFAULT_INTERVAL: Duration = Duration.ofMinutes(60)

        /** The intervals the settings screen offers. */
        val OFFERED_INTERVALS: List<Duration> = listOf(
            Duration.ofMinutes(30),
            Duration.ofMinutes(60),
            Duration.ofMinutes(120),
        )
    }
}

/**
 * The alert configuration for this phone.
 *
 * Thresholds are deliberately phone-local rather than stored on the Bridge: a second phone may want
 * different ones, and the Bridge has no business knowing them.
 */
data class AlertRules(
    val shortWindow: WindowRules = WindowRules(),
    val weekly: WindowRules = WindowRules(),
    val repeatCritical: RepeatRule = RepeatRule(),
) {
    /** The rules for one window. */
    fun forWindow(window: QuotaWindowKind): WindowRules = when (window) {
        QuotaWindowKind.ShortWindow -> shortWindow
        QuotaWindowKind.Weekly -> weekly
    }
}
