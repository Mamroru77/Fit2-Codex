# Stage C — Android Data and UI Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an Android app that securely discovers/pairs to the Stage B Bridge, verifies its stable identity before credentials, consumes REST/WSS v1, caches trusted data, handles reconnect/stale states, and presents Dashboard/History/Connection UI without background-alert behavior yet.

**Architecture:** OkHttp owns HTTPS/WSS; pairing uses a bootstrap fingerprint verifier and then stores a trusted Bridge CA/identity plus device credential through Keystore-backed storage. Repositories feed a single `ConnectionState` state machine and Room cache; Compose only observes ViewModel state.

**Tech Stack:** Kotlin, Jetpack Compose, Android SDK compile/target API 37 with runtime guards for older devices, minSdk 29, OkHttp, Kotlin coroutines/Flow, Room, DataStore, WorkManager dependency present but not used until Stage D, JUnit, MockWebServer, Robolectric where suitable.

**Spec:** `docs/superpowers/specs/2026-09-22-codex-quota-bridge-design.md`

## Global Constraints

- Package name: `com.codexquota.app`.
- Android networking never uses cleartext HTTP.
- Identity is verified before a stored device token is attached to any request.
- Unknown optional JSON fields are ignored; missing required quota fields become protocol errors and never `0%`.
- Connection state is represented by one `StateFlow<ConnectionState>` with the approved states.
- Compose/ViewModels do not own sockets, WorkManager, or NotificationManager.
- Stage C does not implement low-quota alerts or foreground/background scheduling; it supports foreground app sync and live WebSocket while the app/session is active.
- `LocalNetworkPermissionController` exists now; it requests `ACCESS_LOCAL_NETWORK` on API 37+ and returns granted/no-op on lower APIs.

## Review Focus

1. Pairing to a host with a different fingerprint than QR must fail before sending pairing completion secrets/device credentials.
2. A REST payload missing `remainingPercent` must surface a protocol error and preserve previous trusted cache.
3. A WebSocket `sequence` gap must force REST reconciliation and replace current state.
4. Wi-Fi → cellular in LAN-only V1 must produce `OFFLINE_CACHED`, not repeatedly dial a private IP.
5. App recreation must load cached data as stale/cached until a fresh connection proves it current.

---

### Task 1: Scaffold Android project and domain models

**Files:**
- Create: `src/android/settings.gradle.kts`
- Create: `src/android/build.gradle.kts`
- Create: `src/android/gradle.properties`
- Create: `src/android/app/build.gradle.kts`
- Create: `src/android/app/src/main/AndroidManifest.xml`
- Create: `src/android/app/src/main/java/com/codexquota/app/domain/QuotaModels.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/domain/ConnectionState.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/domain/QuotaModelsTest.kt`

**Interfaces:**
- Produces immutable `QuotaWindow`, `QuotaSnapshot`, `HistoryPoint`, `QuotaEvent`.
- Produces sealed `ConnectionState`: `Unpaired`, `Discovering`, `Connecting`, `Connected`, `Reconnecting`, `OfflineCached`, `AuthRequired`, `RepairRequired`, `SecurityError`.

- [ ] **Step 1: Write domain invariant tests**

```kotlin
@Test
fun quotaWindowRejectsOutOfRangeRemaining() {
    assertFailsWith<IllegalArgumentException> {
        QuotaWindow(usedPercent = 10.0, remainingPercent = 101.0, windowMinutes = 300, resetsAt = Instant.DISTANT_FUTURE)
    }
}
```

Also test valid 0/100 boundaries and all `ConnectionState` variants are serializable only where explicitly needed; do not persist transient `Connecting` directly.

- [ ] **Step 2: Run unit tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.domain.*"
```

Expected: FAIL before models exist.

- [ ] **Step 3: Create project and minimal domain implementation**

Configure Java/Kotlin 17, Compose, minSdk 29, compileSdk/targetSdk 37. Manifest includes `INTERNET`, `CHANGE_WIFI_MULTICAST_STATE`, and future `ACCESS_LOCAL_NETWORK`; do not add foreground-service permissions until Stage D.

- [ ] **Step 4: Run domain tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.domain.*"
```

Expected: PASS.

- [ ] **Step 5: Commit Android scaffold**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): scaffold app and quota domain models"
```

---

### Task 2: Add v1 JSON parsing and shared contract fixture tests

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/data/api/V1Dtos.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/api/V1Mapper.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/api/ProtocolException.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/data/api/V1ContractFixtureTest.kt`

