package com.codexquota.app.ui.history

import com.codexquota.app.data.QuotaRepository
import com.codexquota.app.domain.HistoryPoint
import com.codexquota.app.domain.QuotaEvent
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Which quota window the chart is showing. */
enum class HistorySeries {
    ShortWindow,
    Weekly,
}

/** One plotted sample. */
data class ChartPoint(
    val timestamp: Instant,
    val percent: Double,
)

/**
 * A run of samples that are contiguous in time.
 *
 * The chart is a list of these rather than one line, because a gap in the stored data is a real gap:
 * joining across it would draw usage that was never measured.
 */
data class ChartSegment(val points: List<ChartPoint>)

/** The drawable chart. */
data class ChartModel(
    val segments: List<ChartSegment>,
    val minPercent: Double,
    val maxPercent: Double,
    val startsAt: Instant?,
    val endsAt: Instant?,
) {
    /** Whether anything can be drawn. */
    val isEmpty: Boolean get() = segments.all { it.points.isEmpty() }

    companion object {
        /** An empty chart with a sane axis so the drawing code never divides by zero. */
        val Empty = ChartModel(emptyList(), 0.0, 100.0, null, null)
    }
}

/** One event, ready to display. */
data class EventRow(
    val title: String,
    val localTime: String,
    val detail: String?,
)

/** Everything the history screen draws. */
data class HistoryUiState(
    val series: HistorySeries,
    val chart: ChartModel,
    val events: List<EventRow>,
    val stale: Boolean,
    val hasHistory: Boolean,
    val hasGaps: Boolean,
)

/**
 * The history screen's state.
 *
 * The chart is built here rather than in Compose so that "a gap is two segments" is a tested fact
 * instead of a drawing detail.
 */
class HistoryViewModel(
    private val repository: QuotaRepository,
    private val scope: CoroutineScope,
    private val zone: ZoneId = ZoneId.systemDefault(),
    private val clockFormat: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm"),
    private val gapThreshold: Duration = DEFAULT_GAP_THRESHOLD,
) {
    private val _state = MutableStateFlow(
        HistoryUiState(
            series = HistorySeries.ShortWindow,
            chart = ChartModel.Empty,
            events = emptyList(),
            stale = true,
            hasHistory = false,
            hasGaps = false,
        ),
    )

    /** The history screen state. */
    val state: StateFlow<HistoryUiState> = _state.asStateFlow()

    /** Reads the stored history and events, and refreshes them from the Bridge. */
    fun refresh(hours: Int = 24) {
        scope.launch {
            repository.refreshHistory(hours)
            repository.refreshEvents(hours)
            reload()
        }
    }

    /** The stored samples, kept so switching series does not need another read. */
    private var storedPoints: List<HistoryPoint> = emptyList()

    /** Switches the chart between the short window and the weekly window. */
    fun selectSeries(series: HistorySeries) {
        val chart = buildChart(storedPoints.map { it.toChartPoint(series) })

        _state.value = _state.value.copy(
            series = series,
            chart = chart,
            hasGaps = chart.segments.size > 1,
        )
    }

    /** Re-reads what is stored, without going to the network. */
    suspend fun reload() {
        val points = repository.history()
        val events = repository.events()
        val cached = repository.current()

        storedPoints = points

        val chart = buildChart(points.map { it.toChartPoint(_state.value.series) })

        _state.value = _state.value.copy(
            chart = chart,
            events = events.map { it.toRow() },
            stale = cached.stale,
            hasHistory = points.isNotEmpty(),
            hasGaps = chart.segments.size > 1,
        )
    }

    /**
     * Splits a series into contiguous runs.
     *
     * A run breaks wherever consecutive samples are further apart than [gapThreshold]. The threshold
     * is deliberately generous relative to the Bridge's own five-minute persistence interval, so a
     * couple of late samples do not manufacture a gap that was never there.
     */
    private fun buildChart(points: List<ChartPoint>): ChartModel {
        if (points.isEmpty()) {
            return ChartModel.Empty
        }

        val sorted = points.sortedBy { it.timestamp }
        val segments = mutableListOf<ChartSegment>()
        var current = mutableListOf(sorted.first())

        for (index in 1 until sorted.size) {
            val previous = sorted[index - 1]
            val next = sorted[index]

            if (Duration.between(previous.timestamp, next.timestamp) > gapThreshold) {
                segments += ChartSegment(current)
                current = mutableListOf(next)
            } else {
                current += next
            }
        }

        segments += ChartSegment(current)

        return ChartModel(
            segments = segments,
            minPercent = sorted.minOf { it.percent },
            maxPercent = sorted.maxOf { it.percent },
            startsAt = sorted.first().timestamp,
            endsAt = sorted.last().timestamp,
        )
    }

    private fun HistoryPoint.toChartPoint(series: HistorySeries): ChartPoint = when (series) {
        HistorySeries.ShortWindow -> ChartPoint(timestamp, shortWindowRemainingPercent)
        HistorySeries.Weekly -> ChartPoint(timestamp, weeklyRemainingPercent)
    }

    private fun QuotaEvent.toRow(): EventRow = EventRow(
        title = type.name,
        localTime = clockFormat.format(occurredAt.atZone(zone)),
        detail = detail,
    )

    companion object {
        /**
         * Three times the Bridge's maximum persistence interval.
         *
         * The Bridge writes a sample at least every five minutes, so anything beyond a quarter of an
         * hour is missing data rather than a slow sample.
         */
        val DEFAULT_GAP_THRESHOLD: Duration = Duration.ofMinutes(15)
    }
}
