package com.codexquota.app.ui.history

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.codexquota.app.R

/**
 * The 24-hour history and event timeline.
 *
 * The two windows are separate views rather than two overlaid lines, because overlaid lines are hard
 * to read on a phone and the spec asks for one series at a time.
 */
@Composable
fun HistoryScreen(viewModel: HistoryViewModel) {
    val state by viewModel.state.collectAsStateWithLifecycle()

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(text = stringResource(R.string.history_title), style = MaterialTheme.typography.titleLarge)

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(
                selected = state.series == HistorySeries.ShortWindow,
                onClick = { viewModel.selectSeries(HistorySeries.ShortWindow) },
                label = { Text(stringResource(R.string.history_short_window)) },
            )

            FilterChip(
                selected = state.series == HistorySeries.Weekly,
                onClick = { viewModel.selectSeries(HistorySeries.Weekly) },
                label = { Text(stringResource(R.string.history_weekly)) },
            )
        }

        if (!state.hasHistory) {
            Text(text = stringResource(R.string.history_no_data))
            return@Column
        }

        QuotaChart(model = state.chart)

        // A gap is stated, not hidden: the chart draws it and the caption explains it.
        if (state.hasGaps) {
            Text(text = stringResource(R.string.history_gaps), style = MaterialTheme.typography.bodySmall)
        }

        if (state.stale) {
            Text(text = stringResource(R.string.history_offline), style = MaterialTheme.typography.bodySmall)
        }

        Text(text = stringResource(R.string.history_events), style = MaterialTheme.typography.titleMedium)

        if (state.events.isEmpty()) {
            Text(text = stringResource(R.string.history_no_events), style = MaterialTheme.typography.bodySmall)
        } else {
            state.events.forEach { event -> EventCard(event) }
        }
    }
}

@Composable
private fun EventCard(event: EventRow) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(12.dp),
            verticalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            Text(text = "${event.localTime}  ${event.title}", style = MaterialTheme.typography.bodyMedium)
            event.detail?.let { Text(text = it, style = MaterialTheme.typography.bodySmall) }
        }
    }
}