**Interfaces:**
- Produces: `fun QuotaResponseDto.toDomain(): QuotaSnapshot`.
- Produces: `ProtocolException(code = "DATA_PROTOCOL_ERROR")` for missing/invalid required data.

- [ ] **Step 1: Copy/read the root contract fixtures in tests and write parser assertions**

Configure the unit-test source to read `../../../contracts/v1` relative to the Android project or copy fixtures during Gradle test resources. Assert every approved fixture parses; inject `futureUnknownField` and assert success; delete `remainingPercent` and assert `ProtocolException`, not zero/default.

- [ ] **Step 2: Run fixture tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.api.V1ContractFixtureTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement strict-required/lenient-unknown DTO mapping**

Use kotlinx serialization or Moshi consistently; configure unknown-key ignore but make required properties non-null/no default. Validate schema version `1` and percentage ranges before building domain objects.

- [ ] **Step 4: Run fixture tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.api.V1ContractFixtureTest"
```

Expected: PASS.

- [ ] **Step 5: Commit protocol parser**

```powershell
 cd ../..
 git add src/android contracts
 git commit -m "feat(android): consume bridge api v1 contracts safely"
```

---

### Task 3: Implement secure paired-Bridge storage and local-network permission controller

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/data/security/PairedBridge.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/security/PairedBridgeStore.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/security/KeystoreSecretBox.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/sync/LocalNetworkPermissionController.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/data/security/PairedBridgeStoreTest.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/sync/LocalNetworkPermissionControllerTest.kt`

**Interfaces:**
- `PairedBridge` stores `bridgeId`, display name, identity CA/public cert bytes, SPKI fingerprint, last endpoint, encrypted device credential.
- `LocalNetworkPermissionController.state(): StateFlow<LocalNetworkPermissionState>` and `request(activity)`.

- [ ] **Step 1: Write storage/permission tests**

Assert persisted preference/file content never contains the plaintext sample credential `super-secret-device-token`. For permission controller: SDK <37 returns `NotRequired`; SDK 37 without grant returns `Required`; granted returns `Granted`.

- [ ] **Step 2: Run tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.security.*" --tests "com.codexquota.app.sync.LocalNetworkPermissionControllerTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement Keystore-backed secret protection**

Generate an AES-GCM key in Android Keystore under alias `codex_quota_device_secret_v1`. Persist only ciphertext/IV plus non-secret Bridge metadata. Implement API 37 runtime permission branch and lower-API no-op branch.

- [ ] **Step 4: Run tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.security.*" --tests "com.codexquota.app.sync.LocalNetworkPermissionControllerTest"
```

Expected: PASS.

- [ ] **Step 5: Commit secure storage/permission layer**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): protect paired bridge credentials"
```

---

### Task 4: Implement mDNS discovery, QR parsing, fingerprint bootstrap, and pairing

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/data/discovery/BridgeDiscovery.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/discovery/NsdBridgeDiscovery.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/pairing/PairingQrPayload.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/pairing/PairingClient.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/security/BootstrapFingerprintTrustManager.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/security/PairedBridgeTrustManagerFactory.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/data/pairing/PairingClientTest.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/data/security/BootstrapFingerprintTrustManagerTest.kt`

**Interfaces:**
- Discover `_codexquota._tcp` services.
- QR parser accepts only version `1` and mandatory `bridgeId`, `host`, `port`, `pairingId`, `identityFingerprint`.
- Automatic discovery derives and displays the same four-group Bridge identity verification code from the Bridge identity CA in the presented chain and requires explicit user confirmation before any pairing request.
- QR bootstrap uses the `identityFingerprint` in the QR; discovery bootstrap uses the user-confirmed identity code. In both modes, validate certificate validity, chain signatures/basic constraints, hostname/SAN, and that the Bridge identity CA SPKI SHA-256 matches the expected fingerprint; never compare the rotating leaf SPKI to the Bridge identity fingerprint.
- Discovery flow calls `pairing/request`; QR flow calls `pairing/claim`; both display the returned six-digit session verification code, wait for Windows local approval via `pairing/status`, then call `pairing/complete`.
- After completion stores trusted identity and issued device credential.

- [ ] **Step 1: Write fingerprint-before-secret tests**

Use MockWebServer/held test chains with a stable identity CA and rotating leaf. For matching QR identity fingerprint, claim remains pending until a mocked Windows-local approval status is observed, then completion stores the credential. For mismatched identity fingerprint or invalid chain/hostname, assert no pairing request/claim/complete body and no device Bearer credential is ever sent. For discovery flow, assert the code derived from the identity CA matches the Windows-side fixture code before `confirmDiscoveredIdentity()` permits `pairing/request`; without confirmation, no request/trust/credential is stored. Assert a pending session does not complete, rejected maps to an explicit pairing-rejected UI state, and expired maps to `PAIRING_EXPIRED`.

- [ ] **Step 2: Run pairing tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.pairing.*" --tests "com.codexquota.app.data.security.BootstrapFingerprintTrustManagerTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement discovery/pairing trust bootstrap**

Use `NsdManager` for discovery. The bootstrap verifier validates the presented leaf-to-identity-CA chain and checks the expected Bridge identity CA SPKI fingerprint for the user-confirmed target; it is not a global trust-all verifier. Keep OkHttp's normal hostname verification semantics against the leaf SAN. Discovery mode sends `pairing/request` only after identity confirmation; QR mode sends `pairing/claim` only after QR fingerprint validation. Poll `pairing/status` with bounded backoff while the Windows tray is awaiting `Allow`; only an approved session may call `pairing/complete`. After completion, build an `SSLContext` from a KeyStore containing only the paired Bridge identity CA and use the normal hostname verifier.

- [ ] **Step 4: Run pairing/security tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.pairing.*" --tests "com.codexquota.app.data.security.*"
```

