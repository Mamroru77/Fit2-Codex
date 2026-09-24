package com.codexquota.app.ui.connection

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.codexquota.app.R

/**
 * The connection and pairing screen.
 *
 * Two flows are offered, and both require the user to see the Bridge's identity before anything is
 * sent: automatic discovery shows the verification code derived from the certificate the Bridge
 * presents, and the QR flow carries the fingerprint in the code itself.
 */
@Composable
fun ConnectionScreen(viewModel: ConnectionViewModel) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    var code by remember { mutableStateOf("") }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(text = stringResource(R.string.connection_title), style = MaterialTheme.typography.titleLarge)

        // Read once into a local: the state is a delegated property, so a null check on it does not
        // narrow its type for the compiler.
        val pairedName = state.pairedBridgeName

        if (pairedName != null) {
            Text(text = stringResource(R.string.connection_paired_with, pairedName))

            state.pairedIdentityCode?.let {
                Text(text = it, style = MaterialTheme.typography.bodyMedium)
            }

            OutlinedButton(onClick = viewModel::unpair) {
                Text(stringResource(R.string.connection_unpair))
            }
        } else {
            Text(text = stringResource(R.string.connection_not_paired))

            Button(onClick = viewModel::startDiscovery) {
                Text(stringResource(R.string.connection_searching))
            }
        }

        state.problem?.let { problem -> ProblemCard(problem) }

        state.candidates.forEach { candidate ->
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(text = stringResource(R.string.connection_found, candidate.serviceName))

                    Button(onClick = { viewModel.selectCandidate(candidate) }) {
                        Text(stringResource(R.string.connection_confirm))
                    }
                }
            }
        }

        // The identity is shown, and nothing is sent until the user says the codes match.
        state.pendingIdentity?.let { pending ->
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(
                        text = stringResource(
                            R.string.connection_confirm_identity,
                            pending.verificationCode,
                        ),
                    )

                    Button(onClick = viewModel::confirmPendingIdentity) {
                        Text(stringResource(R.string.connection_confirm))
                    }
                }
            }
        }

        OutlinedTextField(
            value = code,
            onValueChange = { code = it },
            label = { Text(stringResource(R.string.connection_qr_hint)) },
            modifier = Modifier.fillMaxWidth(),
        )

        Button(
            onClick = { viewModel.submitPairingCode(code) },
            enabled = code.isNotBlank(),
        ) {
            Text(stringResource(R.string.connection_pair_with_code))
        }

        if (state.lanPermission == com.codexquota.app.sync.LocalNetworkPermissionState.Required) {
            Text(
                text = stringResource(R.string.connection_lan_permission_required),
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

@Composable
private fun ProblemCard(problem: ConnectionProblem) {
    val message = when (problem.kind) {
        ConnectionProblem.Kind.Security -> stringResource(R.string.connection_security_explanation)
        ConnectionProblem.Kind.RepairRequired -> stringResource(R.string.connection_repair_required)
        ConnectionProblem.Kind.Rejected -> stringResource(R.string.connection_pairing_rejected)
        ConnectionProblem.Kind.Expired -> stringResource(R.string.connection_pairing_expired)
        ConnectionProblem.Kind.TimedOut -> stringResource(R.string.connection_pairing_timed_out)
        ConnectionProblem.Kind.InvalidCode -> problem.detail
            ?: stringResource(R.string.connection_pairing_expired)

        ConnectionProblem.Kind.Unreachable -> problem.detail
            ?: stringResource(R.string.connection_pairing_timed_out)
    }

    Card(modifier = Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(12.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Text(text = message, style = MaterialTheme.typography.bodyMedium)
        }
    }
}
