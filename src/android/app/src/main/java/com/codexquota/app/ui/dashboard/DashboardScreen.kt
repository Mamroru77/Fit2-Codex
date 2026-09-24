package com.codexquota.app.ui.dashboard

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Card
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.codexquota.app.R

/**
 * The dashboard.
 *
 * It draws one immutable state and nothing else: no socket, no worker, no notification manager. The
 * remaining percentage is the primary number, the used percentage is secondary, and the connection
 * state is always written out as text because a colour alone cannot say "security error".
 */
@Composable
fun DashboardScreen(viewModel: DashboardViewModel) {
    val state by viewModel.state.collectAsStateWithLifecycle()

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        QuotaCard(
            title = stringResource(R.string.dashboard_short_window),
            remaining = state.shortRemaining,
            used = state.shortUsed,
            resetsAt = state.shortResetsAt,
        )

        QuotaCard(
            title = stringResource(R.string.dashboard_weekly),
            remaining = state.weeklyRemaining,
            used = state.weeklyUsed,
            resetsAt = state.weeklyResetsAt,
        )

        ConnectionBanner(state)

        state.lastUpdated?.let {
            Text(text = it, style = MaterialTheme.typography.bodySmall)
        }
    }
}

@Composable
private fun QuotaCard(
    title: String,
    remaining: String,
    used: String?,
    resetsAt: String?,
) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Text(text = title, style = MaterialTheme.typography.titleMedium)

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.Bottom,
            ) {
                Text(
                    text = remaining,
                    style = MaterialTheme.typography.displaySmall,
                    textAlign = TextAlign.Start,
                )

                Text(
                    text = stringResource(R.string.dashboard_remaining),
                    style = MaterialTheme.typography.bodySmall,
                )
            }

            // The bar is driven by the same number as the label, so the two cannot disagree.
            LinearProgressIndicator(
                progress = { progressOf(remaining) },
                modifier = Modifier.fillMaxWidth(),
            )

            used?.let { Text(text = it, style = MaterialTheme.typography.bodySmall) }
            resetsAt?.let {
                Text(
                    text = stringResource(R.string.dashboard_resets_at, it),
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
    }
}

@Composable
private fun ConnectionBanner(state: DashboardUiState) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(text = state.connectionText, style = MaterialTheme.typography.titleSmall)

        // A security error gets an explanation, because the user has to decide whether they replaced
        // the Bridge themselves.
        if (state.freshness == Freshness.SecurityError) {
            Text(
                text = stringResource(R.string.connection_security_explanation),
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

/** Turns a rendered percentage back into a fraction for the bar. */
private fun progressOf(label: String): Float =
    label.removeSuffix("%").toFloatOrNull()?.div(100f)?.coerceIn(0f, 1f) ?: 0f