Expected: PASS.

- [ ] **Step 5: Commit discovery/pairing**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): securely discover and pair with bridge"
```

---

### Task 5: Add authenticated REST client, Room cache, and stale semantics

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/data/api/BridgeApi.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/api/OkHttpBridgeApi.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/db/AppDatabase.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/db/Entities.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/db/QuotaDao.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/QuotaRepository.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/data/QuotaRepositoryTest.kt`

**Interfaces:**
- `suspend fun fetchQuota(): QuotaSnapshot`
- `suspend fun fetchHistory(hours: Int = 24): List<HistoryPoint>`
- `suspend fun fetchEvents(hours: Int = 24): List<QuotaEvent>`
- `Flow<CachedQuotaState>` exposes trusted snapshot plus freshness metadata.

- [ ] **Step 1: Write repository tests**

Assert fresh REST success updates cache/history; network failure after previous success returns cached values marked stale; protocol error preserves prior trusted cache; `DEVICE_UNAUTHORIZED` maps to `RepairRequired`; `CODEX_AUTH_REQUIRED` maps to source `AuthRequired` without discarding cached quota. Close and recreate the repository/Room database after a successful sync and assert the restored snapshot is shown as cached/stale until a new network refresh succeeds.

- [ ] **Step 2: Run repository tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.QuotaRepositoryTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement authenticated client and Room cache**

Attach Bearer credential only in an OkHttp interceptor used by a client already configured with the paired Bridge trust manager. Store latest snapshot separately from history/events; all timestamps are UTC instants. Mark stale based on failed refresh/connection status, not by inventing changed percentage data.

- [ ] **Step 4: Run repository tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.data.QuotaRepositoryTest"
```

Expected: PASS.

- [ ] **Step 5: Commit REST/cache**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): cache authenticated quota and history"
```

---

### Task 6: Implement WebSocket client and canonical connection state machine

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/data/ws/BridgeWebSocket.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/data/ws/OkHttpBridgeWebSocket.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/sync/ConnectionManager.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/sync/ReconnectBackoff.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/sync/ConnectionManagerTest.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/sync/ReconnectBackoffTest.kt`

**Interfaces:**
- `val state: StateFlow<ConnectionState>` is the single connection truth.
- Reconnect delays: 1s, 2s, 5s, 10s, 30s, 60s max with ±20% jitter.
- Sequence gap calls `QuotaRepository.refreshCurrent()` immediately.

- [ ] **Step 1: Write state transition and gap tests**

Cover `Connected → Reconnecting → Connected`; TLS mismatch → `SecurityError` with no retry; 401 credential rejection → `RepairRequired`; cellular-only → `OfflineCached`; network-changed event cancels current backoff and rediscovery starts immediately; sequence `100 → 102` calls exactly one REST reconciliation.

- [ ] **Step 2: Run state-machine tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.sync.ConnectionManagerTest" --tests "com.codexquota.app.sync.ReconnectBackoffTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement state machine and WebSocket adapter**

Use coroutine scopes owned by `ConnectionManager`, not ViewModel. Parse hello and quota frames; update repository only with complete validated snapshots. Listen to `ConnectivityManager` through an injected network-observer interface so tests can send deterministic network changes.

- [ ] **Step 4: Run sync tests**

```powershell
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.sync.*"
```

Expected: PASS.

- [ ] **Step 5: Commit live connection foundation**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): add bridge websocket connection state machine"
```

