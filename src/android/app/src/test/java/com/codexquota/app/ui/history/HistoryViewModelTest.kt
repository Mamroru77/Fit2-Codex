package com.codexquota.app.ui.history

import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.domain.HistoryPoint
import com.codexquota.app.domain.QuotaEvent
import com.codexquota.app.domain.QuotaEventType
import com.codexquota.app.testing.FakeBridgeApi
import com.codexquota.app.testing.InMemoryQuotaCache
import java.time.Instant
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

/**
 * The history presentation.
 *
 * The interesting requirement is the gap: a period with no recorded sample must be drawn as a break,
 * never as a line joining the samples either side of it.
 */
class HistoryViewModelTest {

    private val zone = ZoneId.of("Asia/Shanghai")
    private val api = FakeBridgeApi()
    private val cache = InMemoryQuotaCache()

    private fun repository() = QuotaRepository(api, cache) { Instant.parse("2026-09-22T13:31:00Z") }

    private fun viewModel(repository: QuotaRepository, scope: kotlinx.coroutines.CoroutineScope) =
        HistoryViewModel(repository = repository, scope = scope, zone = zone)

    private fun at(hour: Int, minute: Int) = Instant.parse("2026-09-22T%02d:%02d:00Z".format(hour, minute))

    private suspend fun seed(
        points: List<HistoryPoint>,
        events: List<QuotaEvent> = emptyList(),
    ) {
        cache.replaceHistory(points)
        cache.replaceEvents(events)
    }

    @Test
    fun aMissingIntervalBecomesTwoSegmentsRatherThanOneLine() = runTest {
        // 10:00 and 10:05 are contiguous; the next sample is almost three hours later, so the app has
        // no idea what happened in between and must not draw it.
        seed(
            listOf(
                HistoryPoint(at(10, 0), 81.0, 60.0),
                HistoryPoint(at(10, 5), 80.0, 60.0),
                HistoryPoint(at(13, 0), 72.0, 54.0),
            ),
        )

        val vm = viewModel(repository(), backgroundScope)
        vm.reload()

        val chart = vm.state.value.chart

        assertEquals(2, chart.segments.size)
        assertEquals(listOf(2, 1), chart.segments.map { it.points.size })
        assertTrue(vm.state.value.hasGaps)
    }

    @Test
    fun aContiguousSeriesStaysOneSegment() = runTest {
        seed(
            listOf(
                HistoryPoint(at(10, 0), 81.0, 60.0),
                HistoryPoint(at(10, 5), 80.0, 60.0),
                HistoryPoint(at(10, 10), 79.0, 60.0),
            ),
        )

        val vm = viewModel(repository(), backgroundScope)
        vm.reload()

        assertEquals(1, vm.state.value.chart.segments.size)
        assertFalse(vm.state.value.hasGaps)
    }

    @Test
    fun theShortWindowAndWeeklySeriesAreSeparateViews() = runTest {
        seed(listOf(HistoryPoint(at(10, 0), 81.0, 60.0)))

        val vm = viewModel(repository(), backgroundScope)
        vm.reload()

        assertEquals(81.0, vm.state.value.chart.segments.single().points.single().percent)

        vm.selectSeries(HistorySeries.Weekly)

        assertEquals(HistorySeries.Weekly, vm.state.value.series)
        // One series at a time: switching replaces the plotted values rather than adding a second line.
        assertEquals(60.0, vm.state.value.chart.segments.single().points.single().percent)
        assertEquals(1, vm.state.value.chart.segments.size)
    }

    @Test
    fun theAxisCoversTheWholeSeries() = runTest {
        seed(
            listOf(
                HistoryPoint(at(10, 0), 81.0, 60.0),
                HistoryPoint(at(10, 5), 8.0, 54.0),
            ),
        )

        val vm = viewModel(repository(), backgroundScope)
        vm.reload()

        assertEquals(8.0, vm.state.value.chart.minPercent)
        assertEquals(81.0, vm.state.value.chart.maxPercent)
        assertEquals(at(10, 0), vm.state.value.chart.startsAt)
        assertEquals(at(10, 5), vm.state.value.chart.endsAt)
    }

    @Test
    fun technicalEventsNeverReachTheTimeline() = runTest {
        // The parser drops anything that is not a user-meaningful type, so the timeline can only
        // contain events a person would want to read.
        seed(
            points = listOf(HistoryPoint(at(10, 0), 81.0, 60.0)),
            events = listOf(
                QuotaEvent(QuotaEventType.WindowReset, at(10, 30), "short window reset"),
                QuotaEvent(QuotaEventType.BridgeStarted, at(9, 0), null),
            ),
        )

        val vm = viewModel(repository(), backgroundScope)
        vm.reload()

        val titles = vm.state.value.events.map { it.title }

        assertEquals(2, titles.size)
        assertTrue(titles.contains(QuotaEventType.WindowReset.name))
        assertFalse(titles.any { it.contains("Ping", ignoreCase = true) })
        assertFalse(titles.any { it.contains("Cleanup", ignoreCase = true) })
    }

    @Test
    fun anEmptyStoreShowsNothingRatherThanAnEmptyChart() = runTest {
        val vm = viewModel(repository(), backgroundScope)
        vm.reload()

        assertFalse(vm.state.value.hasHistory)
        assertTrue(vm.state.value.chart.isEmpty)
        assertEquals(0, vm.state.value.events.size)
    }

    @Test
    fun aSingleSampleDoesNotProduceAGap() = runTest {
        seed(listOf(HistoryPoint(at(10, 0), 81.0, 60.0)))

        val vm = viewModel(repository(), backgroundScope)
        vm.reload()

        assertEquals(1, vm.state.value.chart.segments.size)
        assertFalse(vm.state.value.hasGaps)
    }

    @Test
    fun theGapThresholdIsGenerousComparedWithTheBridgesOwnCadence() {
        // The Bridge writes at least every five minutes, so the break has to be well beyond that or a
        // slow sample would be drawn as a gap that never existed.
        assertTrue(HistoryViewModel.DEFAULT_GAP_THRESHOLD.toMinutes() >= 15)
    }

    @Test
    fun eventsAreShownInThePhonesOwnTimeZone() = runTest {
        seed(
            points = listOf(HistoryPoint(at(10, 0), 81.0, 60.0)),
            events = listOf(QuotaEvent(QuotaEventType.WindowReset, at(15, 42), null)),
        )

        val vm = viewModel(repository(), backgroundScope)
        vm.reload()

        // 15:42Z is 23:42 in Asia/Shanghai.
        assertEquals("23:42", vm.state.value.events.single().localTime)
    }
}
