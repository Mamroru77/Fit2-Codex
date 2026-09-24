package com.codexquota.app.notifications

import com.codexquota.app.data.settings.WatchFormat
import com.codexquota.app.domain.alerts.AlertAction
import com.codexquota.app.domain.alerts.AlertSeverity
import com.codexquota.app.domain.alerts.QuotaWindowKind
import com.codexquota.app.testing.snapshot
import java.time.Instant
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The notification text.
 *
 * These assertions are about strings because the requirement is about strings: the watch mirrors the
 * phone's notification, so "both percentages fit on one compact line" is the thing being verified,
 * not an implementation detail.
 */
class NotificationRendererTest {

    private val zone = ZoneId.of("Asia/Shanghai")
    private val generatedAt = Instant.parse("2026-09-22T13:30:00Z")

    private fun render(
        format: WatchFormat = WatchFormat.Compact,
        stale: Boolean = false,
        short: Double = 72.0,
        weekly: Double = 54.0,
    ) = StatusNotificationRenderer.render(
        snapshot = snapshot(shortRemaining = short, weeklyRemaining = weekly, generatedAt = generatedAt),
        receivedAt = generatedAt,
        stale = stale,
        format = format,
        zone = zone,
    )

    @Test
    fun theCompactFormCarriesBothPercentagesOnOneLine() {
        val model = render()

        assertEquals("Codex", model.title)
        assertEquals("5h 72% | W 54%", model.text)
        assertEquals("5h reset 23:42", model.subText)
    }

    @Test
    fun theChineseFormCarriesBothPercentagesOnOneLine() {
        val model = render(format = WatchFormat.Chinese)

        assertEquals("5小时 72% | 周 54%", model.text)
        assertEquals("23:42 重置", model.subText)
    }

    @Test
    fun aStaleStatusAlwaysSaysSoAndShowsWhenTheDataIsFrom() {
        val model = render(stale = true)

        assertTrue(model.text.startsWith("Offline | "), "the stale marker is mandatory")
        assertEquals("Offline | 5h 72% | W 54%", model.text)
        // 13:30Z is 21:30 in Asia/Shanghai, which is when the phone received it.
        assertEquals("data from 21:30", model.subText)
    }

    @Test
    fun theChineseStaleFormUsesTheLocalizedMarker() {
        val model = render(format = WatchFormat.Chinese, stale = true)

        assertTrue(model.text.contains("离线"))
        assertEquals("离线 | 5小时 72% | 周 54%", model.text)
        assertEquals("21:30 的数据", model.subText)
    }

    @Test
    fun anOrdinaryStatusUpdateIsNeverAnAlert() {
        val model = render()

        assertFalse(model.alerting)
        assertFalse(model.vibrate)
        assertTrue(model.ongoing)
        assertEquals(NotificationChannels.STATUS, model.channelId)
        assertEquals(NotificationChannels.STATUS_NOTIFICATION_ID, model.notificationId)
    }

    @Test
    fun aStatusUpdateAtCriticalLevelIsStillNotAnAlert() {
        // The status notification is not a substitute for the alerts channel, however low the quota.
        val model = render(short = 4.0, weekly = 3.0)

        assertFalse(model.alerting)
        assertEquals("5h 4% | W 3%", model.text)
    }

    @Test
    fun aWarningNamesTheRightWindowAndPercentage() {
        val model = AlertNotificationRenderer.render(
            AlertAction(QuotaWindowKind.ShortWindow, AlertSeverity.Warning, 18.0),
        )

        assertEquals("\u26A0 Codex", model.title)
        assertEquals("5h left 18%", model.text)
        assertTrue(model.alerting)
        assertTrue(model.vibrate)
        assertEquals(NotificationChannels.ALERTS, model.channelId)
    }

    @Test
    fun aCriticalAlertNamesTheRightWindowAndPercentage() {
        val model = AlertNotificationRenderer.render(
            AlertAction(QuotaWindowKind.ShortWindow, AlertSeverity.Critical, 8.0),
        )

        assertEquals("5h only 8% left", model.text)
    }

    @Test
    fun theWeeklyAlertUsesTheWeeklyLabel() {
        val model = AlertNotificationRenderer.render(
            AlertAction(QuotaWindowKind.Weekly, AlertSeverity.Critical, 8.0),
            WatchFormat.Chinese,
        )

        assertEquals("周 仅剩 8%", model.text)
    }

    @Test
    fun theChineseWarningMatchesTheSpecsExample() {
        val model = AlertNotificationRenderer.render(
            AlertAction(QuotaWindowKind.ShortWindow, AlertSeverity.Warning, 18.0),
            WatchFormat.Chinese,
        )

        assertEquals("5小时 剩余 18%", model.text)
    }

    @Test
    fun aWarningAndACriticalAboutTheSameWindowDoNotShareAnId() {
        // Sharing an id would make the critical silently replace the warning in the shade.
        val warning = AlertNotificationRenderer.render(
            AlertAction(QuotaWindowKind.ShortWindow, AlertSeverity.Warning, 18.0),
        )
        val critical = AlertNotificationRenderer.render(
            AlertAction(QuotaWindowKind.ShortWindow, AlertSeverity.Critical, 8.0),
        )

        assertTrue(warning.notificationId != critical.notificationId)
    }

    @Test
    fun theTwoWindowsDoNotShareAnId() {
        val short = AlertNotificationRenderer.render(
            AlertAction(QuotaWindowKind.ShortWindow, AlertSeverity.Critical, 8.0),
        )
        val weekly = AlertNotificationRenderer.render(
            AlertAction(QuotaWindowKind.Weekly, AlertSeverity.Critical, 8.0),
        )

        assertTrue(short.notificationId != weekly.notificationId)
    }

    @Test
    fun aRepeatIsMarkedAsSuch() {
        val model = AlertNotificationRenderer.render(
            AlertAction(QuotaWindowKind.ShortWindow, AlertSeverity.Critical, 8.0, isRepeat = true),
        )

        assertEquals("Still low", model.subText)
    }

    @Test
    fun aFractionalPercentageKeepsOneDecimal() {
        val model = render(short = 72.4)

        assertTrue(model.text.contains("72.4%"))
    }

    @Test
    fun withNoSnapshotTheStatusSaysItHasNothingRatherThanZero() {
        val model = StatusNotificationRenderer.render(
            snapshot = null,
            receivedAt = null,
            stale = true,
        )

        assertEquals("Offline", model.text)
        assertFalse(model.text.contains("0%"), "an absent snapshot must never render as 0%")
    }
}