---

### Task 7: Build Dashboard and pairing/connection UI

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/AppNav.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/dashboard/DashboardScreen.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/dashboard/DashboardViewModel.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/connection/ConnectionScreen.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/connection/ConnectionViewModel.kt`
- Create: `src/android/app/src/main/res/values/strings.xml`
- Create: `src/android/app/src/main/res/values-zh-rCN/strings.xml`
- Create: `src/android/app/src/test/java/com/codexquota/app/ui/dashboard/DashboardViewModelTest.kt`

**Interfaces:**
- Dashboard shows remaining percent, optional used percent, reset time, and explicit textual connection/freshness state.
- Connection UI supports automatic discovery, QR payload input/scanner integration seam, disconnect/re-pair, and security-error explanation.

- [ ] **Step 1: Write ViewModel presentation tests**

Assert fresh `72/54` maps to labels `72%`/`54%` and real-time state; stale cache contains `Offline / cached` plus last-update text; `SecurityError` never formats as generic offline; `AuthRequired` says Codex login required while retaining last trusted numbers if available.

- [ ] **Step 2: Run presentation tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.ui.dashboard.DashboardViewModelTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement Compose screens using StateFlow only**

ViewModels consume repository/connection flows and expose immutable UI state. Use three bottom destinations (`Dashboard`, `History`, `Settings`). In Task 7 the History route renders a concrete static `Text(stringResource(R.string.history_available_after_sync))` screen; Task 8 replaces that route content with the real chart/event screen. No socket/network code belongs in UI.

- [ ] **Step 4: Run UI unit tests and Compose build**

```powershell
 .\gradlew.bat testDebugUnitTest assembleDebug
```

Expected: PASS.

- [ ] **Step 5: Commit Dashboard/connection UI**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): add dashboard and bridge connection ui"
```

---

### Task 8: Build 24-hour History/Event UI and Stage C gate

**Files:**
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/history/HistoryScreen.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/history/HistoryViewModel.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/history/QuotaChart.kt`
- Create: `src/android/app/src/main/java/com/codexquota/app/ui/settings/SettingsScreen.kt`
- Create: `src/android/app/src/androidTest/java/com/codexquota/app/StageCIntegrationTest.kt`
- Create: `src/android/app/src/test/java/com/codexquota/app/ui/history/HistoryViewModelTest.kt`

**Interfaces:**
- Separate short-window and weekly chart tabs.
- Chart segments break across missing-data gaps; do not interpolate across known gaps.
- Event list shows user-meaningful events only.

- [ ] **Step 1: Write history presentation tests**

Feed points at 10:00, 10:05, then 13:00 with an explicit missing interval; assert chart model contains two segments rather than one continuous segment. Assert short/weekly selection yields one series at a time. Assert technical event types are filtered from user timeline.

- [ ] **Step 2: Run history tests and verify failure**

```powershell
 cd src/android
 .\gradlew.bat testDebugUnitTest --tests "com.codexquota.app.ui.history.HistoryViewModelTest"
```

Expected: FAIL.

- [ ] **Step 3: Implement chart/event UI and integration test**

Use Compose Canvas for a minimal single-series line chart to avoid a heavy chart dependency. `StageCIntegrationTest` uses a local fake HTTPS Bridge with test identity to pair, fetch quota/history, receive one WebSocket update, then force disconnect and assert cached stale state.

- [ ] **Step 4: Run Stage C verification**

```powershell
 .\gradlew.bat testDebugUnitTest connectedDebugAndroidTest lintDebug assembleDebug
```

Expected: PASS on an attached emulator/device for `connectedDebugAndroidTest`.

- [ ] **Step 5: Commit and tag Stage C**

```powershell
 cd ../..
 git add src/android
 git commit -m "feat(android): complete secure quota data and ui foundation"
 git tag stage-c-android-foundation
```
